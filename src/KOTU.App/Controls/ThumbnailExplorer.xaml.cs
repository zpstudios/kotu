using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.System;
using KOTU.Core.Routing;
using KOTU.Input;

namespace KOTU.App.Controls;

/// <summary>
/// 중앙 썸네일 탐색기 뷰 (A93) — S1(콘텐츠 없음·모듈만 실행)의 중앙 구획.
/// A81(v0.101.0)의 "좌 도크 열림 시 중앙 탐색기 숨김"을 대체한다 — S1 중앙은 항상 이 뷰다.
/// 목록은 좌 도크 리스트(ExplorerPane)가 원본: 정렬(A5)·필터(A7)가 적용된 표시 목록을
/// 셸이 ShowEntries로 밀어 넣는다(ExplorerPane.ViewChanged 경유) — 어느 쪽에서 폴더를 바꿔도
/// 둘 다 같은 목록을 그린다. 더블클릭 열기·새 인스턴스 이벤트도 ExplorerPane과 같은 배선이라
/// 셸의 기존 라우팅(OpenFileRouted·A24)을 그대로 쓴다.
/// 열 수 = 8 − 2×(열린 도크 수) → 둘 다 열림 4 · 하나 6 · 없음 8(A213 — 구 A93의 4/8 2단 개정.
/// A63 대체 계보: 크기 고정·열 수 가변이던 종전 규칙을 열 수 고정·크기 가변으로 뒤집은 위에
/// 3단화). 타일 한 변 = floor(실폭/열수).
/// </summary>
public sealed partial class ThumbnailExplorer : UserControl
{
    /// <summary>이미지 미리보기 디코드 폭 상한(물리 px) — 원본 크기 디코드로 메모리가 폭주하지 않게.</summary>
    private const int PreviewDecodeWidth = 256;

    /// <summary>더블클릭 판정 창 — ExplorerPane.DoubleClickMs와 같은 값(같은 감각).</summary>
    private const int DoubleClickMs = 500;

    /// <summary>
    /// A192 체감 조정 지점: 분할 조립 조각 크기(타일 수) — 첫 즉시 조각과 프레임당 append 조각이
    /// 같은 값을 쓴다(확정 수치 60 — DocumentView.RenderChunkBlocks의 상수 배치 관용구.
    /// 되돌리기·조정은 이 상수 하나만 고치면 된다).
    /// </summary>
    private const int TileChunkItems = 60;

    /// <summary>
    /// A192: 타일 실체화 상한(ExplorerPane.MaterializeLimit와 같은 값) — 초과분은 타일을 만들지
    /// 않고 말미에 비상호작용 안내 1타일만 붙인다(MakeOverflowNotice). 상한은 컨테이너 실체화에만
    /// 걸린다 — ShowEntries로 받는 Entry 목록 자체는 전체 그대로다(원본은 좌 리스트, A93).
    /// </summary>
    private const int MaterializeLimit = 2000;

    /// <summary>폴더 더블클릭 — 셸이 좌 리스트를 그 폴더로 항해시킨다(상태 공유의 되돌이 경로).</summary>
    public event Action<string>? FolderActivated;

    /// <summary>파일 더블클릭 열기 — 셸이 재사용 규칙(A24)을 적용해 라우팅한다.</summary>
    public event Action<string>? FileActivated;

    /// <summary>명시적 새 창 열기(A24: Shift+더블클릭·우클릭 메뉴) — 셸이 항상 새 창으로.</summary>
    public event Action<string>? FileActivatedNewWindow;

    /// <summary>
    /// 타일 선택 변경 (A200) — 셸이 우측 정보 패널의 "선택 우선" 표시에 쓴다. 인자 없음:
    /// 셸이 <see cref="SelectedEntry"/>를 질의한다(선택 상태의 원본은 그리드 하나 — A86/A90의
    /// 질의 API 관례). 목록 재구축(ShowEntries)·다중 선택 조작에서도 그리드가 알아서 발화한다.
    /// </summary>
    public event Action? SelectionChanged;

    /// <summary>
    /// 파일 경로 → 담당 모듈 ID (액센트 색 타일용). 셸이 라우터로 주입한다 —
    /// 이 컨트롤이 FileTypeRouter를 직접 알면 DI 없이 못 만드는 컨트롤이 된다.
    /// </summary>
    public Func<string, string?>? ModuleIdForFile { get; set; }

    private int _columns = 8; // 기본 = 도크 둘 다 닫힘(전폭) 기준 — 셸이 곧 SetColumns(4/6/8)로 덮는다

    /// <summary>A192: 조립 재진입 가드 — ShowEntries가 올 때마다 증가(ExplorerPane._loadSeq 관용구).
    /// 진행 중 루프의 틱은 append 직전에 이 값과 대조해 낡은 조각을 버린다.</summary>
    private int _showSeq;

    /// <summary>
    /// A192: 분할 조립 루프의 프레임 틱 핸들러(null = 루프 없음). CompositionTarget.Rendering은
    /// static 이벤트라 뷰 수명 안에서 반드시 해제한다 — 남기면 닫힌 뷰가 통째로 누수된다
    /// (DocumentView._renderAppendHandler와 같은 사정). 해제의 단일 지점 = StopTileAppendLoop.
    /// 호출부 전수 = Unloaded·ShowEntries 기동 직전 방어·틱 내부(완료/seq 중단/예외).
    /// </summary>
    private EventHandler<object>? _tileAppendHandler;
    private (string Path, DateTime At)? _lastClick;
    private (string Path, DateTime At)? _lastActivation; // A85: ItemClick 쌍·DoubleTapped 겹침을 1회로 억제
    private (string Path, DateTime At)? _lastPress;      // A131: 원시 눌림 쌍 — 항목 재구축을 건너 살아남는 최후 폴백

    /// <summary>
    /// Ctrl+Shift+N(새 폴더) 직후의 편집 진입 예약 (A94 2차). 이 뷰의 재스캔은 좌 리스트 경유
    /// 비동기(FolderActivated → 셸 → ViewChanged → ShowEntries)라 완료 시점을 직접 기다릴 수 없다 —
    /// 다음으로 <b>완주한</b> 조립(FinishShowEntries — A192에서 분할 조립 완료 시점으로 이동)이
    /// 이 경로의 타일을 찾아 이름변경 편집으로 진입하고 지운다(1회성 — 그 타일이 뒤 조각에
    /// 있을 수 있어 조립 도중에는 소비하지 않는다).
    /// </summary>
    private string? _pendingRenamePath;

    /// <summary>
    /// 지금 그리고 있는 폴더 경로 (A94 — 빈 영역 드랍·붙여넣기의 대상). ShowEntries가 좌 리스트의
    /// ViewChanged에서 받은 폴더로 갱신한다 — 이 컨트롤은 폴더 상태의 원본이 아니다(A93).
    /// </summary>
    public string? CurrentFolder { get; private set; }

    /// <summary>
    /// 선택된 파일 타일의 경로 — 폴더·무선택이면 null (A86: 셸 Enter "선택 파일 있으면 열기").
    /// A94(Extended)부터 다중 선택이 가능하지만 이 속성은 첫 선택(SelectedItem) 기준을 유지한다.
    /// ※ A94 6차(v0.153.0)부터 일괄 열기는 <see cref="OpenSelectedFiles"/> —
    /// 이 속성은 "첫 선택 파일" 질의 API로만 남았다(A86 서술의 원형).
    /// </summary>
    public string? SelectedFilePath =>
        TileGrid.SelectedItem is FrameworkElement { Tag: ExplorerListing.Entry { IsFolder: false } entry }
            ? entry.Path : null;

    /// <summary>선택된 항목(파일·폴더 불문) — 없으면 null (A90: S4 Enter "선택 열기 우선" 판정).</summary>
    public ExplorerListing.Entry? SelectedEntry =>
        TileGrid.SelectedItem is FrameworkElement { Tag: ExplorerListing.Entry entry } ? entry : null;

