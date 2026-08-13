using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Storage;

namespace KOTU.App;

/// <summary>
/// 자체 탐색기 파일 조작 공용 로직 (A94 1차, v0.124.0) — 드래그 아웃 데이터 구성,
/// 드랍 이동/복사, 클립보드 복사/잘라내기/붙여넣기가 한곳에 모인다.
/// 세 표면(ExplorerPane 리스트 · ThumbnailExplorer 타일 · FileListOverlay 패널)이 같은 규칙을 쓴다.
///
/// 동작 결정(윈도우 관례): 같은 볼륨 = 이동, 다른 볼륨 = 복사. Ctrl 홀드 = 복사 강제,
/// Shift 홀드 = 이동 강제. 원본 볼륨은 앱 내부 드래그(다른 KOTU 창 포함)에서만 알 수 있다 —
/// DataPackage.Properties에 실어 보낸 경로 목록으로 판정하고, 외부(OS 탐색기 등) 소스는
/// 경로를 모르므로 기본 = 복사다(수정자로만 이동 강제 가능. docs/A94-matrix.md에 명기).
///
/// 실제 조작은 System.IO로 워커 스레드에서 한다(UI 블로킹 금지). WinRT StorageFolder에는
/// MoveAsync가 없어 폴더 이동을 못 하므로, 파일·폴더 모두 System.IO 한 경로로 통일하고
/// 이름 충돌은 NameCollisionOption.GenerateUniqueName과 같은 "이름 (2)" 규칙을 직접 구현했다
/// (대화상자 없음 — 1차 결정).
/// </summary>
internal static class ExplorerFileOps
{
    /// <summary>
    /// 앱 내부 드래그 식별용 원본 경로 목록 키 (DataPackage.Properties). 값은 '\n' 연결 문자열 —
    /// Properties는 프로세스 경계를 넘어야 하므로(다른 KOTU 창) 원시 문자열만 싣는다.
    /// 경로에 개행은 올 수 없어 구분자로 안전하다.
    /// </summary>
    private const string SourcePathsKey = "kotu.explorer.sourcePaths";

    /// <summary>조작 결과 집계. Skipped = 무동작 가드(같은 폴더로 이동 등)·소실 항목.</summary>
    internal sealed record OpResult(int Done, int Skipped, int Failed, string? FirstError)
    {
        internal static OpResult Empty { get; } = new(0, 0, 0, null);

        /// <summary>실패가 있을 때만 짧은 안내 문구 — 성공은 뷰 갱신이 곧 피드백이라 조용히 넘어간다.</summary>
        internal string? Notice(bool move) => Failed == 0
            ? null
            : $"{Failed} item(s) could not be {(move ? "moved" : "copied")} — {FirstError}";
    }

    /// <summary>Ctrl이 눌린 상태인지 — 클립보드 키 판정용(ExplorerPane의 Shift 판정과 같은 API).</summary>
    internal static bool IsCtrlDown() => Microsoft.UI.Input.InputKeyboardSource
        .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    // ---------- 드래그 아웃 (앱 → OS 탐색기 · 다른 KOTU 창 · 앱 내 다른 표면) ----------

    /// <summary>
    /// 드래그 데이터 구성: 선택 항목 전부(폴더 포함)를 StorageItems로 싣는다 — 이것으로 OS 탐색기
    /// 등 외부 대상으로의 드래그가 성립한다. 호출부는 컨테이너의 CanDrag + DragStarting에서
    /// args.GetDeferral()을 잡고 부른다(ListView CanDragItems의 DragItemsStarting은 await가 안 되고,
    /// UI 스레드에서 WinRT 동기 대기는 교착 위험이라 쓰지 않는다 — ExplorerPane.FetchThumbnail 주석 참고).
    /// 돌려주는 값이 false면 실을 항목이 없다는 뜻 — 호출부가 드래그를 취소한다.
    /// </summary>
    internal static async Task<bool> FillDragDataAsync(DataPackage data, IReadOnlyList<string> paths)
    {
        var items = await CollectStorageItemsAsync(paths);
        if (items.Count == 0) return false;

        data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        data.Properties[SourcePathsKey] = string.Join('\n', paths); // 볼륨 판정용(위 요약 참고)
        data.SetStorageItems(items, false); // 둘째 인자 readOnly=false: 이동 허용(기본 true는 대상이 이동 불가)
        return true;
    }

    // ---------- 드랍 인 (이동/복사) ----------

    /// <summary>
    /// 대상 표면 DragOver 공통 처리: 수락 동작을 정해 커서로 표기하고 Handled로 창 전체
    /// "열기" 폴백(MainWindow.OnWindowDragOver)에 안 넘어가게 한다. targetFolder가 없거나
    /// StorageItems가 아니면 None(무동작 소비 — A93 때와 같은 형태).
    /// </summary>
    internal static void HandleTargetDragOver(DragEventArgs e, string? targetFolder)
    {
        e.Handled = true;
        e.AcceptedOperation =
            targetFolder is { Length: > 0 } && e.DataView.Contains(StandardDataFormats.StorageItems)
                ? DecideOperation(e, targetFolder)
                : DataPackageOperation.None;
    }

