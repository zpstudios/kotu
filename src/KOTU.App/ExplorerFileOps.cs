using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Storage;
using Windows.Storage.Streams;

namespace KOTU.App;

/// <summary>
/// 자체 탐색기 파일 조작 공용 로직 (A94 1차 v0.124.0 · 2차 v0.125.0 · 3차 v0.150.0 · 4차 v0.151.0) —
/// 드래그 아웃 데이터 구성, 드랍 이동/복사, 클립보드 복사/잘라내기/붙여넣기, 휴지통·영구 삭제·
/// 이름변경·새 폴더와 잘라내기 표시 상태가 한곳에 모인다.
/// 세 표면(ExplorerPane 리스트 · ThumbnailExplorer 타일 · FileListOverlay 패널)이 같은 규칙을 쓴다.
///
/// 동작 결정(윈도우 관례): 같은 볼륨 = 이동, 다른 볼륨 = 복사. Ctrl 홀드 = 복사 강제,
/// Shift 홀드 = 이동 강제. 원본 볼륨은 앱 내부 드래그(다른 KOTU 창 포함)에서만 알 수 있다 —
/// DataPackage.Properties에 실어 보낸 경로 목록으로 판정하고, 외부(OS 탐색기 등) 소스는
/// 경로를 모르므로 기본 = 복사다(수정자로만 이동 강제 가능. docs/A94-matrix.md에 명기).
///
/// 실제 조작은 System.IO로 워커 스레드에서 한다(UI 블로킹 금지). WinRT StorageFolder에는
/// MoveAsync가 없어 폴더 이동을 못 하므로, 파일·폴더 모두 System.IO 한 경로로 통일했다.
/// 이름 충돌은 3차부터 탐색기 동등의 선택형 — 워커가 만나는 충돌마다 ExplorerConflictDialog로
/// Replace(파일 덮어쓰기/폴더 병합)·Skip·Keep both("이름 (2)" 규칙 재사용)를 묻고, 취소(Esc)는
/// 남은 작업 중단이다(수행분 유지 — 탐색기 동등). 1차의 무조건 "(2)" 자동 생성은 같은 폴더로의
/// 강제 복사(자기 자신과의 충돌)와 Keep both·새 폴더 경로에만 남았다. 예외(변경 금지 —
/// docs/A94-matrix.md 명기): F2 이름변경 = 거부+원복, 새 폴더 = "New folder (2)".
/// 대량 조작은 진행 문구("Copying 3 of 12...")를 조작 시작 표면의 안내 채널로 라이브 갱신한다.
///
/// 4차(v0.151.0)가 얹은 것: Shift+Del 영구 삭제(확인 대화상자 필수 — 휴지통행과 달리),
/// 잘라내기 원본 반투명 표시(프로세스 전역 1벌 경로 집합 + 표면 렌더 시 경로 매칭),
/// 접근 거부(UAC 필요) 실패의 구분 집계와 관리자 재시작 제안(<see cref="ReportAsync"/>).
///
/// 6차(v0.153.0)가 얹은 것: OS 탐색기 클립보드 상호운용("Preferred DropEffect" 쓰기·읽기 —
/// 탐색기는 이 형식으로만 잘라내기/복사를 가른다), 붙여넣기 메뉴 활성 판정
/// (<see cref="CanPasteFromClipboard"/>), 다중 선택 일괄 열기 상한 적용
/// (<see cref="TakeBatchOpen"/> — 표면이 여는 주체이고 여기서는 대상만 추린다).
/// </summary>
internal static class ExplorerFileOps
{
    /// <summary>
    /// 앱 내부 드래그 식별용 원본 경로 목록 키 (DataPackage.Properties). 값은 '\n' 연결 문자열 —
    /// Properties는 프로세스 경계를 넘어야 하므로(다른 KOTU 창) 원시 문자열만 싣는다.
    /// 경로에 개행은 올 수 없어 구분자로 안전하다.
    /// </summary>
    private const string SourcePathsKey = "kotu.explorer.sourcePaths";

    /// <summary>
    /// 조작 결과 집계. Skipped = 무동작 가드(같은 폴더로 이동 등)·소실 항목 + 3차부터
    /// 충돌 대화상자의 Skip 선택. Cancelled/Total = 3차 취소 안내("n of m completed")용 —
    /// 카운트는 전부 최상위 항목 기준(폴더 병합의 내부 파일은 폴더 1건에 묶인다).
    /// Denied(4차) = Failed 중 권한 부족(UAC 필요)인 건수 — 부분집합이지 별도 축이 아니다.
    /// 1 이상이면 <see cref="ReportAsync"/>가 완료 요약 대신 관리자 재시작을 제안한다.
    /// </summary>
    internal sealed record OpResult(int Done, int Skipped, int Failed, string? FirstError,
        bool Cancelled = false, int Total = 0, int Denied = 0)
    {
        internal static OpResult Empty { get; } = new(0, 0, 0, null);

        /// <summary>
        /// 실패가 있을 때만 짧은 안내 문구 — 성공은 뷰 갱신이 곧 피드백이라 조용히 넘어간다.
        /// 취소(A94 3차)는 예외 — 어디까지 했는지 "n of m completed"로 알린다(첫 오류보다 우선).
        /// </summary>
        internal string? Notice(bool move) => Cancelled
            ? $"{(move ? "Move" : "Copy")} cancelled - {Done} of {Total} completed"
            : Notice(move ? "moved" : "copied");

        /// <summary>임의 동사형(A94 2차 — 삭제 "deleted" 등). 규칙은 위와 동일 — 실패가 있을 때만.</summary>
        internal string? Notice(string verb) => Failed == 0
            ? null
            : $"{Failed} item(s) could not be {verb} - {FirstError}";
    }