    public ThumbnailExplorer()
    {
        InitializeComponent();
        // A34: 타일 그리드에 포커스가 있어도 모듈 버튼 핫키는 통과 — 타이핑 탐색(첫 글자 점프) 우선
        // (ExplorerPane의 IconGrid·ListPane과 같은 규칙). A90의 S4 키맵("A34 문자 핫키 = 무동작")도
        // 이 태그 하나로 충족된다 — S4 그리드에 포커스가 있는 동안 HotkeySupport가 전부 통과시킨다.
        TileGrid.Tag = HotkeySupport.PassThroughTag;
        // A90: Enter = 선택 항목 열기 (keymap S1 "선택 파일 있으면 열기"·S4 "선택 열기 우선"의
        // 그리드 쪽 구현). GridView의 기본 Enter 처리(ItemClick — 이 클래스에선 더블클릭 판정에만
        // 쓰여 단발 Enter로는 안 열린다)가 이벤트를 Handled로 만들 수 있어 handledEventsToo로 받는다
        // (MainWindow의 루트 KeyDown 구독과 같은 관용구).
        TileGrid.AddHandler(UIElement.KeyDownEvent,
            new KeyEventHandler(OnGridKeyDown), handledEventsToo: true);
        // A131: 원시 눌림 쌍 폴백 — 아래 두 더블클릭 판정(ItemClick 쌍·DoubleTapped)은 둘 다 항목
        // 컨테이너 수명에 묶여 있어, 두 클릭 사이·클릭 도중에 목록 재구축(A94 5차 폴더 감시 재스캔
        // 등 — ShowEntries가 타일을 전부 새로 만든다)이 끼면 눌림·뗌이 다른 요소가 되어 클릭이
        // 성립하지 않고(ItemClick 침묵) 새 컨테이너에는 제스처 상태가 없어 DoubleTapped도 뜨지
        // 않는다 — 열기 요청이 셸에 도달하지 못한 채 완전 침묵(압축 모듈 zip 무반응으로 관측).
        // 눌림은 요소 교체와 무관하게 매번 도착하므로 경로 키 판정이 재구축을 건너 살아남는다.
        // handledEventsToo = 리스트가 눌림을 소비해도 판정은 돌아야 한다(셸 A58 홀드 취소 구독과
        // 같은 관용구). Handled는 건드리지 않는다 — 순수 관찰(선택·드래그·제스처 무간섭).
        TileGrid.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnSurfacePointerPressed), handledEventsToo: true);
        // A94 4차: 잘라내기 표시(프로세스 전역 1벌)가 바뀌면 이미 그려 둔 타일의 흐림만 다시 맞춘다.
        // 구독을 Loaded/Unloaded로 묶는 이유 = 정적 이벤트가 닫힌 창의 컨트롤을 붙들지 않게
        // (ExplorerPane과 같은 수명 규칙). 중복 구독은 -= 선행으로 막는다.
        Loaded += (_, _) =>
        {
            ExplorerFileOps.CutMarksChanged -= ApplyCutMarks;
            ExplorerFileOps.CutMarksChanged += ApplyCutMarks;
        };
        Unloaded += (_, _) =>
        {
            ExplorerFileOps.CutMarksChanged -= ApplyCutMarks;
            StopTileAppendLoop(); // A192 — CompositionTarget.Rendering은 static: 남기면 닫힌 뷰 통째 누수
        };
        // A200: 선택 변경을 셸로 중계 — 우측 정보 패널의 선택 우선 표시(파일 정보 직접 조회)용.
        // 그리드 자체 이벤트를 얇게 감싸기만 한다(선택 판정·해석은 셸 몫 — SelectedEntry 질의).
        TileGrid.SelectionChanged += (_, _) => SelectionChanged?.Invoke();
        // A94 6차: 빈 영역(타일이 아닌 곳) 우클릭 메뉴 — New folder / Paste / Refresh.
        // 타일 메뉴와의 이중 발화는 ContextFlyout 규칙이 원천 차단한다: 컨텍스트 요청은 원본
        // 요소에서 위로 버블링하며 **가장 안쪽의 ContextFlyout 하나만** 뜨므로, 타일 위 우클릭은
        // 타일 컨테이너(AttachContextMenu)가 받고 여기까지 오지 않는다. 배경이 있는 LayoutRoot에
        // 거는 이유 = 그리드 자체 배경이 없어도 요청이 반드시 여기까지 올라오기 때문(히트 보장).
        LayoutRoot.ContextFlyout = MakeSurfaceMenu();
    }

    /// <summary>
    /// 잘라내기(Ctrl+X) 표시 반영 (A94 4차): 이미 그려 둔 타일의 콘텐츠 투명도를 경로 매칭으로
    /// 다시 맞춘다 — 재스캔이 아니라 제자리 갱신이라 선택·스크롤이 보존된다. 새로 그려지는 타일은
    /// MakeTile이 같은 규칙(ExplorerFileOps.ApplyCutMark)으로 처음부터 반영한다.
    /// </summary>
    private void ApplyCutMarks()
    {
        foreach (var item in TileGrid.Items) ExplorerFileOps.ApplyCutMark(item);
    }

    // A176: 구 UseTranslucentBackground(S4 중앙 반투명 — A33 아크릴/A129 스왑체인 폴백)는
    // 반투명 축과 함께 철거 — S4 인스턴스도 S1과 같은 불투명 기본 배경(XAML LayoutRoot)을 쓴다.

    /// <summary>썸네일 그리드로 포커스 이동 (A90: S4 진입 시) — 실패해도 무해(포커스만 안 옮겨진다).</summary>
    public void FocusGrid() => TileGrid.Focus(FocusState.Programmatic);

    /// <summary>
    /// Enter = 선택 항목 열기 (A90 — 위 생성자 주석 참고. 선택이 없으면 셸 분배로 흘린다) +
    /// 클립보드 키 (A94): Ctrl+C/X/V/A + 2차(v0.125.0): F2 = 이름변경(첫 선택 타일만),
    /// Del = 휴지통 삭제, Ctrl+Shift+N = 새 폴더 — 이 그리드에 포커스가 있을 때만 온다
    /// (KeyDown 버블링이라 문서 에디터 등 텍스트 표면으로 새지 않고, A34 통과 규칙과도 겹치지 않는다).
    /// 4차(v0.151.0): Shift+Del = 영구 삭제(확인 대화상자 뒤), Esc = 잘라내기 표시 해제(비소비).
    /// 6차(v0.153.0): Enter가 **다중 선택이면 선택된 파일 전부**를 연다(폴더 제외 — 아래 주석).
    /// </summary>
    private async void OnGridKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.KeyStatus.WasKeyDown) return;
        // A94 2차: 이름변경 편집 상자(TextBox) 안의 키는 전부 편집 몫 — handledEventsToo 구독이라
        // 편집 상자가 Handled를 걸어도 여기까지 오므로 원본 요소로 걸러낸다
        // (Enter가 '항목 열기'로, Del·Ctrl+A/V가 파일 조작으로 새면 안 된다).
        if (e.OriginalSource is TextBox) return;
        if (e.Key == VirtualKey.Enter)
        {
            if (SelectedEntry is not { } entry) return; // 선택 없음 — 비소비(A151: 탐색기 표면 포커스의 Enter는 셸도 양보라 무동작)
            e.Handled = true; // 셸 루트 핸들러의 이중 처리 방지 — OnShellEnter는 Handled면 물러난다
            _lastClick = null; // 같은 Enter가 만든 ItemClick 기록이 더블클릭 판정에 섞이지 않게
            // A94 6차: 다중 선택이면 선택된 '파일' 전부를 연다(폴더는 일괄 열기에서 제외).
            // 선택에 파일이 하나도 없으면(폴더만 다중) 아래 현행 첫 항목 동작으로 떨어진다.
            if (TileGrid.SelectedItems.Count > 1 && OpenFiles(SelectedFilePaths())) return;
            if (entry.IsFolder) FolderActivated?.Invoke(entry.Path);
            else FileActivated?.Invoke(entry.Path);
            return;
        }

        // A94 2차: F2 = 이름변경(첫 선택 타일 1개 — 다중 선택이어도 첫 항목만), Del = 휴지통 삭제.
        // A158: 셸 패널 키가 F1/F2에서 F11/F12로 옮겨가 F2 충돌은 소멸했다 — 이름변경은 F2 그대로
        // 유지하고(사용자 확정), "선택이 있을 때만 Handled"라는 기존 소비 규칙도 그대로 둔다.
        if (e.Key == VirtualKey.F2)
        {
            if (TileGrid.SelectedItem is not GridViewItem selected) return;
            e.Handled = true;
            BeginRenameOf(selected);
            return;
        }
        if (e.Key == VirtualKey.Delete)
        {
            // Del = 휴지통 / Shift+Del = 영구 삭제(A94 4차). Ctrl+Del은 우리 조합이 아니라 비켜 준다.
            if (ExplorerFileOps.IsCtrlDown()) return;
            var targets = SelectedPaths();
            if (targets.Count == 0) return;
            e.Handled = true;
            if (ExplorerFileOps.IsShiftDown()) await PermanentDeleteWithConfirmAsync(targets);
            else await DeleteWithNoticeAsync(targets);
            return;
        }
        if (e.Key == VirtualKey.Escape)
        {
            // A94 4차 — 잘라내기 표시 해제(탐색기 동등). A202 개정: **실제로 지운 표시가 있을
            // 때만 소비**한다(ExplorerPane.OnSurfaceKeyDown의 Esc와 같은 규칙 — 무조건 흘리면
            // 셸 Esc의 새 콘텐츠 닫기 층과 겹쳐 한 번에 두 층이 움직인다). 지울 게 없으면
            // 종전대로 흘려 셸 체인(전체화면 → S4 복귀 → 콘텐츠 닫기)이 받는다.
            // 클립보드 자체는 건드리지 않는다.
            if (ExplorerFileOps.ClearCutMarks()) e.Handled = true;
            return;
        }

        if (!ExplorerFileOps.IsCtrlDown()) return;
        switch (e.Key)
        {
            case VirtualKey.N: // Ctrl+Shift+N = 새 폴더 (Shift 없는 Ctrl+N 아님 —
                // 앱 전역 Shift+N 새 창(A84)과도 다른 조합. 판정 = Ctrl(위) && Shift && N)
                if (!ExplorerFileOps.IsShiftDown() || CurrentFolder is not { Length: > 0 }) return;
                e.Handled = true;
                await CreateFolderThenRenameAsync();
                break;
            case VirtualKey.A:
                e.Handled = true;
                TileGrid.SelectAll(); // Extended 모드 전제
                break;
            case VirtualKey.C:
            case VirtualKey.X:
                var paths = SelectedPaths();
                if (paths.Count == 0) return;
                e.Handled = true;
                await CopyWithNoticeAsync(paths, cut: e.Key == VirtualKey.X);
                break;
            case VirtualKey.V:
                if (CurrentFolder is not { Length: > 0 } folder) return;
                e.Handled = true;
                await PasteIntoAsync(folder);
                break;
        }
    }

    /// <summary>
    /// Ctrl+Shift+N·빈 영역 메뉴 New folder (A94 2차 본문을 6차에서 메서드로 분리 — 동작 무변경):
    /// "New folder" 생성(충돌 = "New folder (2)") 후 재스캔을 예약하고, 그 결과가 돌아오면
    /// (ShowEntries) 그 타일로 이름변경 편집에 진입한다. 이 뷰의 재스캔은 좌 리스트 경유 비동기라
    /// 완료를 직접 기다릴 수 없어 <see cref="_pendingRenamePath"/> 예약 방식이다.
    /// </summary>
    private async Task CreateFolderThenRenameAsync()
    {
        if (CurrentFolder is not { Length: > 0 } parent) return;
        var (created, notice, denied) = ExplorerFileOps.CreateFolder(parent);
        if (notice is not null) await ExplorerFileOps.ReportAsync(notice, denied ? 1 : 0, MakeOpUi());
        if (created is null) return;
        _pendingRenamePath = created; // 재스캔 결과(ShowEntries)가 돌아오면 그 타일로 편집 진입
        FolderActivated?.Invoke(parent); // 단일 원본(좌 리스트) 경유 재스캔 — A93 경로
    }

    /// <summary>
    /// 빈 영역 메뉴 New file (A189 — 위 CreateFolderThenRenameAsync의 파일 판본, 흐름 동일):
    /// "New file.txt" 생성(충돌 = "New file (2).txt") 후 재스캔을 예약하고, 그 결과가 돌아오면
    /// 그 타일로 이름변경 편집에 진입한다. 감시(A94 5차) 재스캔·편집 중 보류(EditEnded)는
    /// New folder와 같은 경로를 그대로 타므로 별도 처리가 없다. 현재 목록이 모듈 확장자로
    /// 필터돼 .txt가 안 보이는 모듈에서는 파일만 만들어지고 편집 진입은 조용히 생략된다
    /// (_pendingRenamePath 미매칭 — New folder의 "그새 사라짐" 폴백과 같은 무해 경로).
    /// </summary>
    private async Task CreateFileThenRenameAsync()
    {
        if (CurrentFolder is not { Length: > 0 } parent) return;
        var (created, notice, denied) = ExplorerFileOps.CreateFile(parent);
        if (notice is not null) await ExplorerFileOps.ReportAsync(notice, denied ? 1 : 0, MakeOpUi());
        if (created is null) return;
        _pendingRenamePath = created; // 재스캔 결과(ShowEntries)가 돌아오면 그 타일로 편집 진입
        FolderActivated?.Invoke(parent); // 단일 원본(좌 리스트) 경유 재스캔 — A93 경로
    }

    /// <summary>
    /// 클립보드 적재 공용 (A94 6차 — Ctrl+C/X와 우클릭 메뉴 Cut/Copy가 같은 경로).
    /// 잘라내기 반투명 표시(4차)는 ExplorerFileOps가 적재 성공 시에만 갱신한다.
    /// </summary>
    private async Task CopyWithNoticeAsync(IReadOnlyList<string> paths, bool cut)
    {
        if (paths.Count == 0) return;
        if (await ExplorerFileOps.CopyToClipboardAsync(paths, cut) is { } notice) ShowNotice(notice);
    }

    /// <summary>
    /// 붙여넣기 공용 (A94 6차 — Ctrl+V·빈 영역 메뉴는 현재 폴더, 폴더 타일 메뉴는 그 폴더).
    /// 갱신은 종전대로 단일 원본(좌 리스트) 경유 재스캔 1회 — A93 경로.
    /// </summary>
    private async Task PasteIntoAsync(string targetFolder)
    {
        if (targetFolder.Length == 0) return;
        var ui = MakeOpUi(); // A94 3차 — 충돌 대화상자·진행 문구, 4차 — 접근 거부 안내
        var (didWork, result, notice) = await ExplorerFileOps.PasteFromClipboardAsync(targetFolder, ui);
        if (didWork) RefreshViaShell();
        await ExplorerFileOps.ReportAsync(notice, result.Denied, ui);
    }

    /// <summary>선택 타일 경로 전부(폴더 포함) — 항목 = 컨테이너 직접 추가라 Tag에서 꺼낸다(A94).</summary>
    private IReadOnlyList<string> SelectedPaths() =>
        TileGrid.SelectedItems
            .OfType<FrameworkElement>()
            .Select(i => i.Tag)
            .OfType<ExplorerListing.Entry>()
            .Select(e => e.Path)
            .ToList();

    /// <summary>선택 타일 중 **파일**만의 경로 (A94 6차 — 일괄 열기 대상. 폴더는 제외한다).</summary>
    private IReadOnlyList<string> SelectedFilePaths() =>
        TileGrid.SelectedItems
            .OfType<FrameworkElement>()
            .Select(i => i.Tag)
            .OfType<ExplorerListing.Entry>()
            .Where(e => !e.IsFolder)
            .Select(e => e.Path)
            .ToList();

    /// <summary>
    /// 잡은 타일의 조작 대상 (A94: 드래그·삭제 규칙 — 그 타일이 선택에 포함돼 있으면 선택 전부,
    /// 아니면 그 타일 하나). 6차에서 Cut·Copy도 같은 규칙을 쓰게 메서드로 뽑았다(동작 무변경).
    /// </summary>
    private IReadOnlyList<string> PathsFor(ExplorerListing.Entry entry)
    {
        var selected = SelectedPaths();
        return selected.Contains(entry.Path, StringComparer.OrdinalIgnoreCase) ? selected : [entry.Path];
    }

    /// <summary>
    /// 선택 파일 일괄 열기 (A94 6차) — 종전 "SelectedFilePath 하나를 OpenFileRouted"를 대체한다.
    /// 그리드 자체 Enter·더블클릭과 같은 규칙(아래 OpenFiles).
    /// ※ A151: 셸 Enter가 모드 순환이 되면서 셸 호출부는 사라졌다 — 그리드 자체 Enter 처리와
    /// 대칭인 공개 실행 API로 남긴다(외부 소비자 0인 상태 유지 무해).
    /// </summary>
    public bool OpenSelectedFiles() => OpenFiles(SelectedFilePaths());

    /// <summary>
    /// 일괄 열기 실행 (A94 6차): 상한(10) 적용 뒤 **첫 파일 = 기존 단일 열기 경로**
    /// (newWindowFirst면 Shift+더블클릭과 같은 새 인스턴스, 아니면 셸이 재사용 규칙 A24 적용),
    /// **나머지 = 전부 새 인스턴스**. 창 생성은 기존 이벤트(FileActivated·FileActivatedNewWindow)로만
    /// 나가므로 이 컨트롤은 창 규칙을 알지 않는다. 루프를 동기로 도는 근거 = 창 생성·파일 열기가
    /// 단일 UI 스레드에서 동기 완결이라는 A124 복원 루프의 전례(WindowManager.TryRestoreSession).
    /// 반환 = 하나라도 열었는지.
    /// </summary>
    private bool OpenFiles(IReadOnlyList<string> files, bool newWindowFirst = false)
    {
        if (files.Count == 0) return false;
        var batch = ExplorerFileOps.TakeBatchOpen(files, ShowNotice);
        if (newWindowFirst) FileActivatedNewWindow?.Invoke(batch[0]);
        else FileActivated?.Invoke(batch[0]);
        for (var i = 1; i < batch.Count; i++) FileActivatedNewWindow?.Invoke(batch[i]);
        return true;
    }

    /// <summary>열 수 지정(A213: 8 − 2×열린 도크 수 = 둘 다 4 / 하나 6 / 없음 8). 바뀌면 타일 크기 재계산.</summary>
    public void SetColumns(int columns)
    {
        if (columns == _columns) return;
        _columns = columns;
        ApplyTileSize();
    }

    /// <summary>
    /// 표시 목록 교체 — 좌 리스트(ExplorerPane)가 정렬·필터를 적용해 넘긴 결과를 그대로 그린다.
    /// folder = 그 목록의 폴더 경로(A94 — 드랍·붙여넣기 대상으로 기억한다).
    /// 이미지 미리보기는 BitmapImage가 스스로 비동기 디코드하므로 별도 로드 루프가 없다.
    /// A192: 종전 전량 동기 생성을 분할 조립으로 대체 — 첫 조각(TileChunkItems)만 즉시 만들고
    /// 나머지는 CompositionTarget.Rendering 틱당 한 조각씩 append한다(StartTileAppendLoop —
    /// DocumentView.StartRenderAppendLoop의 A193 구조 복제). 실체화 상한(MaterializeLimit)을
    /// 넘는 초과분은 만들지 않고 완료 시점(FinishShowEntries)에 안내 1타일만 붙는다.
    /// 재진입(감시 재스캔·정렬·폴더 전환 — 전부 이 메서드로 다시 온다)은 명시 해제 +
    /// 틱 진입 seq 대조의 이중 방어. UpdateLayout은 전량 조립 뒤 1회에서 <b>첫 조각 직후 1회</b>로
    /// 축소 — 목적(ApplyTileSize가 캐스트하는 ItemsPanelRoot의 실체화)은 항목 수와 무관하게
    /// 첫 레이아웃 한 번이면 성립하고, 이후 조각은 패널 속성(ItemWidth/ItemHeight)이 셀 크기를
    /// 자동 적용한다(폴백 경로 보정은 FinishShowEntries 주석).
    /// </summary>
    public void ShowEntries(string folder, IReadOnlyList<ExplorerListing.Entry> entries)
    {
        var seq = ++_showSeq;
        StopTileAppendLoop(); // 방어: 직전 조립 루프가 남아 있으면 먼저 해제(A193 관용구)
        CurrentFolder = folder;
        TileGrid.Items.Clear();

        var cap = Math.Min(entries.Count, MaterializeLimit);
        var first = Math.Min(TileChunkItems, cap);
        for (var i = 0; i < first; i++)
            TileGrid.Items.Add(MakeTile(entries[i]));
        EmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        TileGrid.UpdateLayout(); // 첫 조각(상한 60타일)의 패널 실체화 — 아래 타일 크기 반영이 헛돌지 않게
        ApplyTileSize();

        if (first < cap) StartTileAppendLoop(seq, entries, first, cap);
        else FinishShowEntries(entries, seq); // 소형 폴더 — 조립이 여기서 동기 완료(종전 동작 동일)
    }

    /// <summary>
    /// A192: 첫 조각 이후의 나머지 타일을 CompositionTarget.Rendering 틱마다 한 조각
    /// (TileChunkItems)씩 append한다 — UI 스레드 점유 상한 = 조각 1개 생성
    /// (DocumentView.StartRenderAppendLoop과 같은 프레임 틱 관용구·같은 해제 의무).
    /// 중단 판정 = 매 틱 append 직전의 seq 대조(한 틱 = 한 조각이라 틱 진입 시 1회로 충분):
    /// ShowEntries 재진입(감시 디바운스 재스캔 포함)이 _showSeq를 올린다 — 구 루프가 새 목록에
    /// 낡은 타일을 붙이는 사고를 막는다. 틱 핸들러는 본문 전체가 try/catch다(static 이벤트라
    /// 예외가 새면 앱 전역 크래시) — 조각 생성 예외 = 루프 중단(부분 타일 잔존은 다음
    /// ShowEntries가 덮는다).
    /// </summary>
    private void StartTileAppendLoop(int seq, IReadOnlyList<ExplorerListing.Entry> entries, int start, int cap)
    {
        StopTileAppendLoop(); // 방어: 기동 직전 잔존 루프 해제(A193 관용구)

        var next = start;
        void OnTick(object? sender, object? e)
        {
            try
            {
                if (seq != _showSeq)
                {
                    StopTileAppendLoop(); // 그새 다른 목록이 왔다 — 낡은 타일을 붙이지 않는다
                    return;
                }
                var count = Math.Min(TileChunkItems, cap - next);
                for (var i = next; i < next + count; i++)
                    TileGrid.Items.Add(MakeTile(entries[i]));
                next += count;
                if (next >= cap)
                {
                    StopTileAppendLoop(); // 완료 — 더 깨울 이유가 없다
                    FinishShowEntries(entries, seq);
                }
            }
            catch (Exception)
            {
                StopTileAppendLoop();
            }
        }
        _tileAppendHandler = OnTick;
        CompositionTarget.Rendering += OnTick;
    }

    /// <summary>A192: 분할 조립 루프 해제의 단일 지점 — 구독 해제 + 표지 소거(루프 없으면 무동작).
    /// 기동은 StartTileAppendLoop 한 곳뿐이라 구독 중 핸들러 = 이 필드 하나가 불변식이다.</summary>
    private void StopTileAppendLoop()
    {
        if (_tileAppendHandler is { } handler)
        {
            CompositionTarget.Rendering -= handler;
            _tileAppendHandler = null;
        }
    }

    /// <summary>
    /// A192: 조립 완료의 단일 마무리 — ① 상한 초과분 안내 1타일 부착, ② 폴백 크기 재적용,
    /// ③ 보류 중 이름변경 편집 진입. ③을 완료 뒤로 옮긴 이유: 새 폴더 타일이 뒤 조각에 있으면
    /// FindTileByPath가 조립 중에는 못 찾는다 — 편집 진입 예약(_pendingRenamePath)의 소비를
    /// "처음으로 완주한 조립"으로 미룬다(조립이 도중 무산되면 예약이 남아 다음 완주가 소비 —
    /// 종전 '다음 ShowEntries가 소비'와 같은 1회성). ②는 ApplyTileSize의 폴백 경로(패널이
    /// ItemsWrapGrid가 아닐 때 타일 직접 지정) 전용 보정 — 그 경로는 첫 조각만 크기를 받았으므로
    /// 완료 시 한 번 더 전체 적용한다(정상 경로에서는 패널 속성 재대입 한 줄이라 무해).
    /// 낡은 완료(폐기된 루프의 마지막 틱)는 seq 대조로 걸러진다.
    /// </summary>
    private void FinishShowEntries(IReadOnlyList<ExplorerListing.Entry> entries, int seq)
    {
        if (seq != _showSeq) return; // 방어 — 낡은 완료가 편집 진입을 훔치지 않게
        if (entries.Count > MaterializeLimit)
            TileGrid.Items.Add(MakeOverflowNotice(entries.Count - MaterializeLimit));
        ApplyTileSize();

        // A94 2차: 새 폴더(Ctrl+Shift+N) 직후의 재스캔이면 그 타일을 선택하고 곧바로 이름변경
        // 편집 진입(탐색기 관례). 반드시 '재스캔 결과가 그려진 뒤' — 편집 중 재스캔은 편집 UI를 지운다.
        if (_pendingRenamePath is { } pending)
        {
            _pendingRenamePath = null; // 1회성 — 다음 갱신(다른 폴더 이동 등)에 재발화하지 않게
            if (FindTileByPath(pending) is { } tile)
            {
                TileGrid.SelectedItem = tile;
                TileGrid.ScrollIntoView(tile);
                TileGrid.UpdateLayout(); // 컨테이너 실체화 — 편집 상자 삽입·포커스가 성립하게
                BeginRenameOf(tile);
            }
        }
    }

    /// <summary>
    /// A192: 실체화 상한 초과 안내 — 비상호작용 1타일. Tag 없음(타일 조회·조작 루틴은 전부
    /// Tag의 Entry 패턴 매칭이라 자연 제외된다: FindTileByPath·SelectedPaths·EntryFromSource·
    /// ApplyCutMark·OnItemClick 전수 확인), 계약 훅(메뉴·드래그·더블클릭) 미부착,
    /// IsEnabled=false로 포커스·클릭 대상에서도 뺀다. 문구는 좌 리스트(ExplorerPane)와 동일 사양.
    /// </summary>
    private static GridViewItem MakeOverflowNotice(int hidden) => new()
    {
        Content = new TextBlock
        {
            Text = $"{hidden} more items are not shown. Refine the filter to see them.",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4),
        },
        IsEnabled = false,
    };

    /// <summary>경로로 타일 컨테이너 찾기 — 항목 = 컨테이너 직접 추가(Tag = Entry) 구조 전제.</summary>
    private GridViewItem? FindTileByPath(string path) =>
        TileGrid.Items.OfType<GridViewItem>().FirstOrDefault(i =>
            i.Tag is ExplorerListing.Entry entry &&
            string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// F2·우클릭 Rename 진입 (A94 2차): 타일 캡션 TextBlock을 인라인 편집(ExplorerRenameBox)으로
    /// 바꾼다. 캡션 위치 = MakeTile의 tile.Children[1](아래 행 캡션 — 인덱스 수동 동기).
    /// 커밋 성공 갱신 = RefreshViaShell(편집이 끝난 뒤에만 — 편집 중 재스캔 금지).
    /// </summary>
    private void BeginRenameOf(GridViewItem item)
    {
        if (item.Tag is not ExplorerListing.Entry entry) return;
        if (item.Content is not Grid { Children.Count: > 1 } tile ||
            tile.Children[1] is not TextBlock caption) return;
        ExplorerRenameBox.Begin(tile, caption, entry.Path, MakeOpUi(), RefreshViaShell);
    }

    /// <summary>조작 후 갱신 — 폴더 상태의 단일 원본(좌 리스트)을 셸이 다시 항해시키는 A93 경로.</summary>
    private void RefreshViaShell()
    {
        if (CurrentFolder is { Length: > 0 } folder) FolderActivated?.Invoke(folder);
    }

    /// <summary>
    /// 파일 조작용 UI 문맥 (A94 3차) — 이 그리드 창의 DispatcherQueue·XamlRoot(충돌 대화상자용)와
    /// ShowNotice 채널(진행 문구 라이브 갱신용)을 조작 시작 시점에 캡처한다. 4차부터는 영구 삭제
    /// 확인·접근 거부 안내(관리자 재시작 제안)와 이름변경·새 폴더 실패 보고까지 같은 문맥을 쓴다.
    /// </summary>
    private ExplorerFileOps.OpUi MakeOpUi() => new(DispatcherQueue, XamlRoot, ShowNotice);

    /// <summary>
    /// Del·우클릭 Delete (A94 2차): 휴지통 경유 삭제(StorageDeleteOption.Default —
    /// ExplorerFileOps 주석). 확인 대화상자 없음(탐색기 관례) — 실패만 안내 문구,
    /// 권한 부족은 관리자 재시작 제안(A94 4차 — ReportAsync).
    /// </summary>
    private async Task DeleteWithNoticeAsync(IReadOnlyList<string> paths)
    {
        var ui = MakeOpUi();
        var result = await ExplorerFileOps.DeleteToRecycleAsync(paths);
        RefreshViaShell();
        await ExplorerFileOps.ReportAsync(result.Notice("deleted"), result.Denied, ui);
    }

    /// <summary>
    /// Shift+Del = 영구 삭제 (A94 4차): 탐색기 동등으로 **영구 삭제만** 확인창을 띄우고(기본 버튼 =
    /// Cancel), 확인하면 휴지통을 거치지 않고 지운다. 대상 선택 규칙·재스캔·실패 안내는 Del과
    /// 같은 경로다(좌 리스트 단일 원본 경유 재스캔). 취소하면 아무것도 하지 않는다.
    /// </summary>
    private async Task PermanentDeleteWithConfirmAsync(IReadOnlyList<string> paths)
    {
        var ui = MakeOpUi();
        if (!await ExplorerDialogs.ConfirmPermanentDeleteAsync(ui.Dispatcher, ui.Root, paths)) return;
        var result = await ExplorerFileOps.DeletePermanentlyAsync(paths);
        RefreshViaShell();
        await ExplorerFileOps.ReportAsync(result.Notice("deleted"), result.Denied, ui);
    }

    /// <summary>
    /// 타일 한 변 = floor(그리드 실폭 / 열 수) (A93 확정 수식). GridView의 기본 아이템 패널
    /// (ItemsWrapGrid)의 셀 크기(ItemWidth/ItemHeight)로 지정한다 — 셀이 균일하면 줄바꿈이
    /// 정확히 열 수대로 떨어진다. 패널이 아직 없거나 다른 타입이면(테마·템플릿 변형 대비)
    /// 타일 루트에 직접 크기를 주는 폴백으로 같은 결과를 낸다.
    /// </summary>
    private void ApplyTileSize()
    {
        var width = TileGrid.ActualWidth;
        if (width <= 0) return;
        var size = Math.Floor(width / _columns);
        if (size < 24) return; // 극단적으로 좁은 창 보호 — 이전 크기 유지가 낫다

        if (TileGrid.ItemsPanelRoot is ItemsWrapGrid wrap)
        {
            wrap.ItemWidth = size;
            wrap.ItemHeight = size;
            return;
        }
        foreach (var obj in TileGrid.Items)
            if (obj is GridViewItem { Content: FrameworkElement tile })
            {
                tile.Width = size;
                tile.Height = size;
            }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyTileSize();

    // ---------- 타일 구성 ----------

    /// <summary>균일 타일: 위(미리보기/글리프/확장자) + 아래 파일명 1줄 말줄임 캡션(A93).</summary>
    private GridViewItem MakeTile(ExplorerListing.Entry entry)
    {
        var preview = entry.IsFolder ? MakeFolderGlyph()
            : IsImageFile(entry.Name) ? MakeImagePreview(entry)
            : MakeExtensionTile(entry);

        var tile = new Grid();
        tile.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        tile.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        tile.Children.Add(preview);

        var caption = new TextBlock
        {
            Text = entry.Name,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis, // 1줄 말줄임(A93) — 2줄 아님
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(4, 0, 4, 4),
        };
        Grid.SetRow(caption, 1);
        tile.Children.Add(caption);
        ToolTipService.SetToolTip(tile, entry.Name);

        var item = new GridViewItem
        {
            Content = tile,
            Tag = entry,
            // 셀(ItemWidth/ItemHeight)을 타일이 꽉 채워야 미리보기 영역이 균일해진다
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        ExplorerFileOps.ApplyCutMark(item); // A94 4차 — 잘라내기 중인 경로면 처음부터 반투명
        AttachContextMenu(item, entry); // A24 — 좌 리스트와 같은 우클릭 메뉴
        AttachDragDrop(item, entry); // A94 — 드래그 아웃 + 폴더 타일 드랍
        item.IsDoubleTapEnabled = true; // A85 — 압축 모듈 내부 리스트(ArchiveView)와 같은 명시
        item.DoubleTapped += OnItemDoubleTapped; // A85 — 더블클릭 열기의 기본 경로
        return item;
    }

    /// <summary>
    /// 타일에 드래그 아웃(전 항목)과 드랍 대상(폴더 타일만)을 건다 (A94 —
    /// ExplorerPane.AttachDragDrop과 같은 구성: 데퍼럴이 있는 컨테이너 CanDrag 경로).
    /// 잡은 타일이 선택에 포함돼 있으면 선택 전부를, 아니면 그 타일 하나만 싣는다(윈도우 관례).
    /// 폴더 타일 핸들러가 Handled를 걸므로 루트(LayoutRoot) 핸들러와 이중 처리되지 않는다.
    /// </summary>
    private void AttachDragDrop(GridViewItem item, ExplorerListing.Entry entry)
    {
        item.CanDrag = true;
        item.DragStarting += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var selected = SelectedPaths();
                IReadOnlyList<string> paths = selected.Contains(entry.Path, StringComparer.OrdinalIgnoreCase)
                    ? selected
                    : [entry.Path];
                if (!await ExplorerFileOps.FillDragDataAsync(args.Data, paths))
                    args.Cancel = true; // 실을 항목이 없다(그새 삭제 등)
            }
            finally
            {
                deferral.Complete();
            }
        };

        if (!entry.IsFolder) return;
        item.AllowDrop = true;
        item.DragOver += (_, e) => ExplorerFileOps.HandleTargetDragOver(e, entry.Path);
        item.Drop += (_, e) => HandleDrop(e, entry.Path);
    }

    /// <summary>폴더 타일: Segoe Fluent 폴더 글리프 — ExplorerPane 그리드/리스트와 같은 E8B7.</summary>
    private static FontIcon MakeFolderGlyph() => new()
    {
        Glyph = "\uE8B7",
        FontSize = 40,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>이미지 모듈 담당 확장자인지 — 담당 목록(ImageFolderNavigator)을 그대로 재사용(A93).</summary>
    private static bool IsImageFile(string name) =>
        ExplorerListing.MatchesExtension(name, KOTU.Module.Image.ImageFolderNavigator.SupportedExtensions);

    /// <summary>
    /// 이미지 실제 축소 미리보기: BitmapImage + DecodePixelWidth(A93 지정) — 디코드는 XAML
    /// 파이프라인이 비동기로 한다. WIC 밖 포맷(psd)·손상 파일은 ImageFailed로 확장자 타일 폴백.
    /// </summary>
    private UIElement MakeImagePreview(ExplorerListing.Entry entry)
    {
        // A175: 클라우드 전용(placeholder) 파일은 원본 디코드가 하이드레이션(전체 다운로드)을
        // 일으킨다 — 원본은 절대 열지 않고, 캐시·클라우드 제공 썸네일만 비동기로 시도한다.
        if (entry.IsPlaceholder) return MakePlaceholderPreview(entry);

        var host = new Grid();
        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = PreviewDecodeWidth };
            bitmap.UriSource = new Uri(entry.Path);
            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(4),
            };
            image.ImageFailed += (_, _) =>
            {
                host.Children.Clear();
                host.Children.Add(MakeExtensionTile(entry));
            };
            host.Children.Add(image);
        }
        catch
        {
            host.Children.Add(MakeExtensionTile(entry)); // 경로가 Uri가 못 되는 극단 케이스
        }
        return host;
    }

    /// <summary>
    /// 클라우드 전용(placeholder) 이미지 타일 (A175): 즉시 확장자 타일을 그려 두고, 캐시된
    /// 썸네일(ReturnOnlyIfCached — 원본을 열지 않는다)이 있으면 비동기로 바꿔 끼운다.
    /// 없으면 확장자 타일 그대로 — 어떤 경우에도 하이드레이션은 일어나지 않는다.
    /// </summary>
    private UIElement MakePlaceholderPreview(ExplorerListing.Entry entry)
    {
        var host = new Grid();
        host.Children.Add(MakeExtensionTile(entry));
        _ = FillCachedThumbnailAsync(host, entry.Path);
        return host;
    }

    /// <summary>
    /// 캐시·클라우드 제공 썸네일을 UI 스레드 비동기로 받아 host에 채운다 (A175).
    /// ReturnOnlyIfCached라 원본 파일은 열리지 않는다(캐시에 없으면 실패 → 확장자 타일 유지).
    /// 폴더 이동으로 host가 트리에서 떨어져도(ShowEntries가 타일을 전부 새로 만든다)
    /// 고아 Grid 갱신일 뿐이라 무해하다 — 재진입 가드가 필요 없는 이유.
    /// </summary>
    private static async Task FillCachedThumbnailAsync(Grid host, string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumb = await file.GetThumbnailAsync(
                ThumbnailMode.SingleItem, PreviewDecodeWidth, ThumbnailOptions.ReturnOnlyIfCached);
            if (thumb is null || thumb.Size == 0) return;

            // 스트림 → 바이트 → BitmapImage: ExplorerPane.FetchThumbnail과 같은 변환 관용구
            // (검증된 형태만 복제 — thumb를 SetSourceAsync에 직접 넘기는 선례가 저장소에 없다).
            using var stream = thumb.AsStreamForRead();
            using var buffer = new MemoryStream((int)thumb.Size);
            await stream.CopyToAsync(buffer);
            buffer.Position = 0;
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(buffer.AsRandomAccessStream());

            host.Children.Clear();
            host.Children.Add(new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(4),
            });
        }
        catch
        {
            // 캐시 썸네일 없음·읽기 실패 — 확장자 타일 유지. 원본은 어떤 폴백에서도 열지 않는다.
        }
    }

    /// <summary>
    /// 이미지 외 파일 타일: 담당 모듈 액센트 색 배경 + 확장자 대문자 (A93).
    /// 담당 모듈이 없으면(액센트 null) 중립 레이어 색 — Branding.ModuleAccent의 폴백 규칙 그대로.
    /// </summary>
    private UIElement MakeExtensionTile(ExplorerListing.Entry entry)
    {
        var ext = Path.GetExtension(entry.Name).TrimStart('.').ToUpperInvariant();
        var accent = Branding.ModuleAccent(ModuleIdForFile?.Invoke(entry.Path));

        var label = new TextBlock
        {
            Text = ext.Length > 0 ? ext : "?",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (accent is not null) // 액센트 배경 위에서만 흰 글자 — 중립 배경은 테마 기본색 유지
            label.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);

        return new Border
        {
            Margin = new Thickness(8),
            CornerRadius = new CornerRadius(6),
            Background = accent is { } color
                ? new SolidColorBrush(color)
                : (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            Child = label,
        };
    }

    /// <summary>
    /// 항목 우클릭 메뉴 — ExplorerPane.AttachContextMenu와 같은 구성(A94 2차 신설 → 6차 확장).
    /// 순서는 탐색기 관례 근사: 파일 = "Open in new instance"(A24) → 구분선 → Cut·Copy →
    /// 구분선 → Rename·Delete, 폴더 = Cut·Copy·**Paste(대상 = 그 폴더)** → 구분선 → Rename·Delete.
    /// Delete·Cut·Copy 대상은 드래그와 같은 규칙 — 그 타일이 선택에 포함돼 있으면 선택 전부,
    /// 아니면 그 타일 하나(PathsFor).
    /// Rename은 플라이아웃이 닫히며 포커스를 되돌린 '뒤'에 진입해야 편집 상자가 곧장 LostFocus
    /// 커밋으로 닫혀 버리지 않는다 — 디스패처로 한 박자 미룬다.
    /// </summary>
    private void AttachContextMenu(GridViewItem item, ExplorerListing.Entry entry)
    {
        var flyout = new MenuFlyout();
        if (!entry.IsFolder)
        {
            var open = new MenuFlyoutItem
            {
                Text = "Open in new instance", // A53 문구
                Icon = new FontIcon { Glyph = "\uE8A7" }, // OpenInNewWindow
            };
            open.Click += (_, _) => FileActivatedNewWindow?.Invoke(entry.Path);
            flyout.Items.Add(open);
            flyout.Items.Add(new MenuFlyoutSeparator());
        }
        AddClipboardItems(flyout, entry); // A94 6차 — Cut·Copy·(폴더면 Paste) + 구분선
        var rename = new MenuFlyoutItem
        {
            Text = "Rename",
            Icon = new FontIcon { Glyph = "\uE8AC" }, // Rename
        };
        rename.Click += (_, _) => DispatcherQueue.TryEnqueue(() => BeginRenameOf(item));
        flyout.Items.Add(rename);
        var delete = new MenuFlyoutItem
        {
            Text = "Delete",
            Icon = new FontIcon { Glyph = "\uE74D" }, // Delete
        };
        delete.Click += async (_, _) => await DeleteWithNoticeAsync(PathsFor(entry));
        flyout.Items.Add(delete);
        item.ContextFlyout = flyout;
    }

    /// <summary>
    /// 타일 메뉴의 클립보드 묶음 (A94 6차): Cut · Copy · (폴더면) Paste + 뒤따르는 구분선.
    /// 조작은 Ctrl+C/X/V와 **완전히 같은 경로**다(CopyWithNoticeAsync·PasteIntoAsync) —
    /// 폴더 Paste만 대상이 현재 폴더가 아니라 그 폴더다(PasteFromClipboardAsync가 이미 대상
    /// 폴더를 인자로 받으므로 넓힐 것이 없었다). Paste 활성 판정은 메뉴가 열릴 때 한다.
    /// </summary>
    private void AddClipboardItems(MenuFlyout flyout, ExplorerListing.Entry entry)
    {
        var cutItem = new MenuFlyoutItem
        {
            Text = "Cut",
            Icon = new FontIcon { Glyph = "\uE8C6" }, // Cut
        };
        cutItem.Click += async (_, _) => await CopyWithNoticeAsync(PathsFor(entry), cut: true);
        flyout.Items.Add(cutItem);

        var copyItem = new MenuFlyoutItem
        {
            Text = "Copy",
            Icon = new FontIcon { Glyph = "\uE8C8" }, // Copy
        };
        copyItem.Click += async (_, _) => await CopyWithNoticeAsync(PathsFor(entry), cut: false);
        flyout.Items.Add(copyItem);

        if (entry.IsFolder)
        {
            var pasteItem = new MenuFlyoutItem
            {
                Text = "Paste",
                Icon = new FontIcon { Glyph = "\uE77F" }, // Paste
            };
            pasteItem.Click += async (_, _) => await PasteIntoAsync(entry.Path);
            flyout.Items.Add(pasteItem);
            flyout.Opening += (_, _) => pasteItem.IsEnabled = ExplorerFileOps.CanPasteFromClipboard();
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
    }

    /// <summary>
    /// 빈 영역 우클릭 메뉴 (A94 6차 → A189에서 New file 추가): New folder / New file / Paste /
    /// Refresh — 전부 기존 경로 재사용이다(Ctrl+Shift+N의 CreateFolderThenRenameAsync와 그 파일
    /// 판본 CreateFileThenRenameAsync = 생성 후 이름 편집 진입까지 · 현재 폴더 붙여넣기 ·
    /// 단일 원본 경유 재스캔 RefreshViaShell). 이 뷰는 표면이 하나라 메뉴도 한 벌이다.
    /// 활성 판정은 메뉴가 열릴 때: 아직 폴더가 정해지지 않았으면 전부 비활성, Paste는 클립보드에
    /// 파일 항목이 있을 때만(판정 실패 시 활성 — CanPasteFromClipboard 주석).
    /// </summary>
    private MenuFlyout MakeSurfaceMenu()
    {
        var newFolder = new MenuFlyoutItem
        {
            Text = "New folder",
            Icon = new FontIcon { Glyph = "\uE8F4" }, // NewFolder
        };
        newFolder.Click += async (_, _) => await CreateFolderThenRenameAsync();

        // A189: New file - New folder 옆, 같은 흐름(생성 후 이름변경 편집 진입)의 파일 판본.
        var newFile = new MenuFlyoutItem
        {
            Text = "New file",
            Icon = new FontIcon { Glyph = "\uE7C3" }, // 문서(파일) — 탐색기 파일 타일과 같은 글리프
        };
        newFile.Click += async (_, _) => await CreateFileThenRenameAsync();

        var paste = new MenuFlyoutItem
        {
            Text = "Paste",
            Icon = new FontIcon { Glyph = "\uE77F" }, // Paste
        };
        paste.Click += async (_, _) => await PasteIntoAsync(CurrentFolder ?? string.Empty);

        var refresh = new MenuFlyoutItem
        {
            Text = "Refresh",
            Icon = new FontIcon { Glyph = "\uE72C" }, // Refresh
        };
        refresh.Click += (_, _) => RefreshViaShell();

        var flyout = new MenuFlyout();
        flyout.Items.Add(newFolder);
        flyout.Items.Add(newFile);
        flyout.Items.Add(paste);
        flyout.Items.Add(refresh);
        flyout.Opening += (_, _) =>
        {
            var ready = CurrentFolder is { Length: > 0 };
            newFolder.IsEnabled = ready;
            newFile.IsEnabled = ready; // A189: 새 폴더와 같은 판정(폴더 확정 전 비활성)
            paste.IsEnabled = ready && ExplorerFileOps.CanPasteFromClipboard();
            refresh.IsEnabled = ready;
        };
        return flyout;
    }

    // ---------- 입력 ----------

    /// <summary>
    /// 원시 눌림(PointerPressed) 쌍 = 더블클릭 최후 폴백 (A131 — 배선 근거는 생성자 주석).
    /// 왼쪽 눌림 "전이"만 태운다(A112 XButton1 판정과 같은 관용구 — 다른 버튼이 눌린 채 겹쳐 온
    /// 눌림은 전이 종류가 달라 걸리지 않는다). Ctrl 눌림은 다중 선택 토글 제스처라 쌍에서
    /// 제외한다(Shift는 제외하지 않는다 — Shift+더블클릭 = 새 창(A24)은 Activate가 해석한다).
    /// 정상 환경에서는 기존 두 판정과 같은 제스처에서 겹쳐 발화하지만 Activate의 _lastActivation
    /// 억제(A85)가 1회로 누른다 — 두 번째 눌림 시점 발화는 탐색기 관례(WM_LBUTTONDBLCLK)와 같다.
    /// </summary>
    private void OnSurfacePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(TileGrid).Properties.PointerUpdateKind
            != Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed) return;
        if (e.OriginalSource is TextBox) return; // 이름변경 편집 상자(A94 2차) — 더블클릭은 텍스트 선택 몫
        if (ExplorerFileOps.IsCtrlDown())
        {
            _lastPress = null; // Ctrl 토글 선택 — 진행 중이던 쌍 판정을 끊는다
            return;
        }
        if (EntryFromSource(e.OriginalSource) is not { } entry)
        {
            _lastPress = null; // 빈 영역·스크롤바 — 항목 밖 눌림은 쌍을 끊는다
            return;
        }
        var now = DateTime.UtcNow;
        var isPair = _lastPress is { } last && last.Path == entry.Path &&
                     (now - last.At).TotalMilliseconds < DoubleClickMs;
        _lastPress = isPair ? null : (entry.Path, now);
        if (isPair) Activate(entry);
    }

    /// <summary>눌림의 원본 요소에서 타일 컨테이너(Tag = Entry)를 찾는다 — 조상 상향 탐색
    /// (깊이 상한 64 = HotkeySupport.MaxAncestorDepth와 같은 방어).</summary>
    private static ExplorerListing.Entry? EntryFromSource(object source)
    {
        var node = source as DependencyObject;
        for (var depth = 0; node is not null && depth < 64; depth++)
        {
            if (node is GridViewItem { Tag: ExplorerListing.Entry entry }) return entry;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    /// <summary>
    /// 클릭 2회(500ms 내 같은 항목) = 더블클릭 — ExplorerPane.OnItemClick과 같은 판정.
    /// ※ A85: 실기기 입력 스택은 더블클릭의 두 번째 클릭을 더블탭 제스처로 소비해 두 번째
    /// ItemClick이 안 올 수 있다 — 그 경우는 OnItemDoubleTapped가 받는다. 이 판정은
    /// ItemClick이 2회 오는 환경(키보드 Enter 연타 포함)의 보조 경로로 유지한다.
    /// </summary>
    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FrameworkElement { Tag: ExplorerListing.Entry entry }) return;

        var now = DateTime.UtcNow;
        var isDouble = _lastClick is { } last && last.Path == entry.Path &&
                       (now - last.At).TotalMilliseconds < DoubleClickMs;
        _lastClick = (entry.Path, now);
        if (!isDouble) return;

        _lastClick = null;
        Activate(entry);
    }

    /// <summary>
    /// 컨테이너 DoubleTapped = 더블클릭 열기 (A85). 실기기에서는 두 번째 클릭이 더블탭 제스처로
    /// 소비되어 두 번째 ItemClick이 오지 않아, 클릭 쌍 판정(OnItemClick)만으로는 열기가 조용히
    /// 무시됐다(압축 모듈 내부 리스트는 처음부터 DoubleTapped라 이 증상이 없었다 — 같은 배선).
    /// </summary>
    private void OnItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox) return; // 이름변경 편집 상자(A94 2차) — 더블클릭은 텍스트 선택 몫
        if (sender is not GridViewItem { Tag: ExplorerListing.Entry entry }) return;
        e.Handled = true;
        _lastClick = null; // 이 제스처를 이룬 클릭 기록이 다음 클릭 쌍 판정에 섞이지 않게
        Activate(entry);
    }

    /// <summary>
    /// 더블클릭 열기 공통 종착점 (A85): 폴더 = 좌 리스트 항해(FolderActivated), 파일 = 열기
    /// (Shift = 새 창, A24). ItemClick 쌍과 DoubleTapped가 같은 제스처에서 둘 다 발화하는
    /// 환경이 있어, 같은 경로의 연속 발화를 판정 창(DoubleClickMs) 안에서 1회로 누른다 —
    /// A24 "항상 새 창" 설정에서 창이 두 개 뜨는 이중 열기 방지.
    /// A94 6차: 활성화한 타일이 **다중 선택에 포함돼 있으면** 선택된 파일 전부를 연다(폴더 제외 —
    /// 선택에 파일이 하나도 없으면 종전대로 그 타일 하나. Enter 규칙과 같다).
    /// </summary>
    private void Activate(ExplorerListing.Entry entry)
    {
        var now = DateTime.UtcNow;
        if (_lastActivation is { } last && last.Path == entry.Path &&
            (now - last.At).TotalMilliseconds < DoubleClickMs)
            return;
        _lastActivation = (entry.Path, now);

        var shift = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        // A94 6차 — 다중 선택 일괄 열기(잡은 타일이 선택 밖이면 그 타일만: 드래그·삭제와 같은 규칙)
        if (TileGrid.SelectedItems.Count > 1 &&
            SelectedPaths().Contains(entry.Path, StringComparer.OrdinalIgnoreCase) &&
            OpenFiles(SelectedFilePaths(), shift))
            return;

        if (entry.IsFolder)
        {
            FolderActivated?.Invoke(entry.Path);
            return;
        }

        if (shift) FileActivatedNewWindow?.Invoke(entry.Path);
        else FileActivated?.Invoke(entry.Path);
    }

    // ---------- 드래그 앤 드랍 (A94 1차, v0.124.0 — A93의 무동작 소비를 실동작으로 전환) ----------

    /// <summary>
    /// 중앙(탐색기) 빈 영역·파일 타일 위 드래그 = 현재 폴더로 이동/복사(같은 볼륨 이동/다른 볼륨
    /// 복사·Ctrl 복사 강제·Shift 이동 강제 — ExplorerFileOps.DecideOperation). 폴더 타일 위는
    /// 타일 자체 핸들러(AttachDragDrop)가 먼저 Handled로 받는다. 목록이 아직 없으면(폴더 미정)
    /// None으로 소비만 — 어느 쪽이든 Handled라 창 전체 "열기" 폴백(OnWindowDrop)에 안 넘어간다.
    /// </summary>
    private void OnDragOver(object sender, DragEventArgs e) =>
        ExplorerFileOps.HandleTargetDragOver(e, CurrentFolder);

    /// <summary>빈 영역·파일 타일 위 드랍 — 대상 = 현재 폴더.</summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (CurrentFolder is { Length: > 0 } folder) HandleDrop(e, folder);
        else e.Handled = true; // 폴더 미정 — A93 때처럼 소비만
    }

    /// <summary>
    /// 드랍 실행(A94): 조작은 워커에서 비동기, 완료 후 FolderActivated로 현재 폴더를 다시 항해 —
    /// 폴더 상태의 단일 원본인 좌 리스트(ExplorerPane)를 셸이 항해시키고 결과가 ViewChanged로
    /// 돌아와 이 그리드까지 갱신된다(A93 경로 그대로 — 5차의 폴더 감시가 같은 변경을 또 봐도
    /// 디바운스가 흡수하므로 명시 재스캔은 유지한다).
    /// </summary>
    private async void HandleDrop(DragEventArgs e, string targetFolder)
    {
        e.Handled = true; // 창 수준 라우팅과의 이중 처리 방지 (await 전에 동기로 지정해야 유효)
        var operation = ExplorerFileOps.DecideOperation(e, targetFolder);
        if (operation == DataPackageOperation.None ||
            !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        e.AcceptedOperation = operation; // 소스(OS 탐색기 등)에 확정 동작을 알린다

        // A94 3차 — 충돌 대화상자·진행 문구용 UI 문맥(조작 시작 시점 캡처). 4차 — 접근 거부 안내도 같은 문맥.
        var ui = MakeOpUi();
        var move = operation == DataPackageOperation.Move;
        var result = await ExplorerFileOps.TransferDroppedAsync(e.DataView, targetFolder, move, ui);
        FolderActivated?.Invoke(CurrentFolder is { Length: > 0 } current ? current : targetFolder);
        await ExplorerFileOps.ReportAsync(result.Notice(move), result.Denied, ui);
    }

    // ---------- 조작 실패 안내 (A94 — A92류 일시 문구) ----------
    // Storyboard 페이드는 CI(컴파일 전용)로 검증할 수 없고 실패 시 상태가 남을 수 있어(A92 선례)
    // A90 강조와 같은 타이머 + Visibility 두 단계로만 구현 — 최악의 실패도 "문구가 안 보인다".

    private static readonly TimeSpan NoticeHoldFor = TimeSpan.FromSeconds(2.5); // A92 표시 시간과 동일

    private DispatcherTimer? _noticeTimer;

    private void ShowNotice(string text)
    {
        NoticeText.Text = text;
        NoticeText.Visibility = Visibility.Visible;
        if (_noticeTimer is null)
        {
            var timer = new DispatcherTimer { Interval = NoticeHoldFor };
            timer.Tick += (_, _) =>
            {
                timer.Stop(); // 반복 타이머 — Tick에서 반드시 멈춘다(A92 관용구)
                NoticeText.Visibility = Visibility.Collapsed;
            };
            _noticeTimer = timer;
        }
        _noticeTimer.Stop(); // 연속 실패 시 표시 시간 되감기
        _noticeTimer.Start();
    }
}
