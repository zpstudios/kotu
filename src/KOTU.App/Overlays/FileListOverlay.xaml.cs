using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using KOTU.Core.Settings;

namespace KOTU.App.Overlays;

/// <summary>
/// 파일 리스트 오버레이 공용 컨트롤 (A57 ②) — 기존 MainWindow의 AltOverlayRoot(우측 30%,
/// v0.25.0)를 추출해 좌측 30%로 스왑(A57 ①)한 것. 내부는 ExplorerPane 리스트 전용 모드 재사용.
/// 컨텍스트는 모듈이 주입한다: Show(folder, extensions)의 확장자 목록이 모듈별 필터(A57 ③)가 되고,
/// ExplorerPane의 A7 드롭다운은 그 안에서 추가로 좁힌다. 적용 대상은 파일 모듈
/// (Image·Video·Audio·Document·Archive) — 정보(H/W)·설정 모듈은 셸이 파일 경로가 없어
/// 애초에 Show를 부르지 않는다(현행 동작 유지).
/// 상단 25%는 디스크 계층 트리(A57 ④): 폴더만 표시, 노드 펼침 시점 지연 로드,
/// Show() 시 현재 폴더까지 자동 펼침·선택·스크롤. 트리 선택은 하단 리스트를 그 폴더로 옮긴다
/// (NavigateTo 재사용이라 A5 정렬·A7 필터·A8 경로 표시가 그대로 따라온다).
/// 입력(Alt 홀드·2연타 고정, A32)은 셸(MainWindow)이 그대로 담당한다 — A58에서 별도 대체 예정.
/// </summary>
public sealed partial class FileListOverlay : UserControl
{
    private ExplorerPane? _list; // 지연 생성 (기존 MainWindow._altList와 동일 수명)
    private IReadOnlyList<string> _extensions = []; // 마지막 Show()의 모듈 필터 — 트리 이동에 재사용
    private int _expandSeq; // 연속 Show() 시 늦은 자동 펼침 폐기

    /// <summary>파일 더블클릭 열기 — 셸이 재사용 규칙(A24)을 적용해 라우팅한다.</summary>
    public event Action<string>? FileActivated;

    /// <summary>명시적 새 창 열기(A24: Shift+더블클릭·우클릭 메뉴) — 셸이 항상 새 창으로.</summary>
    public event Action<string>? FileActivatedNewWindow;

    /// <summary>정렬 키 저장용(A5) — 셸이 리스트 첫 생성 전에 주입한다. 없어도 동작(기본 이름순).</summary>
    public ISettingsService? Settings { get; set; }

    /// <summary>오버레이가 화면에 떠 있는지 — 셸의 표시 갱신·Alt 키 소비 판단에 쓴다.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    public FileListOverlay()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 모듈 컨텍스트를 주입받아 표시한다: folder = 현재 파일의 폴더,
    /// extensions = 모듈 담당 확장자(IModule.SupportedExtensions — A57 ③ 모듈별 필터).
    /// 이미 떠 있으면 폴더·필터만 갱신한다(모듈 전환 시 ExplorerPane이 A7 필터를 재구성).
    /// </summary>
    public void Show(string folder, IReadOnlyList<string> extensions)
    {
        _extensions = extensions;
        if (_list is null)
        {
            _list = new ExplorerPane { Settings = Settings }; // 정렬 키 저장(A5)
            _list.ConfigureListOnly();
            _list.FileActivated += path => FileActivated?.Invoke(path);
            _list.FileActivatedNewWindow += path => FileActivatedNewWindow?.Invoke(path);
            ListHost.Content = _list;
        }
        _list.NavigateTo(folder, extensions);
        Visibility = Visibility.Visible;

        EnsureDriveRoots(); // A57 ④ — 드라이브 구성이 바뀌었으면(USB 등) 루트 재구성
        _ = ExpandToFolderAsync(folder);
    }

    public void Hide() => Visibility = Visibility.Collapsed;

    /// <summary>고정(2연타) 안내 문구 표시 — 떠 있는 상태에서 고정됐을 때만 보인다(v0.32.0).</summary>
    public void SetPinned(bool pinned) =>
        PinnedText.Visibility = IsOpen && pinned ? Visibility.Visible : Visibility.Collapsed;

    // ---------- 디스크 계층 트리 (A57 ④) ----------

    private const int TreeChildLimit = 1000; // 초대형 폴더 보호 — 하위 폴더 노드 상한

    /// <summary>트리 노드 콘텐츠. 기본 항목 템플릿이 ToString()을 표시하므로 Display를 돌려준다.</summary>
    private sealed record FolderNode(string Path, string Display)
    {
        public override string ToString() => Display;
    }