    /// <summary>
    /// 조작을 시작한 표면의 UI 문맥 (A94 3차) — 충돌 대화상자·진행 문구를 워커에서 UI 스레드로
    /// 마셜하는 통로. 조작 시작 시점(UI 스레드)에 캡처해 넘긴다: Dispatcher = 그 표면 창의
    /// DispatcherQueue, Root = 그 창의 XamlRoot(ContentDialog 필수 — null이면 충돌 = 취소 흐름),
    /// Notice = 그 표면의 A92류 안내 문구 채널(진행 문구 라이브 갱신에 재사용).
    /// </summary>
    internal sealed record OpUi(DispatcherQueue Dispatcher, XamlRoot? Root, Action<string>? Notice)
    {
        /// <summary>워커 → UI 문구 마셜 (SettingsView.DispatcherProgress와 같은 TryEnqueue — 창이 죽었으면 조용히 버려진다).</summary>
        internal void Post(string text)
        {
            if (Notice is { } notice) Dispatcher.TryEnqueue(() => notice(text));
        }
    }

    /// <summary>Ctrl이 눌린 상태인지 — 클립보드 키 판정용(ExplorerPane의 Shift 판정과 같은 API).</summary>
    internal static bool IsCtrlDown() => Microsoft.UI.Input.InputKeyboardSource
        .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>
    /// Shift가 눌린 상태인지 (A94 2차) — Ctrl+Shift+N(새 폴더)과 Shift+Del(영구 삭제 — 4차부터
    /// 실동작) 판정용. IsCtrlDown과 같은 API.
    /// </summary>
    internal static bool IsShiftDown() => Microsoft.UI.Input.InputKeyboardSource
        .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    // ---------- 다중 선택 일괄 열기 (A94 6차, v0.153.0) ----------

    /// <summary>
    /// 한 번에 여는 파일 수 상한 (사양 확정 수치) — 초과분은 열지 않고 안내 문구로만 알린다.
    /// 여는 주체는 표면(기존 FileActivated·FileActivatedNewWindow 이벤트)이고, 여기서는
    /// 대상 목록만 추린다 — 창 생성 경로를 이 파일이 알 필요가 없다.
    /// </summary>
    internal const int BatchOpenLimit = 10;

    /// <summary>
    /// 일괄 열기 대상 추리기 (A94 6차): 상한을 넘으면 **앞 10개만** 돌려주고 나머지는
    /// 기존 안내 채널(A92류 일시 문구)로 알린다 — 별도 대화상자를 만들지 않는다.
    /// 목록은 호출부가 이미 "파일만"(폴더 제외)으로 걸러 넘긴다.
    /// </summary>
    internal static IReadOnlyList<string> TakeBatchOpen(IReadOnlyList<string> files, Action<string>? notice)
    {
        if (files.Count <= BatchOpenLimit) return files;
        notice?.Invoke($"Opened first {BatchOpenLimit} of {files.Count} selected files");
        return files.Take(BatchOpenLimit).ToList();
    }

    // ---------- 잘라내기 원본 반투명 표시 (A94 4차, v0.151.0) ----------

    /// <summary>잘라내기 표시 항목의 불투명도(윈도우 탐색기와 같은 절반 흐림) — 두 표면 공용 단일 값.</summary>
    internal const double CutOpacity = 0.5;

    /// <summary>
    /// 잘라내기(Ctrl+X)로 표시 중인 경로 집합. **프로세스 전역 1벌** — 클립보드 자체가 전역이라
    /// 모든 창·모든 표면이 같은 집합을 봐야 한다(A70의 전역 1벌 저장 깔때기와 같은 사고방식).
    /// 접근은 단일 UI 스레드 전제(A110 확정) — 클립보드 조작도 표면 렌더도 전부 UI 스레드다.
    /// 경로 비교는 대소문자 무시(NTFS 관례, 저장소의 다른 경로 비교와 동일).
    /// </summary>
    private static readonly HashSet<string> CutMarked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 잘라내기 표시 집합이 바뀌었다 — 두 표면(ExplorerPane·ThumbnailExplorer)이 구독해 이미
    /// 그려 둔 항목의 흐림만 다시 맞춘다(재스캔이 아니다 — 선택·스크롤 보존).
    /// 구독은 Loaded, 해지는 Unloaded — 정적 이벤트가 닫힌 창의 컨트롤을 붙들지 않게 한다.
    /// 신규 이벤트는 이 하나뿐이고, 그 밖의 갱신은 기존 재스캔/ViewChanged 경로가 처리한다
    /// (새로 그려지는 항목은 생성 시점에 <see cref="ApplyCutMark"/>로 반영된다).
    /// </summary>
    internal static event Action? CutMarksChanged;

    /// <summary>이 경로가 잘라내기 표시 대상인지 — 표면이 항목마다 묻는다(재스캔 뒤 재적용의 근거).</summary>
    internal static bool IsCutMarked(string path) => CutMarked.Count > 0 && CutMarked.Contains(TrimSep(path));

    /// <summary>
    /// 항목 컨테이너 하나에 잘라내기 흐림을 반영한다 — 두 표면(리스트 행·그리드 타일) 공용.
    /// 컨테이너가 아니라 '콘텐츠'의 Opacity를 건드린다: 선택 강조는 또렷한 채 아이콘·이름만
    /// 흐려지는 탐색기 모양이 된다. 항목 = 컨테이너 직접 추가(Tag = Entry) 구조 전제.
    /// </summary>
    internal static void ApplyCutMark(object item)
    {
        // ExplorerListing은 KOTU.Core.Routing — 이 파일에 using을 새로 들이지 않으려 전체 이름으로 쓴다
        // (Windows.Storage·DataTransfer 이름들과 섞을 이유가 없다).
        if (item is SelectorItem { Content: UIElement content, Tag: KOTU.Core.Routing.ExplorerListing.Entry entry })
            content.Opacity = IsCutMarked(entry.Path) ? CutOpacity : 1.0;
    }

