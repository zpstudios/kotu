using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using KOTU.Core.Routing;
using KOTU.Core.Settings;
using KOTU.Input;

namespace KOTU.App.Overlays;

/// <summary>
/// 파일 리스트 패널 공용 컨트롤 (A57 ②) — 기존 MainWindow의 AltOverlayRoot(우측,
/// v0.25.0)를 추출해 좌측으로 스왑(A57 ①)한 것. 내부는 ExplorerPane 리스트 전용 모드 재사용.
/// 용어(A108): 사이드바 = 불투명(OpaqueDocked) / 오버레이 = 반투명(홀드·고정).
/// 패널 폭은 전 상태 공통 25%(A116 — 종전 "콘텐츠 30% / S1 25%" 2값 폐지, SetPanelPercent).
/// 컨텍스트는 모듈이 주입한다: Show(folder, extensions)의 확장자 목록이 모듈별 필터(A57 ③)가 되고,
/// ExplorerPane의 A7 드롭다운은 그 안에서 추가로 좁힌다. 적용 대상은 파일 모듈
/// (Image·Video·Audio·Document·Archive) — 정보(H/W)·설정 모듈은 셸이 파일 경로가 없어
/// 애초에 Show를 부르지 않는다(현행 동작 유지).
/// 상단 25%는 디스크 계층 트리(A57 ④): 폴더만 표시, 노드 펼침 시점 지연 로드,
/// Show() 시 현재 폴더까지 자동 펼침·선택·스크롤. 트리 선택은 하단 리스트를 그 폴더로 옮긴다
/// (NavigateTo 재사용이라 A5 정렬·A7 필터·A8 경로 표시가 그대로 따라온다).
/// 입력(A86 — A58의 Alt를 Z로 대체: Z 홀드 = 오버레이 / 2초 = 오버레이 고정 / 2연타 = 사이드바 /
/// 열림 상태에서 Z 1회 = 닫기)은 셸(MainWindow)의 상태 머신이 담당한다 —
/// 이 컨트롤은 Show/Hide/SetState만 받는다.
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

    /// <summary>
    /// 내부 리스트(ExplorerPane)의 표시 목록이 다시 그려질 때 폴더 경로와 함께 그대로 전달 (A93 —
    /// 폴더 인자는 A94: 썸네일 뷰가 드랍·붙여넣기 대상 폴더를 알아야 한다).
    /// 셸이 구독해 S1 중앙 썸네일 뷰(ThumbnailExplorer)를 같은 목록으로 갱신한다 —
    /// 좌 리스트와 중앙이 폴더·필터(A7)·정렬(A5) 상태를 공유하는 유일한 통로.
    /// </summary>
    public event Action<string, IReadOnlyList<ExplorerListing.Entry>>? ViewChanged;

    /// <summary>정렬 키 저장용(A5) — 셸이 리스트 첫 생성 전에 주입한다. 없어도 동작(기본 이름순).</summary>
    public ISettingsService? Settings { get; set; }

    /// <summary>오버레이가 화면에 떠 있는지 — 셸의 표시 갱신·경계 버튼 위치 판단에 쓴다.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>
    /// 떠 있는 동안의 선택 파일 경로 (A86 — 셸 Enter의 "선택 파일 있으면 열기" 판정).
    /// 닫혀 있거나(보이지 않는 선택은 열지 않는다) 선택이 폴더·없음이면 null.
    /// ※ A94 6차(v0.153.0)부터 셸 Enter는 <see cref="OpenSelectedFiles"/>(일괄 열기)를 쓴다 —
    /// 이 속성은 "첫 선택 파일" 질의 API로만 남았다(A86 서술의 원형).
    /// </summary>
    public string? SelectedFilePath => IsOpen ? _list?.SelectedFilePath : null;

    /// <summary>
    /// 떠 있는 동안의 선택 파일 일괄 열기 (A94 6차 — 셸 Enter가 부른다). 종전 "SelectedFilePath
    /// 하나를 OpenFileRouted"의 대체: 첫 파일은 재사용 규칙(A24) 경로, 나머지는 새 인스턴스
    /// (상한 10 — ExplorerPane.OpenSelectedFiles). 닫혀 있으면 아무것도 하지 않는다 —
    /// 보이지 않는 선택은 열지 않는다(SelectedFilePath와 같은 규칙).
    /// 반환 = 하나라도 열었는지(false면 셸이 종전 폴백으로 간다).
    /// </summary>
    public bool OpenSelectedFiles() => IsOpen && _list is { } list && list.OpenSelectedFiles();

    public FileListOverlay()
    {
        InitializeComponent();
        // A34: 폴더 트리에 포커스가 있는 동안에도 모듈 버튼 핫키는 통과시킨다
        // (트리 타이핑 탐색 우선 — 하단 리스트는 ExplorerPane이 같은 표시를 건다).
        FolderTree.Tag = HotkeySupport.PassThroughTag;
    }

    /// <summary>
    /// 모듈 컨텍스트를 주입받아 표시한다: folder = 현재 파일의 폴더,
    /// extensions = 모듈 담당 확장자(IModule.SupportedExtensions — A57 ③ 모듈별 필터).
    /// 이미 떠 있으면 폴더·필터만 갱신한다(모듈 전환 시 ExplorerPane이 A7 필터를 재구성).
    /// </summary>
    public void Show(string folder, IReadOnlyList<string> extensions)
    {
        NavigateList(folder, extensions);
        Visibility = Visibility.Visible;

        EnsureDriveRoots(); // A57 ④ — 드라이브 구성이 바뀌었으면(USB 등) 루트 재구성
        _ = ExpandToFolderAsync(folder);
    }

    /// <summary>
    /// 표시 여부를 바꾸지 않고 리스트만 만들어 항해시킨다 (A93 — Show에서 분리).
    /// S1에서는 좌 도크가 닫혀 있어도 이 리스트가 폴더 상태의 원본이라, 셸이 중앙 썸네일 뷰를
    /// 채우기 위해 이 경로를 쓴다(결과는 ViewChanged로 돌아온다). extensions를 생략하면
    /// 마지막 모듈 필터 유지 — 썸네일 뷰의 폴더 더블클릭이 이 형태로 온다.
    /// </summary>
    public void NavigateList(string folder, IReadOnlyList<string>? extensions = null)
    {
        if (extensions is not null) _extensions = extensions;
        if (_list is null)
        {
            _list = new ExplorerPane { Settings = Settings }; // 정렬 키 저장(A5)
            _list.ConfigureListOnly();
            // A91(v0.115.0): 주소·필터 줄을 리스트에서 떼어 패널 최상단(트리 위)에 올린다.
            // Child가 비어 있을 때만 붙인다 — 같은 UIElement를 두 부모에 넣으면 죽는다.
            // (FrameworkElement.Parent는 라이브 트리 부착 전 null이라 중복 가드로 못 쓴다:
            //  v0.111.0 COMException 0x800F1000 전례. DetachPathBar도 멱등이라 이중 안전.)
            PathBarHost.Child ??= _list.DetachPathBar();
            _list.FileActivated += path => FileActivated?.Invoke(path);
            _list.FileActivatedNewWindow += path => FileActivatedNewWindow?.Invoke(path);
            _list.ViewChanged += (folder, entries) => ViewChanged?.Invoke(folder, entries); // A93 — 중앙 썸네일 동기화
            _list.Notice += ShowTransientNotice; // A94 — 리스트 항목 드랍·클립보드 실패 안내
            ListHost.Content = _list;
        }
        _list.NavigateTo(folder, _extensions);
    }

    /// <summary>
    /// 패널 폭(전폭 대비 %) 지정 — 셸이 전 상태 공통 SidebarPercent(25, A116)를 넘긴다.
    /// 내부 별 분할이 셸 도크 컬럼과 같은 비율이어야 사이드바에서 픽셀 단위로 정렬되고,
    /// 경계 버튼 옆 안내 문구(A108 — RestColumn 기준 배치)의 x도 이 분할이 정한다.
    /// ContentInfoOverlay에도 같은 메서드가 있다.
    /// </summary>
    public void SetPanelPercent(double percent)
    {
        PanelColumn.Width = new GridLength(percent, GridUnitType.Star);
        RestColumn.Width = new GridLength(100 - percent, GridUnitType.Star);
    }

    /// <summary>
    /// 좌 패널 드래그 = 현재 폴더로 이동/복사 (A94 1차, v0.124.0 — A93의 무동작 소비를 실동작으로
    /// 전환. ThumbnailExplorer의 중앙 영역 핸들러와 같은 규칙). 폴더 항목 위는 내부 리스트
    /// (ExplorerPane)의 항목 핸들러가 먼저 Handled로 받아 그 폴더가 대상이 된다.
    /// 리스트가 아직 없으면 None으로 소비만 — 어느 쪽이든 Handled라 창 전체 "열기" 폴백에
    /// 안 넘어간다(OnWindowDrop).
    /// </summary>
    private void OnPanelDragOver(object sender, DragEventArgs e) =>
        ExplorerFileOps.HandleTargetDragOver(e, _list?.CurrentFolder);

    /// <summary>빈 영역·파일 항목·트리 영역 드랍 — 대상 = 현재 폴더 (A94).</summary>
    private async void OnPanelDrop(object sender, DragEventArgs e)
    {
        e.Handled = true; // 창 수준 라우팅과의 이중 처리 방지 (await 전에 동기로 지정해야 유효)
        if (_list?.CurrentFolder is not { Length: > 0 } folder) return;
        var operation = ExplorerFileOps.DecideOperation(e, folder);
        if (operation == DataPackageOperation.None ||
            !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        e.AcceptedOperation = operation; // 소스(OS 탐색기 등)에 확정 동작을 알린다

        // A94 3차 — 충돌 대화상자·진행 문구용 UI 문맥(조작 시작 시점의 이 창 기준 캡처).
        // 4차 — 접근 거부(UAC 필요) 안내·관리자 재시작 제안도 같은 문맥으로 간다.
        var ui = new ExplorerFileOps.OpUi(DispatcherQueue, XamlRoot, ShowTransientNotice);
        var move = operation == DataPackageOperation.Move;
        var result = await ExplorerFileOps.TransferDroppedAsync(e.DataView, folder, move, ui);
        NavigateList(folder); // 단일 원본(내부 리스트) 재스캔 — ViewChanged로 중앙 썸네일까지 갱신
        await ExplorerFileOps.ReportAsync(result.Notice(move), result.Denied, ui);
    }

    /// <summary>
    /// 파일 조작 실패 안내(A94): A92 힌트 표시 경로 재사용 — 상태를 비우고 다시 띄워
    /// 같은 문구의 연속 실패에도 다시 보이게 한다(ShowHint의 동일 문구 중복 억제 우회).
    /// </summary>
    private void ShowTransientNotice(string text)
    {
        HideHint();
        ShowHint(text);
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        HideHint(); // A92 — 다시 열릴 때 안내가 처음부터 다시 보이게 상태를 비운다
    }

    /// <summary>
    /// 표시 모드·고정 안내 반영 (A58 — v0.32.0 SetPinned 대체).
    /// TranslucentOver = 오버레이(아크릴 반투명, A33 — A108 용어): 홀드 중이면 문구 없음,
    /// pinned(2초 홀드 고정)면 unpin 안내. OpaqueDocked = 사이드바(불투명 배경) + close 안내 —
    /// 실제 폭 차지(메인 축소)는 셸의 도크 컬럼이 담당하고 여기서는 시각·문구만 바꾼다.
    /// 문구는 실제 표시 상태(IsOpen) 기준 — 폴더 부재 등으로 Show가 못 떴으면 숨긴 채 둔다.
    /// A92(v0.115.0): 문구는 상시 표시가 아니라 잠깐 보였다 사라진다(아래 안내 문구 절 참고).
    /// A108(v0.135.0): 문구 위치는 패널 하단이 아니라 경계 버튼 옆(XAML PinnedText 배치 참고).
    /// </summary>
    public void SetState(OverlayMode mode, bool pinned)
    {
        var docked = mode == OverlayMode.OpaqueDocked;
        PanelBorder.Background = (Brush)Application.Current.Resources[
            docked ? "SolidBackgroundFillColorBaseBrush" : "OverlayAcrylicBrush"];
        if (IsOpen && (docked || pinned))
            ShowHint(docked
                ? OverlayHints.Docked(OverlayHints.ListKey)
                : OverlayHints.Pinned(OverlayHints.ListKey));
        else
            HideHint();
    }

    // ---------- 안내 문구 일시 표시 (A92, v0.115.0 — 문구·키 표기는 A107부터 OverlayHints가 단일 출처) ----------
    // ⚠️ ContentInfoOverlay·SidePanelHost(A119)에 같은 상수·필드·메서드(표시 타이밍 장치)가 한 벌씩
    // 더 있다. 문구 문자열은 A107에서 OverlayHints로 모았지만 타이밍 장치는 세 벌 —
    // 한쪽을 고치면 반드시 나머지도 맞출 것.
    // A108(v0.135.0): 표시 위치가 패널 하단 → 경계 버튼 옆(세로 중앙)으로 이동 — XAML만 바뀌었고
    // 타이밍 장치는 그대로다. PinnedText를 재사용하는 A94 실패 안내(ShowTransientNotice)도
    // 같은 자리에 뜬다(요소 하나 = 위치 하나 — A107 단일화 유지의 의도된 결과).

    private const double HintOpacity = 0.6; // XAML PinnedText.Opacity와 같아야 한다(페이드 후 되돌릴 값)
    private static readonly TimeSpan HintHoldFor = TimeSpan.FromSeconds(2.5);      // 표시 시간(구현 시 결정)
    private static readonly TimeSpan HintFadeFor = TimeSpan.FromMilliseconds(300); // 페이드아웃 시간

    private DispatcherTimer? _hintTimer; // UI 스레드 타이머 (MainWindow.MakePinTimer·DriveStrip과 같은 방식)
    private Storyboard? _hintFade;
    private bool _hintVisible;    // 지금 "보여야 하는 상태"인가 — 매 SetState마다 되감지 않기 위한 기억
    private string? _hintText;    // 마지막으로 띄운 문구 — 내용이 바뀔 때만 다시 띄운다

    /// <summary>
    /// 안내를 잠깐 띄운다: 2.5초 표시 → 300ms 페이드아웃 → Collapsed.
    /// SetState는 상태 머신이 움직일 때마다 여러 번 불리므로, **표시 상태로 새로 진입했거나
    /// 문구가 바뀐 경우에만** 다시 띄우고 타이머를 되감는다(매번 재시작하면 영영 안 사라진다).
    /// </summary>
    private void ShowHint(string text)
    {
        if (_hintVisible && _hintText == text) return; // 이미 이 안내를 낸 뒤 — 그대로 둔다(사라진 채라도)
        _hintVisible = true;
        _hintText = text;

        StopHint(); // 돌던 타이머·페이드를 먼저 정리해야 아래 Opacity 대입이 애니메이션에 눌리지 않는다
        PinnedText.Text = text;
        PinnedText.Opacity = HintOpacity; // 직전 페이드로 0이 된 채 남아 있을 수 있다
        PinnedText.Visibility = Visibility.Visible;

        _hintTimer ??= CreateHintTimer();
        _hintTimer.Stop();  // DispatcherTimer는 반복 타이머 — Stop 후 Start로 확실히 되감는다
        _hintTimer.Start();
    }

    /// <summary>숨겨야 하는 상태(닫힘·도크도 고정도 아님) — 타이머·페이드를 즉시 멈추고 감춘다.</summary>
    private void HideHint()
    {
        _hintVisible = false;
        _hintText = null;
        StopHint();
        PinnedText.Visibility = Visibility.Collapsed;
    }

    private DispatcherTimer CreateHintTimer()
    {
        var timer = new DispatcherTimer { Interval = HintHoldFor };
        timer.Tick += (_, _) =>
        {
            timer.Stop(); // 반복 타이머라 Tick 안에서 반드시 멈춘다(MainWindow.MakePinTimer와 같은 관용구)
            FadeOutHint();
        };
        return timer;
    }

    /// <summary>Storyboard + DoubleAnimation(Opacity) — DriveStrip 마퀴와 같은 관용구.</summary>
    private void FadeOutHint()
    {
        var animation = new DoubleAnimation
        {
            From = HintOpacity,
            To = 0,
            Duration = new Duration(HintFadeFor),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, PinnedText);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var fade = new Storyboard();
        fade.Children.Add(animation);
        fade.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_hintFade, fade)) return; // 그새 다시 띄워졌다 — 감추면 안 된다
            PinnedText.Visibility = Visibility.Collapsed;
        };
        _hintFade = fade;
        fade.Begin();
    }

    private void StopHint()
    {
        _hintTimer?.Stop();
        _hintFade?.Stop(); // Stop은 Completed를 부르지 않는다 — 보류 중인 Collapsed도 함께 사라진다
        _hintFade = null;
    }

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