    /// <summary>
    /// 드랍 동작 결정: Ctrl = 복사 강제 / Shift = 이동 강제 / 그 외 = 같은 볼륨 이동·다른 볼륨 복사
    /// (외부 소스는 경로를 몰라 기본 복사). 가드: 대상이 원본 폴더 자신·하위면 None,
    /// 같은 폴더로의 "이동"도 None(복사 강제는 허용 — 탐색기처럼 "이름 (2)" 사본이 된다).
    /// </summary>
    internal static DataPackageOperation DecideOperation(DragEventArgs e, string targetFolder)
    {
        var sources = SourcePathsOf(e.DataView);

        // 폴더를 자기 자신·자기 하위로 떨어뜨리는 드래그는 이동·복사 모두 금지(무한 재귀·자기 소멸)
        if (sources is not null &&
            sources.Any(p => IsSelfOrDescendant(targetFolder, p)))
            return DataPackageOperation.None;

        var move = e.Modifiers.HasFlag(DragDropModifiers.Control) ? false
            : e.Modifiers.HasFlag(DragDropModifiers.Shift) ? true
            : sources is { Count: > 0 } && sources.All(p => SameVolume(p, targetFolder));

        // 같은 폴더로의 이동 = 무동작 (원위치 — 커서부터 막는다)
        if (move && sources is { Count: > 0 } &&
            sources.All(p => Path.GetDirectoryName(p) is { } parent && PathsEqual(parent, targetFolder)))
            return DataPackageOperation.None;

        return move ? DataPackageOperation.Move : DataPackageOperation.Copy;
    }

    /// <summary>
    /// 드랍된 항목들을 대상 폴더로 이동/복사한다. 호출부는 await 전에 e.Handled를 동기로
    /// 지정해 둔 상태다(ArchiveView.OnDrop 관용구). 반환 = 실패 안내 문구(없으면 null).
    /// </summary>
    internal static async Task<OpResult> TransferDroppedAsync(
        DataPackageView view, string targetFolder, bool move)
    {
        IReadOnlyList<IStorageItem> items;
        try
        {
            items = await view.GetStorageItemsAsync();
        }
        catch (Exception ex)
        {
            return new OpResult(0, 0, 1, ex.Message);
        }
        var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (paths.Count == 0) return OpResult.Empty;
        return await Task.Run(() => Transfer(paths, targetFolder, move));
    }

    // ---------- 클립보드 (Ctrl+C/X/V) ----------

    /// <summary>
    /// 선택 항목을 클립보드에 복사(cut=false)/잘라내기(cut=true)로 싣는다.
    /// 잘라내기 구분은 RequestedOperation = Move — 앱 내(다른 KOTU 창 포함) 붙여넣기는 이걸로
    /// 일관 동작한다. OS 탐색기의 "Preferred DropEffect" 형식까지는 싣지 않는다(1차 결정 —
    /// 탐색기 쪽에 붙여넣으면 복사로 떨어질 수 있다. docs/A94-matrix.md에 명기).
    /// 반환 = 실패 안내 문구(없으면 null).
    /// </summary>
    internal static async Task<string?> CopyToClipboardAsync(IReadOnlyList<string> paths, bool cut)
    {
        if (paths.Count == 0) return null;
        var items = await CollectStorageItemsAsync(paths);
        if (items.Count == 0) return "Nothing to copy";

        var data = new DataPackage
        {
            RequestedOperation = cut ? DataPackageOperation.Move : DataPackageOperation.Copy,
        };
        data.Properties[SourcePathsKey] = string.Join('\n', paths);
        data.SetStorageItems(items, !cut); // 둘째 인자 readOnly — 잘라내기(cut)면 false(이동 허용)
        try
        {
            Clipboard.SetContent(data);
        }
        catch (Exception ex)
        {
            return "Clipboard error — " + ex.Message; // 다른 앱이 클립보드를 잠근 순간 등
        }
        return null;
    }

    /// <summary>
    /// 클립보드의 StorageItems를 대상 폴더로 붙여넣는다. RequestedOperation에 Move가 있으면
    /// 이동(잘라내기), 아니면 복사. 이동이 전부 성공하면 클립보드를 비운다 — 두 번째 Ctrl+V가
    /// 사라진 원본을 다시 옮기려다 실패하는 것을 막는다(탐색기도 잘라내기는 1회성).
    /// 반환 = (조작을 시도했는지 — 갱신 필요 여부, 실패 안내 문구).
    /// </summary>
    internal static async Task<(bool DidWork, string? Notice)> PasteFromClipboardAsync(string targetFolder)
    {
        DataPackageView view;
        try
        {
            view = Clipboard.GetContent();
        }
        catch
        {
            return (false, null); // 클립보드 접근 실패 — 붙여넣을 것이 없는 것과 같게 취급
        }
        if (!view.Contains(StandardDataFormats.StorageItems)) return (false, null);

        var move = view.RequestedOperation.HasFlag(DataPackageOperation.Move);
        var result = await TransferDroppedAsync(view, targetFolder, move);
        if (move && result.Failed == 0 && result.Done > 0)
        {
            try { Clipboard.Clear(); } catch { /* 소유권 등 — 비우기 실패는 무해 */ }
        }
        return (true, result.Notice(move));
    }