    /// <summary>
    /// 잘라내기 표시 지정(Ctrl+X 성공 직후). 이전 표시는 대체된다 — 새 잘라내기·새 복사가
    /// 앞선 표시를 지우는 것이 탐색기 동작이다.
    /// </summary>
    private static void SetCutMarks(IReadOnlyList<string> paths)
    {
        CutMarked.Clear();
        foreach (var path in paths) CutMarked.Add(TrimSep(path));
        RaiseCutMarksChanged();
    }

    /// <summary>
    /// 잘라내기 표시 해제 — 붙여넣기 소진(클립보드 비우기와 같은 조건)·Ctrl+C·Esc 공용.
    /// 이미 비어 있으면 통지도 하지 않는다(무의미한 전 표면 재적용 방지).
    /// </summary>
    internal static void ClearCutMarks()
    {
        if (CutMarked.Count == 0) return;
        CutMarked.Clear();
        RaiseCutMarksChanged();
    }

    /// <summary>
    /// 표면별 격리 통지 — 구독자 하나가 던져도(창이 그새 내려가 XAML 접근이 실패하는 등) 나머지
    /// 표면은 계속 갱신한다. 조작 로직의 "항목별 실패 격리"와 같은 원칙이고, 여기서 예외가
    /// 새어 나가면 Ctrl+X 핸들러(async void)가 그대로 죽는다. 놓친 표면도 다음 재스캔의
    /// 생성 시점 반영(<see cref="ApplyCutMark"/>)으로 결국 맞춰진다.
    /// </summary>
    private static void RaiseCutMarksChanged()
    {
        if (CutMarksChanged is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch
            {
                // 내려간 창의 표면 — 무시하고 다음 표면으로
            }
        }
    }

    // ---------- 접근 거부(UAC 필요) 구분 안내 (A94 4차, v0.151.0) ----------

    /// <summary>
    /// 권한 부족(UAC 필요) 실패인지. System.IO 경로는 <see cref="UnauthorizedAccessException"/>으로
    /// 던지고, WinRT 경로(DeleteAsync 등)는 HRESULT를 실은 예외(IOException·COMException 부류)로
    /// 던져 타입만으로는 못 가른다 — 그래서 HResult까지 본다:
    /// 0x80070005 = E_ACCESSDENIED(ERROR_ACCESS_DENIED), 0x80070522 = ERROR_PRIVILEGE_NOT_HELD.
    /// 판정이 빗나가도 종전과 같은 일반 실패 안내로만 떨어진다(안전한 쪽).
    /// </summary>
    internal static bool IsAccessDenied(Exception ex)
    {
        if (ex is UnauthorizedAccessException) return true;
        var code = unchecked((uint)ex.HResult);
        return code == 0x80070005u || code == 0x80070522u;
    }