    /// <summary>
    /// 루트(드라이브) 노드를 채운다. 이미 같은 드라이브 구성이면 그대로 두어(펼침 상태 유지),
    /// 탈착 등으로 구성이 바뀌었을 때만 다시 만든다. 드라이브는 이름+종류만 간단 표기.
    /// </summary>
    private void EnsureDriveRoots()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            return; // 드라이브 열거 실패 — 기존 트리 유지
        }

        var current = FolderTree.RootNodes
            .Select(n => (n.Content as FolderNode)?.Path)
            .Where(p => p is not null)
            .ToList();
        if (current.Count == drives.Length &&
            drives.Select(d => d.RootDirectory.FullName)
                  .All(p => current.Contains(p, StringComparer.OrdinalIgnoreCase)))
            return;

        FolderTree.RootNodes.Clear();
        foreach (var drive in drives)
        {
            string root, display;
            try
            {
                root = drive.RootDirectory.FullName;
                display = $"{drive.Name.TrimEnd('\\')} ({DriveKind(drive.DriveType)})";
            }
            catch
            {
                continue; // 접근 불가 드라이브는 조용히 생략
            }
            FolderTree.RootNodes.Add(new TreeViewNode
            {
                Content = new FolderNode(root, display),
                HasUnrealizedChildren = true, // 하위는 펼칠 때 로드
            });
        }
    }

    private static string DriveKind(DriveType type) => type switch
    {
        DriveType.Fixed => "Local Disk",
        DriveType.Removable => "Removable",
        DriveType.Network => "Network",
        DriveType.CDRom => "CD-ROM",
        DriveType.Ram => "RAM Disk",
        _ => "Drive",
    };

    /// <summary>노드 펼침 시점의 지연 로드 — 전체 스캔 없이 그 단계 하위 폴더만 열거한다.</summary>
    private async void OnTreeExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        await LoadChildrenAsync(args.Node);
    }

    /// <summary>
    /// 하위 폴더 노드를 한 단계 채운다. 숨김/시스템 폴더는 탐색기 리스트와 같은 기준으로 제외
    /// (ExplorerListing.List와 동일), 접근 불가 폴더(권한·미준비 드라이브)는 조용히 생략.
    /// HasUnrealizedChildren을 먼저 내려 재진입(자동 펼침과 Expanding 이벤트 중복)을 막는다.
    /// </summary>
    private async Task LoadChildrenAsync(TreeViewNode node)
    {
        if (!node.HasUnrealizedChildren || node.Content is not FolderNode folder) return;
        node.HasUnrealizedChildren = false;

        string[] children;
        try
        {
            children = await Task.Run(() =>
                new DirectoryInfo(folder.Path).EnumerateDirectories()
                    .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(TreeChildLimit)
                    .Select(d => d.FullName)
                    .ToArray());
        }
        catch
        {
            return; // 권한 등으로 못 읽는 폴더 — 빈 채로 둔다(펼침 표시만 사라짐)
        }

        foreach (var path in children)
            node.Children.Add(new TreeViewNode
            {
                Content = new FolderNode(path, Path.GetFileName(path)),
                HasUnrealizedChildren = true, // 실제 하위 유무는 펼칠 때 판명 — 미리 조사하지 않는다
            });
    }

    /// <summary>
    /// 현재 폴더까지 경로를 자동으로 펼치고 그 노드를 선택·스크롤한다(A57 ④ Show() 시).
    /// 각 단계는 LoadChildrenAsync로 지연 로드하며, 숨김 폴더 등으로 경로가 끊기면 거기까지만 간다.
    /// </summary>
    private async Task ExpandToFolderAsync(string folder)
    {
        var seq = ++_expandSeq;
        string full;
        try
        {
            full = Path.GetFullPath(folder);
        }
        catch
        {
            return;
        }

        var node = FolderTree.RootNodes.FirstOrDefault(n =>
            n.Content is FolderNode f &&
            full.StartsWith(f.Path, StringComparison.OrdinalIgnoreCase));
        if (node is null) return; // 다른 볼륨(UNC 등) — 트리는 드라이브만 안다

        while (node.Content is FolderNode current &&
               !string.Equals(TrimSep(current.Path), TrimSep(full), StringComparison.OrdinalIgnoreCase))
        {
            await LoadChildrenAsync(node);
            if (seq != _expandSeq) return; // 그새 다른 폴더로 Show()됨
            node.IsExpanded = true;

            var next = node.Children.FirstOrDefault(c =>
                c.Content is FolderNode f &&
                (string.Equals(TrimSep(f.Path), TrimSep(full), StringComparison.OrdinalIgnoreCase) ||
                 full.StartsWith(TrimSep(f.Path) + Path.DirectorySeparatorChar,
                     StringComparison.OrdinalIgnoreCase)));
            if (next is null) break; // 숨김 폴더 경유 등 — 도달한 지점까지만 선택
            node = next;
        }

        FolderTree.SelectedNode = node;
        ScrollTreeTo(node);
    }

    private static string TrimSep(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// 선택 노드가 보이게 스크롤한다. TreeView에 공개 API가 없어 내부 리스트(TreeViewList는
    /// ListView 파생)를 비주얼 트리에서 찾아 ScrollIntoView를 부른다 — 못 찾으면 조용히 넘어간다.
    /// </summary>
    private void ScrollTreeTo(TreeViewNode node)
    {
        FolderTree.UpdateLayout(); // 방금 추가한 노드의 컨테이너 실체화
        if (FindTreeList(FolderTree) is { } list) list.ScrollIntoView(node);
    }

    private static ListView? FindTreeList(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ListView list) return list;
            if (FindTreeList(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>트리에서 드라이브/폴더 선택 → 하단 리스트를 그 폴더로 이동(모듈 필터 유지).</summary>
    private void OnTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        var content = args.InvokedItem is TreeViewNode node ? node.Content : args.InvokedItem;
        if (content is FolderNode folder && _list is not null)
            _list.NavigateTo(folder.Path, _extensions);
    }
}