    // ---------- 내부: 경로 판정 ----------

    private static IReadOnlyList<string>? SourcePathsOf(DataPackageView view)
    {
        try
        {
            // 인터페이스 캐스트 경유 — WinRT 프로젝션이 IReadOnlyDictionary 멤버를 클래스에
            // 노출하지 않는 경우가 있어(CS1061 부류) 컴파일이 보장되는 형태로 부른다.
            var props = (IReadOnlyDictionary<string, object>)view.Properties;
            return props.TryGetValue(SourcePathsKey, out var value) && value is string joined
                ? joined.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                : null;
        }
        catch
        {
            return null; // 외부 소스의 Properties 구현이 별나도 "외부 = 경로 미상"으로만 떨어진다
        }
    }

    private static string TrimSep(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool PathsEqual(string a, string b) =>
        string.Equals(TrimSep(a), TrimSep(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>candidate가 root 자신이거나 그 하위 경로인지 — 대소문자 무시 prefix 검사.</summary>
    private static bool IsSelfOrDescendant(string candidate, string root)
    {
        var c = TrimSep(candidate);
        var r = TrimSep(root);
        return string.Equals(c, r, StringComparison.OrdinalIgnoreCase) ||
               c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameVolume(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetPathRoot(Path.GetFullPath(a)),
                Path.GetPathRoot(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // 판정 불가 = 다른 볼륨 취급(복사 — 원본이 남는 안전한 쪽)
        }
    }

    // ---------- 내부: 실제 조작 (워커 스레드, System.IO) ----------

    /// <summary>워커 스레드: 항목별 이동/복사. 항목 단위로 실패를 격리한다(하나 실패해도 나머지 계속).</summary>
    private static OpResult Transfer(IReadOnlyList<string> paths, string targetFolder, bool move)
    {
        string target;
        try
        {
            target = Path.GetFullPath(targetFolder);
        }
        catch (Exception ex)
        {
            return new OpResult(0, 0, paths.Count, ex.Message);
        }

        int done = 0, skipped = 0, failed = 0;
        string? firstError = null;
        foreach (var raw in paths)
        {
            try
            {
                var src = TrimSep(Path.GetFullPath(raw));
                var isFolder = Directory.Exists(src);
                if (!isFolder && !File.Exists(src))
                {
                    skipped++; // 드래그·복사 후 사라진 항목 — 조용히 건너뜀
                    continue;
                }
                var name = Path.GetFileName(src);
                if (name.Length == 0)
                    throw new IOException("cannot move a drive root");
                if (isFolder && IsSelfOrDescendant(target, src))
                    throw new IOException("cannot put a folder inside itself");
                if (move && Path.GetDirectoryName(src) is { } parent && PathsEqual(parent, target))
                {
                    skipped++; // 같은 폴더로 이동 = 무동작(가드 — DragOver가 놓친 경로 대비)
                    continue;
                }

                var dest = UniqueDestination(target, name); // 충돌 = "이름 (2)" — 대화상자 없음(1차 결정)
                if (isFolder)
                {
                    if (!move) CopyDirectory(src, dest);
                    else if (SameVolume(src, target)) Directory.Move(src, dest);
                    else
                    {
                        CopyDirectory(src, dest); // Directory.Move는 볼륨을 못 넘는다
                        Directory.Delete(src, recursive: true);
                    }
                }
                else
                {
                    if (!move) File.Copy(src, dest);
                    else if (SameVolume(src, target)) File.Move(src, dest);
                    else
                    {
                        File.Copy(src, dest); // 명시 복사+삭제 — 실패 시 원본 보존이 명확하다
                        File.Delete(src);
                    }
                }
                done++;
            }
            catch (Exception ex)
            {
                failed++;
                firstError ??= ex.Message;
            }
        }
        return new OpResult(done, skipped, failed, firstError);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    /// <summary>이름 충돌 회피: "이름.ext" → "이름 (2).ext" → "이름 (3).ext" … (파일·폴더 공통).</summary>
    private static string UniqueDestination(string targetFolder, string name)
    {
        var dest = Path.Combine(targetFolder, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; File.Exists(dest) || Directory.Exists(dest); i++)
            dest = Path.Combine(targetFolder, $"{stem} ({i}){ext}");
        return dest;
    }

    // ---------- 내부: WinRT 항목 수집 ----------

    /// <summary>경로 → IStorageItem(파일/폴더). 사라진 항목은 건너뛴다. StorageFile API는 agile.</summary>
    private static async Task<List<IStorageItem>> CollectStorageItemsAsync(IReadOnlyList<string> paths)
    {
        var items = new List<IStorageItem>(paths.Count);
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                    items.Add(await StorageFolder.GetFolderFromPathAsync(path));
                else
                    items.Add(await StorageFile.GetFileFromPathAsync(path));
            }
            catch
            {
                // 그새 사라졌거나 접근 불가 — 남은 항목으로 계속
            }
        }
        return items;
    }
}