    /// <summary>
    /// 조작 결과 보고의 단일 종착점 (A94 4차). 접근 거부가 1건 이상이면 완료·실패 요약 문구 대신
    /// 관리자 재시작 제안 대화상자를 띄운다 — 권한 부족이 일반 실패에 뭉개지지 않게 한다.
    /// 그 밖에는 종전대로 안내 문구 채널(문구가 null이면 조용 — 성공은 뷰 갱신이 피드백).
    /// 띄울 창이 없으면(XamlRoot 부재) 안내 문구로 떨어진다.
    /// 호출부는 이동/복사/붙여넣기/삭제(휴지통·영구)/이름변경/새 폴더 전부다.
    /// </summary>
    internal static async Task ReportAsync(string? notice, int denied, OpUi ui)
    {
        if (denied > 0 && ui.Root is { } root)
        {
            await ExplorerDialogs.PromptAccessDeniedAsync(ui.Dispatcher, root, denied);
            return;
        }
        if (notice is not null) ui.Notice?.Invoke(notice);
    }

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
    /// 지정해 둔 상태다(ArchiveView.OnDrop 관용구). ui = 조작 시작 표면의 UI 문맥(A94 3차 —
    /// 충돌 대화상자·진행 문구). 반환 = 결과 집계(안내 문구는 OpResult.Notice).
    /// </summary>
    internal static async Task<OpResult> TransferDroppedAsync(
        DataPackageView view, string targetFolder, bool move, OpUi ui)
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
        return await Task.Run(() => TransferAsync(paths, targetFolder, move, ui));
    }

    // ---------- OS 탐색기 클립보드 상호운용: "Preferred DropEffect" (A94 6차, v0.153.0) ----------
    // RequestedOperation(Copy/Move)은 WinRT 앱끼리만 통하는 축이라 1~5차에서는 KOTU↔KOTU만
    // 잘라내기가 성립했다. 셸(OS 탐색기)은 이 문자열 형식의 4바이트 DWORD 하나로만 잘라내기를
    // 가르므로, 양방향 정합을 위해 **쓸 때도 읽을 때도** 이 형식을 다룬다. 둘 다 실패는 무해 —
    // 종전(복사로 떨어짐 / RequestedOperation 판정)으로 되돌아갈 뿐이다.

    /// <summary>셸 클립보드 형식 이름(CFSTR_PREFERREDDROPEFFECT의 문자열 이름) — 값은 4바이트 DWORD.</summary>
    private const string PreferredDropEffectFormat = "Preferred DropEffect";

    /// <summary>DROPEFFECT_MOVE — 잘라내기. 탐색기는 Ctrl+X에 정확히 이 값을 싣는다.</summary>
    private const uint DropEffectMove = 2;

    /// <summary>DROPEFFECT_COPY|DROPEFFECT_LINK(1|4) — 탐색기가 Ctrl+C에 싣는 관례 값.</summary>
    private const uint DropEffectCopy = 5;

    /// <summary>
    /// 클립보드 데이터에 "Preferred DropEffect"를 싣는다 (A94 6차): 잘라내기 = 2, 복사 = 5.
    /// 값은 4바이트 리틀엔디언 DWORD 스트림이다(윈도우는 리틀엔디언 — BitConverter가 그대로 맞다).
    /// SetData의 값은 스트림이어야 셸이 읽어 가므로 MemoryStream을 WinRT 스트림으로 감싼다
    /// (<c>AsRandomAccessStream</c> — ExplorerPane.LoadThumbnailsAsync 선례와 같은 확장).
    /// MemoryStream을 여기서 닫지 않는 것은 의도다 — 클립보드가 나중에 읽어 가고, 관리 메모리뿐이라
    /// 해제할 자원이 없다. 실패해도 조용히 넘어간다(RequestedOperation만 남은 종전 동작).
    /// </summary>
    private static void SetPreferredDropEffect(DataPackage data, bool cut)
    {
        try
        {
            var bytes = BitConverter.GetBytes(cut ? DropEffectMove : DropEffectCopy);
            data.SetData(PreferredDropEffectFormat, new MemoryStream(bytes).AsRandomAccessStream());
        }
        catch
        {
            // 이 형식을 못 실어도 앱 내부(KOTU↔KOTU) 동작은 RequestedOperation으로 종전과 같다
        }
    }

    /// <summary>
    /// 클립보드 데이터의 "Preferred DropEffect"로 이동 여부를 판정한다 (A94 6차) —
    /// OS 탐색기에서 Ctrl+X 한 뒤 KOTU에 붙여넣는 경우의 유일한 근거다(탐색기는
    /// RequestedOperation을 싣지 않는다). 반환 null = 형식 없음·판독 실패 → 호출부가 종전
    /// 판정(RequestedOperation)을 그대로 쓴다. 값 형은 소스·런타임마다 다를 수 있어
    /// (IRandomAccessStream / IBuffer / byte[]) 형 검사 후 안전 판독하고, 어긋나면 전부 null이다.
    /// </summary>
    private static async Task<bool?> PreferredDropEffectIsMoveAsync(DataPackageView view)
    {
        try
        {
            if (!view.Contains(PreferredDropEffectFormat)) return null;
            var bytes = ReadFourBytes(await view.GetDataAsync(PreferredDropEffectFormat));
            if (bytes is null) return null;
            // 이동 비트만 본다 — 탐색기는 2를 싣지만 COPY|MOVE(3)처럼 겹쳐 싣는 소스도 있다.
            return (BitConverter.ToUInt32(bytes, 0) & DropEffectMove) != 0;
        }
        catch
        {
            return null; // 판독 실패 = 현행 판정 유지(복사로 떨어져 원본이 남는 안전한 쪽)
        }
    }

    /// <summary>
    /// WinRT 클립보드 값에서 앞 4바이트를 꺼낸다 (A94 6차). 지원하지 않는 형이거나 길이가
    /// 모자라면 null — 호출부가 종전 판정으로 떨어진다. 스트림은 읽고 즉시 닫는다
    /// (클립보드 원본이 아니라 이번 읽기용 뷰 — 다음 붙여넣기가 다시 열 수 있다).
    /// </summary>
    private static byte[]? ReadFourBytes(object? value)
    {
        var bytes = new byte[4];
        switch (value)
        {
            case byte[] raw:
                return raw.Length >= bytes.Length ? raw : null;
            case IRandomAccessStream stream:
            {
                using var reader = stream.AsStreamForRead();
                return reader.Read(bytes, 0, bytes.Length) == bytes.Length ? bytes : null;
            }
            case IBuffer buffer:
            {
                using var reader = DataReader.FromBuffer(buffer);
                if (reader.UnconsumedBufferLength < bytes.Length) return null;
                reader.ReadBytes(bytes);
                return bytes;
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// 붙여넣기 메뉴 항목을 활성화할지 (A94 6차 — 우클릭 메뉴가 열릴 때 판정).
    /// 클립보드 접근이 실패하면(다른 앱이 잠근 순간 등) **활성으로 둔다** — 눌러도
    /// <see cref="PasteFromClipboardAsync"/>가 조용한 무동작으로 떨어지므로 안전하다(사양 확정).
    /// </summary>
    internal static bool CanPasteFromClipboard()
    {
        try
        {
            return Clipboard.GetContent().Contains(StandardDataFormats.StorageItems);
        }
        catch
        {
            return true;
        }
    }

    // ---------- 클립보드 (Ctrl+C/X/V) ----------

    /// <summary>
    /// 선택 항목을 클립보드에 복사(cut=false)/잘라내기(cut=true)로 싣는다.
    /// 잘라내기 구분은 RequestedOperation = Move — 앱 내(다른 KOTU 창 포함) 붙여넣기는 이걸로
    /// 일관 동작한다. A94 6차부터는 OS 탐색기용 "Preferred DropEffect"(잘라내기 2 / 복사 5)도
    /// 함께 싣는다 — 두 축을 같은 조작에서 한 번에 정하므로 서로 어긋날 수 없다.
    /// 반환 = 실패 안내 문구(없으면 null).
    /// A94 4차: 성공하면 잘라내기 표시를 갱신한다 — cut이면 이 경로들로 지정, 복사면 해제
    /// (새 클립보드 내용이 앞선 잘라내기를 무효화한다 — 탐색기 동등). 클립보드 적재가
    /// 실패했으면 표시는 건드리지 않는다(클립보드와 표시가 어긋나지 않게).
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
        SetPreferredDropEffect(data, cut); // A94 6차 — OS 탐색기가 읽는 잘라내기/복사 구분
        try
        {
            Clipboard.SetContent(data);
        }
        catch (Exception ex)
        {
            return "Clipboard error - " + ex.Message; // 다른 앱이 클립보드를 잠근 순간 등
        }
        if (cut) SetCutMarks(paths); else ClearCutMarks(); // A94 4차 — 원본 반투명 표시 갱신
        return null;
    }

    /// <summary>
    /// 클립보드의 StorageItems를 대상 폴더로 붙여넣는다. 이동/복사 판정은 A94 6차부터 두 단계다:
    /// **"Preferred DropEffect"가 있으면 그것이 우선**(OS 탐색기에서 잘라내기 → KOTU 붙여넣기 =
    /// 이동이 성립하는 근거), 없거나 못 읽으면 종전대로 RequestedOperation에 Move가 있는지.
    /// 이동이 전부 성공하면 클립보드를 비운다 — 두 번째 Ctrl+V가
    /// 사라진 원본을 다시 옮기려다 실패하는 것을 막는다(탐색기도 잘라내기는 1회성).
    /// 취소된 이동은 비우지 않는다(A94 3차 — 남은 항목을 다시 붙여넣을 수 있게).
    /// ui = 조작 시작 표면의 UI 문맥(A94 3차). 반환 = (조작을 시도했는지 — 갱신 필요 여부,
    /// 결과 집계 — 접근 거부 판정용(A94 4차), 안내 문구).
    /// A94 4차: 잘라내기 표시 해제는 **클립보드를 비우는 것과 같은 조건**이다(1회성 소진) —
    /// 표시와 클립보드가 어긋나지 않게 한 조건으로 묶었다.
    /// </summary>
    internal static async Task<(bool DidWork, OpResult Result, string? Notice)> PasteFromClipboardAsync(
        string targetFolder, OpUi ui)
    {
        DataPackageView view;
        try
        {
            view = Clipboard.GetContent();
        }
        catch
        {
            return (false, OpResult.Empty, null); // 클립보드 접근 실패 — 붙여넣을 것이 없는 것과 같게 취급
        }
        if (!view.Contains(StandardDataFormats.StorageItems)) return (false, OpResult.Empty, null);

        // A94 6차 — 셸 형식 우선, 없으면 종전 판정. 이동으로 판정된 원본의 삭제 주체는 우리다
        // (아래 TransferAsync가 System.IO로 옮긴다 — 탐색기 쪽에 알릴 것은 없다).
        var move = await PreferredDropEffectIsMoveAsync(view)
            ?? view.RequestedOperation.HasFlag(DataPackageOperation.Move);
        var result = await TransferDroppedAsync(view, targetFolder, move, ui);
        if (move && !result.Cancelled && result.Failed == 0 && result.Done > 0)
        {
            try { Clipboard.Clear(); } catch { /* 소유권 등 — 비우기 실패는 무해 */ }
            ClearCutMarks(); // A94 4차 — 잘라내기 1회성 소진(원본은 이미 사라졌다)
        }
        return (true, result, result.Notice(move));
    }

    // ---------- 삭제: Del = 휴지통 (A94 2차) / Shift+Del = 영구 삭제 (A94 4차) ----------

    /// <summary>
    /// 선택 항목 전부(파일·폴더)를 휴지통으로 보낸다 — WinRT <c>IStorageItem.DeleteAsync</c>의
    /// StorageDeleteOption.Default가 휴지통 경유다(ImageViewerView.DeleteCurrentAsync 선례 —
    /// COM 인터롭(SHFileOperation류) 없이 성립한다). 확인 대화상자 없음(윈도우 탐색기도
    /// 휴지통행은 기본 무확인). 항목별 실패 격리 — Skipped = 그새 사라져 수집조차 안 된 항목.
    /// </summary>
    internal static Task<OpResult> DeleteToRecycleAsync(IReadOnlyList<string> paths) =>
        DeleteAsync(paths, StorageDeleteOption.Default);

    /// <summary>
    /// Shift+Del = 영구 삭제 (A94 4차): 휴지통을 거치지 않는다 — 같은 WinRT DeleteAsync의
    /// StorageDeleteOption.PermanentDelete로, 2차 Default와 **같은 enum·같은 호출부 구조**다
    /// (새 API 표면이 아니라 인자 하나 차이 — 저위험). 확인은 호출부가 이미 받아 온다
    /// (ExplorerDialogs.ConfirmPermanentDeleteAsync — 탐색기도 영구 삭제만 확인창을 띄운다).
    /// 대상 선택 규칙·실패 격리·완료 후 재스캔은 휴지통 삭제와 완전히 동일한 경로다.
    /// </summary>
    internal static Task<OpResult> DeletePermanentlyAsync(IReadOnlyList<string> paths) =>
        DeleteAsync(paths, StorageDeleteOption.PermanentDelete);

    /// <summary>
    /// 삭제 공통 실행부 — 휴지통(Default)과 영구(PermanentDelete)가 옵션 하나만 다르다.
    /// 항목별 실패 격리(하나 실패해도 나머지 계속) + 접근 거부 구분 집계(A94 4차).
    /// </summary>
    private static async Task<OpResult> DeleteAsync(IReadOnlyList<string> paths, StorageDeleteOption option)
    {
        if (paths.Count == 0) return OpResult.Empty;
        var items = await CollectStorageItemsAsync(paths);
        int done = 0, failed = 0, denied = 0;
        string? firstError = null;
        foreach (var item in items)
        {
            try
            {
                await item.DeleteAsync(option);
                done++;
            }
            catch (Exception ex)
            {
                failed++;
                if (IsAccessDenied(ex)) denied++; // 권한 부족 = 관리자 재시작 제안 대상(A94 4차)
                firstError ??= ex.Message;
            }
        }
        return new OpResult(done, paths.Count - items.Count, failed, firstError, Denied: denied);
    }

    // ---------- 이름변경 · 새 폴더 (A94 2차, v0.125.0) ----------

    /// <summary>
    /// 같은 폴더 안 이름변경 — File.Move/Directory.Move.
    /// 반환 = (실패 안내 문구 — 성공·무변경이면 null, 그 실패가 권한 부족인지 — A94 4차).
    /// 검증 실패(빈 이름·잘못된 문자·충돌)면 아무것도 바꾸지 않는다 — 이동/복사와 달리 자동 "(2)"를
    /// 붙이지 않는다(이름변경 결과가 입력과 다른 이름이 되는 건 사용자 의도와 다른 결과다).
    /// 대소문자만 바꾸는 이름은 허용 — 같은 경로 취급(NTFS 대소문자 무시)이라 충돌 검사를 건너뛴다
    /// (Directory.Move의 대소문자 전용 이름변경은 .NET Core 3.0부터 허용).
    /// 단일 메타데이터 조작이라 워커를 태우지 않는다(즉시 반환 — Transfer류 대량 조작과 다르다).
    /// </summary>
    internal static (string? Notice, bool Denied) Rename(string path, string newName)
    {
        try
        {
            var name = newName.Trim();
            if (name.Length == 0) return ("Name cannot be empty", false);
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return ("Name contains characters that are not allowed", false);

            var src = TrimSep(Path.GetFullPath(path));
            var oldName = Path.GetFileName(src);
            if (oldName.Length == 0) return ("Cannot rename a drive root", false);
            if (string.Equals(oldName, name, StringComparison.Ordinal)) return (null, false); // 무변경 = 무동작
            if (Path.GetDirectoryName(src) is not { Length: > 0 } parent)
                return ("Cannot rename this item", false);

            var dest = Path.Combine(parent, name);
            if (!string.Equals(oldName, name, StringComparison.OrdinalIgnoreCase) &&
                (File.Exists(dest) || Directory.Exists(dest)))
                return ($"\"{name}\" already exists here", false);

            if (Directory.Exists(src)) Directory.Move(src, dest);
            else if (File.Exists(src)) File.Move(src, dest);
            else return ("The item no longer exists", false);
            return (null, false);
        }
        catch (Exception ex)
        {
            // 잠긴 파일·권한 부족 등 — 호출부가 안내 문구로 띄운다(권한 부족이면 관리자 재시작 제안)
            return (ex.Message, IsAccessDenied(ex));
        }
    }

    /// <summary>
    /// 현재 폴더에 "New folder"를 만든다 — 충돌 시 "New folder (2)"(UniqueDestination 재사용,
    /// 탐색기 관례). 반환 = (생성된 전체 경로, 실패 안내 문구, 권한 부족 실패인지 — A94 4차) —
    /// 성공이면 문구가 null이다.
    /// 호출부는 재스캔 완료 후 이 경로의 항목을 찾아 곧바로 이름변경 편집에 진입시킨다.
    /// </summary>
    internal static (string? Created, string? Notice, bool Denied) CreateFolder(string parentFolder)
    {
        try
        {
            var dest = UniqueDestination(Path.GetFullPath(parentFolder), "New folder");
            Directory.CreateDirectory(dest);
            return (dest, null, false);
        }
        catch (Exception ex)
        {
            return (null, "Could not create a folder - " + ex.Message, IsAccessDenied(ex));
        }
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

    /// <summary>
    /// 워커 스레드: 항목별 이동/복사 (A94 3차 — 충돌 선택형). 항목 단위로 실패를 격리한다
    /// (하나 실패해도 나머지 계속). 이름 충돌은 TransferOp.AskAsync로 UI에 묻고(정책 흐름:
    /// all 선택이 있으면 그대로, 없으면 대화상자), 취소면 그 지점에서 남은 작업을 중단한다
    /// (수행분 유지). Task.Run으로 진입하고 대화상자 대기는 await뿐이라 파일 I/O가 UI 스레드로
    /// 올라오지 않는다(TCS는 RunContinuationsAsynchronously — 연속도 풀에서 돈다).
    /// </summary>
    private static async Task<OpResult> TransferAsync(
        IReadOnlyList<string> paths, string targetFolder, bool move, OpUi ui)
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

        var op = new TransferOp(ui, move, paths.Count);
        int done = 0, skipped = 0, failed = 0, denied = 0; // denied = failed 중 권한 부족(A94 4차)
        string? firstError = null;
        for (var i = 0; i < paths.Count && !op.Cancelled; i++)
        {
            op.ReportProgress(i + 1); // "Copying 3 of 12..." — 최상위 항목 기준(3개 미만 생략)
            try
            {
                var src = TrimSep(Path.GetFullPath(paths[i]));
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

                var dest = Path.Combine(target, name);
                if (PathsEqual(src, dest))
                {
                    // 같은 폴더로의 강제 복사(Ctrl) = 자기 자신과의 충돌 — 탐색기처럼 묻지 않고
                    // "(2)" 사본(1차 규칙 유지. 병합-자기재귀도 이 가드가 원천 차단한다)
                    TransferItem(src, UniqueDestination(target, name), isFolder, move);
                    done++;
                    continue;
                }
                if (!File.Exists(dest) && !Directory.Exists(dest))
                {
                    TransferItem(src, dest, isFolder, move); // 충돌 없음 — 종전과 동일한 직행
                    done++;
                    continue;
                }

                // 체크박스 노출 = 남은 충돌 2건 이상일 때. 폴더 충돌은 병합 내부의 파일 충돌
                // 가능성이 미지수라 항상 노출한다(구현 시 결정 — 보고서 명기).
                var offerAll = !op.HasSticky &&
                    (isFolder || KnownConflictsAhead(paths, i, target) >= 2);
                var choice = await op.AskAsync(name, isFolder, target, offerAll);
                if (choice is null) break; // 취소 — 남은 작업 중단(이미 수행분 유지)
                switch (choice)
                {
                    case ConflictChoice.Skip:
                        skipped++; // 이동이면 원본 잔류 — 탐색기 동등
                        break;
                    case ConflictChoice.KeepBoth:
                        TransferItem(src, UniqueDestination(target, name), isFolder, move);
                        done++;
                        break;
                    case ConflictChoice.Replace when isFolder && Directory.Exists(dest):
                    {
                        // 폴더 충돌의 Replace = 병합(대상을 지우지 않는다 — 탐색기 동등)
                        var (ok, mergeError, mergeDenied) = await MergeDirectoryAsync(op, src, dest);
                        if (op.Cancelled) break; // 병합 중 취소 — 이 항목은 미완(카운트 제외)
                        if (ok) done++;
                        else
                        {
                            failed++;
                            // 병합 내부의 권한 부족도 최상위 1건으로 센다(카운트는 최상위 기준 — 3차 규칙)
                            if (mergeDenied) denied++;
                            firstError ??= mergeError ?? $"could not merge \"{name}\"";
                        }
                        break;
                    }
                    case ConflictChoice.Replace:
                        // 파일 덮어쓰기. 종류 불일치(파일 자리에 폴더 등)는 아래 실행부가 던져
                        // 항목 실패로 격리된다(탐색기도 오류로 다룬다)
                        TransferItem(src, dest, isFolder, move, overwrite: true);
                        done++;
                        break;
                }
            }
            catch (Exception ex)
            {
                failed++;
                if (IsAccessDenied(ex)) denied++; // 권한 부족 = 관리자 재시작 제안 대상(A94 4차)
                firstError ??= ex.Message;
            }
        }
        return new OpResult(done, skipped, failed, firstError, op.Cancelled, paths.Count, denied);
    }

    /// <summary>
    /// 이동/복사 1회분의 충돌 정책·취소·진행 표시 상태 (A94 3차). 인스턴스는 조작 1회 수명 —
    /// "Do this for all remaining conflicts"도 이번 조작 한정이다(저장 안 함). 워커 흐름 전용
    /// (순차 await 체인이라 잠금 불요).
    /// </summary>
    private sealed class TransferOp
    {
        private const int ProgressMinItems = 3;     // 1~2개 조작은 진행 문구 생략(순간 완료 — 구현 시 결정)
        private const int ProgressThrottleMs = 100; // UI 마셜 스로틀 — 마지막 값(current == total)은 예외

        private readonly OpUi _ui;
        private readonly bool _move;
        private readonly int _total;
        private ConflictChoice? _sticky;
        private DateTime _lastProgressAt = DateTime.MinValue;

        internal TransferOp(OpUi ui, bool move, int total)
        {
            _ui = ui;
            _move = move;
            _total = total;
        }

        /// <summary>취소됨(Esc·창 닫힘·표시 실패) — 이후 남은 작업은 수행하지 않는다(수행분 유지).</summary>
        internal bool Cancelled { get; private set; }

        /// <summary>이동 조작인지 — 병합 재귀(MergeDirectoryAsync)가 참조한다.</summary>
        internal bool Move => _move;

        /// <summary>all 선택이 이미 있는지 — 있으면 대화상자가 안 뜨므로 체크박스 판정 비용을 아낀다.</summary>
        internal bool HasSticky => _sticky is not null;

        /// <summary>
        /// 충돌 1건의 정책 결정: all 선택이 있으면 그대로, 없으면 UI 스레드로 마셜해 대화상자
        /// (ExplorerConflictDialog — 창 단위 직렬화 포함). null = 취소(이후 전 충돌도 취소로 고정).
        /// </summary>
        internal async Task<ConflictChoice?> AskAsync(
            string name, bool isFolder, string destFolder, bool offerAll)
        {
            if (Cancelled) return null;
            if (_sticky is { } sticky) return sticky;
            var (choice, all) = await ExplorerConflictDialog.AskAsync(
                _ui.Dispatcher, _ui.Root, name, isFolder, destFolder, offerAll);
            if (choice is null)
            {
                Cancelled = true;
                return null;
            }
            if (all) _sticky = choice;
            return choice;
        }

        /// <summary>최상위 항목 진행 문구 "Copying 3 of 12..." — 항목 시작마다, 100ms 스로틀(마지막은 강제).</summary>
        internal void ReportProgress(int current)
        {
            if (_total < ProgressMinItems) return;
            var now = DateTime.UtcNow;
            if (current != _total && (now - _lastProgressAt).TotalMilliseconds < ProgressThrottleMs) return;
            _lastProgressAt = now;
            _ui.Post($"{(_move ? "Moving" : "Copying")} {current} of {_total}...");
        }
    }

    /// <summary>
    /// 현재 충돌(1) + 남은 최상위 항목 중 대상에 같은 이름이 이미 있는 것의 수 —
    /// "Do this for all remaining conflicts" 체크박스 노출 판정용. 2 이상이면 충분해 조기 종료.
    /// </summary>
    private static int KnownConflictsAhead(IReadOnlyList<string> paths, int fromIndex, string target)
    {
        var count = 1;
        for (var j = fromIndex + 1; j < paths.Count && count < 2; j++)
        {
            try
            {
                var name = Path.GetFileName(TrimSep(Path.GetFullPath(paths[j])));
                if (name.Length == 0) continue;
                var dest = Path.Combine(target, name);
                if (File.Exists(dest) || Directory.Exists(dest)) count++;
            }
            catch
            {
                // 판정 불가 항목은 세지 않는다 — 체크박스가 안 뜨는 쪽으로만 어긋난다
            }
        }
        return count;
    }

    /// <summary>
    /// 단일 항목 실행부(충돌 판정 없음 — 호출부가 dest를 확정한 뒤). overwrite = Replace의
    /// 파일 덮어쓰기(폴더 병합은 MergeDirectoryAsync 몫 — 폴더 + overwrite 조합은 대상이
    /// 파일일 때뿐이고, 그 경우 CopyDirectory가 던져 항목 실패로 격리된다).
    /// </summary>
    private static void TransferItem(string src, string dest, bool isFolder, bool move, bool overwrite = false)
    {
        if (isFolder)
        {
            if (!move) CopyDirectory(src, dest);
            else if (SameVolume(src, dest)) Directory.Move(src, dest);
            else
            {
                CopyDirectory(src, dest); // Directory.Move는 볼륨을 못 넘는다
                Directory.Delete(src, recursive: true);
            }
            return;
        }
        TransferFile(src, dest, move, overwrite);
    }

    /// <summary>파일 1개 이동/복사. overwrite면 File.Copy/File.Move의 덮어쓰기 오버로드를 쓴다.</summary>
    private static void TransferFile(string src, string dest, bool move, bool overwrite = false)
    {
        if (!move)
        {
            File.Copy(src, dest, overwrite);
            return;
        }
        if (SameVolume(src, dest))
        {
            if (overwrite) File.Move(src, dest, overwrite: true);
            else File.Move(src, dest);
            return;
        }
        File.Copy(src, dest, overwrite); // 명시 복사+삭제 — 실패 시 원본 보존이 명확하다
        File.Delete(src);
    }

    /// <summary>
    /// 폴더 충돌의 Replace = 병합 (A94 3차, 탐색기 동등): 대상 폴더를 지우지 않고 내용을 재귀
    /// 복사/이동한다. 내부 "파일" 충돌은 같은 정책 흐름(all 선택 존중, 없으면 그 파일에 대해
    /// 대화상자 — 체크박스는 항상 노출: 남은 내부 충돌 수는 미지수), 내부 "폴더" 충돌은 묻지
    /// 않고 자동 병합(하위로 계속). 이동 병합은 내용을 옮긴 뒤 "빈" 원본 폴더만 지운다 —
    /// Skip·실패로 항목이 남으면 원본 폴더 유지(탐색기 동등). 취소는 그 지점에서 멈춘다
    /// (수행분 유지·원본 정리도 하지 않는다). 반환 = (내부 전체 성공 여부, 첫 오류,
    /// 실패 중 권한 부족이 있었는지 — A94 4차: 호출부가 그 최상위 항목을 접근 거부 1건으로 센다).
    /// 순환 가드는 1차 가드 재사용 — 최상위에서 대상이 원본 하위면 애초에 오지 않고,
    /// 병합은 이미 있던 대상 폴더로만 내려가 새 순환을 만들 수 없다.
    /// </summary>
    private static async Task<(bool Ok, string? FirstError, bool Denied)> MergeDirectoryAsync(
        TransferOp op, string src, string dest)
    {
        var ok = true;
        var denied = false;
        string? firstError = null;
        void Fail(Exception ex) => FailWith(ex.Message, IsAccessDenied(ex));
        void FailWith(string message, bool wasDenied)
        {
            ok = false;
            denied |= wasDenied;
            firstError ??= message;
        }

        List<string> files, dirs;
        try
        {
            // 스냅샷 — 이동 병합은 순회 중 원본에서 항목을 빼내므로 라이브 열거가 깨진다.
            // ※ A160(v0.169.0) 확인 사항: 표시는 숨김/시스템을 거르지만(ExplorerListing.ShouldShow)
            // 조작은 거르지 않는다 — 이 열거도 아래 CopyDirectory도 속성 필터가 없어, 감춰진 항목까지
            // 함께 복사·이동·삭제된다(탐색기 동등 동작이라 A160 범위에서 고치지 않았다. 되짚지 말 것).
            files = Directory.EnumerateFiles(src).ToList();
            dirs = Directory.EnumerateDirectories(src).ToList();
        }
        catch (Exception ex)
        {
            return (false, ex.Message, IsAccessDenied(ex));
        }

        foreach (var file in files)
        {
            if (op.Cancelled) return (ok, firstError, denied);
            try
            {
                var name = Path.GetFileName(file);
                var destFile = Path.Combine(dest, name);
                if (File.Exists(destFile) || Directory.Exists(destFile))
                {
                    var choice = await op.AskAsync(name, isFolder: false, dest, offerAll: true);
                    if (choice is null) return (ok, firstError, denied); // 취소 — 원본 정리 없이 중단
                    if (choice == ConflictChoice.Skip) continue; // 이동이면 원본 파일·폴더 잔류
                    if (choice == ConflictChoice.KeepBoth)
                    {
                        TransferFile(file, UniqueDestination(dest, name), op.Move);
                        continue;
                    }
                    TransferFile(file, destFile, op.Move, overwrite: true); // Replace
                    continue;
                }
                TransferFile(file, destFile, op.Move);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }
        foreach (var dir in dirs)
        {
            if (op.Cancelled) return (ok, firstError, denied);
            try
            {
                var name = Path.GetFileName(dir);
                var destDir = Path.Combine(dest, name);
                if (Directory.Exists(destDir))
                {
                    var (subOk, subError, subDenied) = await MergeDirectoryAsync(op, dir, destDir);
                    if (!subOk) FailWith(subError ?? $"could not merge \"{name}\"", subDenied);
                }
                else if (File.Exists(destDir))
                {
                    // 폴더 자리의 동명 파일 — 탐색기도 오류(권한 문제가 아니다)
                    FailWith($"a file named \"{name}\" is in the way", false);
                }
                else if (!op.Move)
                {
                    CopyDirectory(dir, destDir);
                }
                else if (SameVolume(dir, destDir))
                {
                    Directory.Move(dir, destDir);
                }
                else
                {
                    CopyDirectory(dir, destDir);
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        if (op.Move && !op.Cancelled)
        {
            try
            {
                // 빈 원본만 삭제(비재귀) — Skip·실패 잔여물이 있으면 남긴다(탐색기 동등)
                if (!Directory.EnumerateFileSystemEntries(src).Any()) Directory.Delete(src);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }
        return (ok, firstError, denied);
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
