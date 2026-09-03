using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using KOTU.App.Integration;
using KOTU.Core.Contracts;
using KOTU.Core.Routing;
using KOTU.Core.Settings;
using KOTU.Core.Threading;
using KOTU.Module.Audio;
using KOTU.Module.Document;

namespace KOTU.App;

/// <summary>
/// 설정 페이지. UI 스케일(v0.24.0), 탐색기 통합(파일 연결·우클릭 메뉴)을 관리한다.
/// 탐색기 등록은 현재 사용자(HKCU) 범위 — 관리자 권한 불필요, 해제 시 흔적 없음.
/// 하단 바(후원 문구 — ⛶ 전체화면은 A151에서 셸 모드 버튼으로 이관)는 셸이 TakeBottomBar()로 가져간다(v0.50.0).
/// Updates 섹션은 전역 <see cref="UpdateCoordinator"/>의 상태를 표시만 하고, 확인은
/// <b>이 화면 진입 1회</b> · <b>이 화면에 머무는 동안</b>의 2분 주기 타이머 두 경로가
/// 코디네이터에서 돈다(A114, v0.136.0 — A95의 "수동 전용"을 대체. A125/v0.148.0에서 수동 버튼만
/// 걷어내 자동 두 경로가 남았고, A206/v0.215.0에서 주기 타이머가 "상시"에서 "이 화면 체류 중"으로
/// 좁혀졌다. 토스트·오토체크 토글은 계속 없다). 그 체류를 코디네이터에 알리는 것이
/// Loaded/Unloaded에 걸린 <see cref="HoldUpdateWatch"/>/<see cref="ReleaseUpdateWatch"/>다.
/// 연결 토글의 레지스트리 작업·기본 앱 개수 조회는 전부 <see cref="Worker"/>에서 돌고
/// UI에는 진행률과 결과만 흘러온다(A77, v0.106.0).
/// A195: 남아 있던 UI 스레드 동기 레지스트리 접근 셋(<b>모듈 토글 초기값 읽기</b>·
/// <b>우클릭 메뉴 토글 초기값 읽기</b>·<b>우클릭 메뉴 등록/해제</b>)도 같은 워커로 옮겼다 —
/// 이 화면에서 레지스트리를 만지는 코드는 이제 <b>전부</b> 워커 스레드에 있다
/// (ARCHITECTURE.md §11.1 ① "UI 스레드 동기 레지스트리 IO 금지").
/// A183: Explorer integration 절의 스위치들은 스위치 하나당 묶음 하나로 구획한다 —
/// A220(2026-08-24)에서 구획 수단이 카드(Border)에서 그룹 간 여백으로 바뀌었다(카드 안쪽
/// Padding이 스위치 좌변을 들뜨게 한다는 사용자 보고 — 묶음 구조 자체는 유지).
/// A197: 카드 헤더 행의 순서는 <b>스위치 → 제목 → 진행 링</b>이다 — 스위치가 제목 <b>왼쪽</b>에
/// 선다(A183의 우측 끝 배치를 사용자 지시로 뒤집은 것. 스위치 내장 On/Off 문구는 제목과
/// 붙어 보이지 않게 비워 뒀다).
/// A227: 절 맨 위에 "Register all file associations" 마스터 스위치가 하나 더 선다 —
/// 등록 경로를 새로 만들지 않고 <b>모듈 토글의 IsOn을 대신 눌러</b> 위 A77 흐름을 그대로 태운다.
/// 표시 규칙은 "전부 켜짐일 때만 On"이고, 그 되계산은 <see cref="_suppressMasterToggle"/>이 감싼다.
/// A257(2026-08-28): 이 절의 세로 순서는 설명 → Learn more → <b>우클릭 메뉴 그룹</b> →
/// <b>마스터 그룹</b> → 모듈 그룹 5개 → 공용 상태 줄이었다(A235 ①의 마스터·메뉴 순서를 교환하고,
/// A235 ②의 "Show per-module options" 접기는 폐지). 그룹 사이 간격 12 통일은 지금도 유효하다.
/// A292: 모듈 그룹 5개가 <b>"Advanced options" 펼침</b> 안의 <b>확장자 개별 토글</b>로 흡수·대체됐다
/// (등록 단위가 모듈 → 확장자로 — 정본은 종전과 같이 레지스트리다. 설정 파일 키는 만들지 않았으므로
/// 마이그레이션도 없다. <b>A326(2026-09-03)이 이 입도를 다시 모듈 단위로 되돌렸다</b> — 아래 참조).
/// 펼침은 저장하지 않고 늘 닫힌 채 시작한다. 절 순서도 하나 움직였다:
/// <b>Playback</b> 절(A258/v0.258.0이 Updates 바로 앞에 넣었던 것)이 이 절 바로 아래로 올라와
/// 마스터·Advanced 링크·Playback이 대부분 한 화면에 함께 보인다. 그 이동으로 머리글 없이 이 절에
/// 딸려 읽히던 네 카드(진단 3장 + 설정 파일)가 Playback 밑으로 읽히게 되어 <b>Troubleshooting</b>
/// 머리글을 신설해 그 아래로 묶었다(카드 자체·Updates와의 상대 순서는 무변경).
/// A326(2026-09-03): A292의 <b>입도 개정</b> — "Advanced options" 펼침 안의 토글이 확장자 48개에서
/// <b>모듈 5개</b>로 돌아갔다(사용자 정정: A292의 "모듈별 확장자 등록 개별화" 지시는 <b>모듈 단위</b>가
/// 의도였다. A235 ②의 접기 → A257 철회 → A292 확장자 개별화로 이어진 이력의 종착점이다).
/// 펼침 구조·마스터 스위치·모듈 소제목·"Default app for n/m extensions" 줄·"Set default..."는 그대로다.
/// 모듈 스위치의 semantics는 마스터와 같은 하이브리드다 — <b>그 모듈의 확장자가 전부 등록됐을 때만 On</b>,
/// 누르면 <b>그 모듈의 전 확장자를 워커에서 직렬로 일괄 등록/해제</b>(부분 등록은 Off로 보인다).
/// 레지스트리 층은 무접촉 — A292가 만든 확장자 단위 API를 모듈 스위치가 루프로 부를 뿐이다.
/// </summary>
public sealed partial class SettingsView : UserControl, IBottomBarProvider
{
    private readonly TextBlock _status = new() { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
    private readonly ISettingsService _settings;
    private bool _suppressToggle;

    /// <summary>
    /// A227(v0.235.0): "Register all file associations" 마스터 스위치를 <b>프로그램적으로</b>
    /// 고쳐 그릴 때만 켜는 전용 가드. <see cref="_suppressToggle"/>와 축을 나눈 이유가 이 배치의
    /// 핵심이다 — 마스터의 동작은 "확장자 토글들의 IsOn을 대신 눌러 주는 것"(A292 전에는 모듈
    /// 토글)이라 그쪽 Toggled가 <b>반드시 돌아야</b> 하는데, 공용 <see cref="_suppressToggle"/>을
    /// 재활용하면 마스터 상태를 되계산하는 순간 확장자 토글의 등록 흐름까지 함께 막혀
    /// 아무것도 등록되지 않는다. 이 플래그가 막는 것은 오직 마스터 자신의 Toggled 재발화다.
    /// </summary>
    private bool _suppressMasterToggle;

    /// <summary>
    /// A167(v0.171.0): Updates 섹션의 "다음 확인까지 남은 시간"을 1초마다 다시 그리는 타이머.
    /// <b>뷰 하나당 하나</b>다 — 필드 초기화로 만들어 두고 다시 만드는 곳이 없다.
    /// Tick 배선·Start·Stop은 전부 <see cref="BuildUpdatesSection"/> 한 곳에 모아 두었고,
    /// Loaded에서 시작해 Unloaded에서 멈춘다(화면 밖에서는 돌지 않는다).
    /// </summary>
    private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>
    /// A206(v0.215.0): 이 뷰가 지금 <see cref="UpdateCoordinator"/>의 "설정 열림" 카운트를 하나
    /// 쥐고 있는지. 코디네이터의 카운트는 창 여러 개가 함께 쓰는 전역 값이라, 이 뷰가 올린 몫은
    /// 정확히 1이어야 한다 — 이 bool이 그 멱등성을 지킨다(Loaded가 두 번 와도 +1은 한 번,
    /// Unloaded/창 닫기가 겹쳐 와도 -1은 한 번).
    /// </summary>
    private bool _updateWatchHeld;

    /// <summary>
    /// 설정 화면 전용 직렬 워커(A42 계약, A77에서 도입). 레지스트리 등록·해제·UserChoice 쓰기·
    /// 기본 앱 개수 조회 + <b>토글 초기 상태 조회·우클릭 메뉴 등록/해제</b>(A195)가 전부 여기서 돈다.
    /// 모듈별로 나누지 않고 하나로 둔 이유 —
    /// 모듈들이 Capabilities 키 하나를 공유해 동시 쓰기가 서로를 지울 수 있다.
    /// 우클릭 메뉴도 같은 워커에 태우는 이유는 같다 — 파일 연결과 같은 HKCU\Software\Classes를
    /// 만지고, 등록 끝에 부르는 셸 통지(SHChangeNotify)도 겹치면 안 된다(A195).
    /// 화면 UI는 모듈마다 따로 놀지만(각자 링·텍스트·토글) 실제 작업은 큐 순서대로 직렬 실행된다.
    /// </summary>
    private ModuleWorker? _worker;

    /// <summary>지연 생성: Unloaded로 정리된 뒤 다시 로드돼도 되살아난다(ExplorerPane과 같은 규칙).</summary>
    private ModuleWorker Worker => _worker ??= new ModuleWorker($"{Branding.AppName} settings worker");

    /// <summary>
    /// 뷰가 화면에 붙어 있는지 (A77). 워커 결과가 Unloaded 뒤에 도착해도 UI 요소·설정 페이지 열기
    /// 같은 부수효과로 새지 않게 막는 가드다.
    /// </summary>
    private bool _uiAlive = true;

    public SettingsView(FileTypeRouter router)
    {
        InitializeComponent();
        _settings = App.Services.GetRequiredService<ISettingsService>();
        Build(router);
        Loaded += (_, _) =>
        {
            _uiAlive = true;
            Focus(FocusState.Programmatic); // 셸 키(Enter 순환·Esc 등)가 바로 듣게
        };
        Unloaded += (_, _) =>
        {
            // A77: 화면을 떠난 뒤 워커가 끝나도 UI를 만지지 않는다.
            // 진행 중인 레지스트리 작업은 중간에 끊지 않고(반쯤 등록된 상태 방지) 워커가 마저 끝낸다.
            _uiAlive = false;
            _worker?.Dispose();
            _worker = null;
        };
    }

    /// <summary>하단 바(후원 문구)를 뷰에서 떼어 셸 하단 바 한 줄에 얹는다(v0.50.0).</summary>
    public object? TakeBottomBar()
    {
        RootGrid.Children.Remove(ControlBar);
        return ControlBar;
    }

    // A151: 전체화면 토글(⛶ 버튼·F11/Esc 액셀러레이터, v0.50.0)은 전부 제거 —
    // 전체화면은 셸의 3단 모드 체계(MainWindow — Enter 순환·Alt+Enter·Esc·모드 버튼)가 담당한다.

    private void Build(FileTypeRouter router)
    {
        // A79 ③(v0.119.0): 설정 화면 상단 워드마크. 꺼져 있으면 요소를 만들지 않는다 — 빈 자리를 남기지 말 것.
        if (BrandAssets.CreateWordmark(40) is { } wordmark)
        {
            wordmark.HorizontalAlignment = HorizontalAlignment.Left;
            Root.Children.Add(wordmark);
        }

        BuildDisplaySection();
        // A222(2026-08-24): Windows 섹션(A24 "Always open files in a new instance" 토글 하나뿐이었다)은
        // 옵션 폐지와 함께 통째로 제거 — 창 재사용 규칙은 이제 설정 없이 고정이다(WindowManager 주석).

        AddHeader("Explorer integration");
        // A162(v0.171.0): 4문장 374자였던 설명을 한 줄로 줄이고 상세(관리자 권한 불필요·해제 시 완전 삭제·
        // Windows가 막는 보호 확장자와 그 대처)는 사용자 가이드 "Explorer integration" 절로 옮겼다.
        // 같은 작업에서 문구 끝에 노출돼 있던 내부 요구사항 번호도 걷어냈다 — 사용자 문구에 A번호는 없다.
        // A182: 그 상세를 다시 앱 안으로 데려왔다(펼치면 보인다). 아래 문장은 축약 직전 원문
        // (b80c437^)에서 위 한 줄이 이미 말한 부분을 뺀 나머지다.
        // ⚠️ 보호 확장자 문장만 원문 그대로 두지 않았다 — A166(v0.184.0)이 UCPD 보호 확장자에서는
        // 딥링크 자동 열기를 억제하도록 동작을 바꿨으므로, 원문("보호 확장자는 기본 앱 페이지가 열린다")을
        // 그대로 복원하면 코드와 어긋난 안내가 된다. 정보량은 유지하고 사실만 현행 동작에 맞췄다.
        Root.Children.Add(new TextBlock
        {
            Text = $"Registers file types for this user account only, and makes {Branding.AppName} the default app for them.",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        Root.Children.Add(LearnMore(
            "No administrator rights are needed, and turning a switch off removes the registration "
            + "completely - nothing is left behind. Windows protects a few file types and can refuse "
            + "that last step: for those, use \"Set default...\" beside the switch and pick "
            + $"{Branding.AppName}. If Windows blocks any other type, the Windows default-apps page "
            + "opens so you can confirm it once."));

        // ── A257(2026-08-28): 우클릭 메뉴 그룹이 절의 첫 그룹이다(A235 ①의 "마스터 다음 두 번째"를
        // 사용자 지시로 다시 교환 — 마스터는 이제 메뉴 그룹 다음, 모듈 5그룹 바로 위에 선다).
        // 이 블록은 A227 마스터의 사정권 밖이라(파일 연결이 아니다) 로직 의존이 0이고,
        // 아래 세 지역(archiveModule·archiveExts·archiveBrand)도 router만 보므로 자리를 옮겨도
        // 참조가 깨지지 않는다. 공용 _status 줄은 종전대로 절 맨 끝에 남는다.
        var archiveModule = router.Modules.FirstOrDefault(m => m.Id == "archive");
        var archiveExts = archiveModule?.SupportedExtensions ?? (IReadOnlyList<string>)[];
        // 우클릭 메뉴 라벨은 모듈 BrandName을 따른다(A52로 KOTU-zip → KOTU-archive).
        var archiveBrand = archiveModule?.BrandName ?? Branding.AppName;

        // 우클릭 메뉴 토글 통합(v0.30.0 사용자 요청): "여기에 풀기"(압축 파일)와
        // "압축하기"(모든 파일)를 하나의 스위치로 함께 등록/해제한다.
        // A183: 이 토글도 같은 절에 있으므로 아래 모듈 카드들과 같은 카드 체계에 편입한다
        // (한 절 안에 카드와 맨 토글이 섞이면 구획이 다시 흐려진다). Header였던 긴 문구는
        // 제목 + 설명 줄로 나뉘고, 아래 "Show more options" 안내도 이 카드 안으로 들어온다.
        var menuToggle = new ToggleSwitch
        {
            // A197: 아래 모듈 카드와 같은 배치·같은 정리(스위치 = 제목 왼쪽, 내장 On/Off 문구 제거,
            // MinWidth 0 유지). 한 절 안에서 카드마다 스위치 자리가 다르면 다시 흐려진다.
            OnContent = string.Empty,
            OffContent = string.Empty,
            MinWidth = 0,
            // A195: 아래 모듈 토글과 같은 이유로 IsOn 초기값을 여기서 읽지 않는다(아래 워커 조회).
            VerticalAlignment = VerticalAlignment.Center,
        };

        // A195: 등록/해제(레지스트리 쓰기 + SHChangeNotify 셸 통지)를 워커로 옮긴다 —
        // 종전 Apply()는 이 전부를 UI 스레드에서 동기로 돌렸다(ARCHITECTURE §11.1 ①).
        // 배선은 아래 파일 연결 토글(A77 계보)과 같은 관용구다: 재진입 플래그 → 토글 잠금 →
        // 진행 문구 → await Worker.Run → UI 스레드 복귀 후 잠금 해제 → 실패면 IsOn 되돌리기.
        // 다른 점 — 진행 링이 없고 n/m 진행이 없어 문구가 한 줄로 끝난다(A79 ⑤의 발바닥 스피너는
        // 모듈 단위 일괄 등록에 붙던 것으로 A292에서 그 자리와 함께 은퇴했다).
        // 같은 워커라 파일 연결 등록/해제와 겹치지 않는다(둘 다 HKCU\Software\Classes를 쓴다).
        var menuBusy = false;
        menuToggle.Toggled += async (_, _) =>
        {
            if (_suppressToggle || menuBusy) return;
            var turnedOn = menuToggle.IsOn;

            menuBusy = true;
            menuToggle.IsEnabled = false;
            var progressMessage = turnedOn
                ? "Registering the right-click menu..."
                : "Removing the right-click menu...";
            _status.Text = progressMessage;

            string? error;
            try
            {
                error = await Worker.Run(ctx => ApplyMenuRegistration(archiveExts, archiveBrand, turnedOn));
            }
            catch (Exception ex)
            {
                // 워커가 이미 닫혔거나(뷰 이탈) 예상 못 한 실패 — 종전 Apply()와 같은 실패 처리로 보낸다.
                error = ex.Message;
            }

            // 여기부터는 UI 스레드. 화면을 떠났어도 잠금은 풀어 둔다(파일 연결 토글과 같은 처리).
            menuBusy = false;
            menuToggle.IsEnabled = true;
            if (!_uiAlive) return;

            if (error is null)
            {
                // 종전 Apply()의 성공 동작(상태 줄 비우기) 그대로 — 단, 동기였던 종전과 달리
                // 작업 중에 옆 모듈 토글이 이 공용 줄에 결과를 써 놓았을 수 있다.
                // 그래서 아직 우리 진행 문구일 때만 지운다(남의 결과를 지우지 않는다).
                if (_status.Text == progressMessage) _status.Text = string.Empty;
                return;
            }

            // 종전 Apply()의 실패 동작 유지: 토글을 원위치로 되돌리고 이유를 표시한다.
            _suppressToggle = true;
            menuToggle.IsOn = !menuToggle.IsOn;
            _suppressToggle = false;
            _status.Text = "Failed to apply: " + error;
        };

        // A195: 메뉴 토글 초기값도 워커에서(아래 모듈 토글과 같은 관용구).
        var menuStateDispatcher = DispatcherQueue;
        Worker.Post(() =>
        {
            var registered = Safe(() => ExplorerIntegration.IsExtractHereMenuRegistered(archiveExts)
                                     || ExplorerIntegration.IsCompressMenuRegistered());
            menuStateDispatcher.TryEnqueue(() =>
            {
                if (!_uiAlive || menuBusy) return;
                _suppressToggle = true;
                menuToggle.IsOn = registered;
                _suppressToggle = false;
            });
        });

        // A197: 스위치(왼쪽) · 제목(가변 폭). 이 카드에는 진행 링이 없어 두 칸이면 된다.
        var menuHeaderRow = new Grid { ColumnSpacing = 8 };
        menuHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        menuHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var menuTitle = new TextBlock
        {
            Text = "Explorer right-click menu",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(menuToggle, 0);
        Grid.SetColumn(menuTitle, 1);
        menuHeaderRow.Children.Add(menuToggle);
        menuHeaderRow.Children.Add(menuTitle);

        // A220: 아래 모듈 그룹과 같은 처리 — 카드 해체·간격 구분(한 절 안에서 배치가 갈리면 다시 흐려진다).
        // A235 ④: 아래 여백 8은 뺐다(그룹 사이 = Root Spacing 12 하나로 통일).
        var menuCardBody = new StackPanel { Spacing = 6 };
        menuCardBody.Children.Add(menuHeaderRow);
        menuCardBody.Children.Add(new TextBlock
        {
            Text = $"\"Extract here with {archiveBrand}\" (archives) · \"Compress with {archiveBrand}\" (all files)",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        // A162: 다섯 문구 중 유일하게 링크를 붙이지 않은 줄이다 — 이미 한 줄(65자)이고 내용도
        // "어디에 나타나는가" 한 가지뿐이라 옮길 상세가 없다. 같은 사실은 가이드 6장에도 적혀 있다.
        // A183: 이 줄이 설명하는 대상이 위 스위치뿐이라 같은 카드 안으로 들여놓았다.
        menuCardBody.Children.Add(new TextBlock
        {
            Text = "On Windows 11 these appear under \"Show more options\" (Shift+F10).",
            FontSize = 12,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });
        Root.Children.Add(menuCardBody);

        // ── A227(2026-08-25): 스위치를 하나씩 누르지 않고 한 번에 켜고 끄는 마스터 스위치.
        // 등록 경로를 새로 만들지 않는다 — 아래 확장자 토글들의 IsOn을 프로그램적으로 세팅해
        // 기존 Toggled 흐름(A77 계보: 재진입 방지·토글 잠금·워커 등록/해제·실패 되돌리기)을
        // 그대로 태운다. 그래서 이 그룹에는 제 진행 표시도 제 결과 문구도 없다.
        // A292: 대신 눌러 주는 대상이 모듈 토글 5개에서 아래 Advanced 펼침 안의 확장자 토글
        // 전부로 바뀌었다 — 마스터의 semantics(상태형 표시 + 대행 누름)와 "전부 켜짐일 때만 On"
        // 되계산 규칙은 A227 그대로다. 마스터 off가 하위 토글을 비활성(그레이)으로 만들지도
        // 않는다 — A227이 모듈 토글을 그레이 처리하지 않던 것과 같은 결이다.
        // A326: 대행 대상이 다시 모듈 토글 5개다(확장자 48개 → 모듈 5개). 마스터 자신의 코드는
        // 대상 목록의 이름만 바뀌었을 뿐 A227/A292 그대로다 — 하위 토글의 IsOn을 대신 눌러
        // 그쪽 Toggled를 태우고, 하위가 값을 확정할 때마다 RecomputeMaster가 표시를 다시 잰다.
        // 사정권: 파일 연결 모듈 토글만. 위 우클릭 메뉴 토글(Extract here·Compress)은
        // 파일 연결이 아니므로 이 스위치가 건드리지 않는다(사양 확정).
        // A257: 자리는 메뉴 그룹 다음 — 대신 눌러 주는 대상(Advanced 펼침) 바로 위에 선다.
        var masterToggle = new ToggleSwitch
        {
            // A197과 같은 문법 — 스위치가 제목 왼쪽, 내장 On/Off 문구 제거, MinWidth 0(기본 154 해제).
            OnContent = string.Empty,
            OffContent = string.Empty,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 아래 foreach가 만드는 모듈 토글이 화면 순서대로 담긴다(A326 — A292의 확장자 토글 목록을
        // 대체). 배선을 루프보다 먼저 해도 되는 이유 — 아래 두 핸들러는 사람이 스위치를 만지거나
        // 워커 답이 온 뒤에야 도는데, 그때는 이미 Build()가 끝나 목록이 다 차 있다
        // (클로저가 리스트 자체를 잡고 있다).
        var moduleToggles = new List<ToggleSwitch>();

        // 마스터 표시 규칙 = "전부 켜짐일 때만 On". 모듈 토글이 값을 확정하는 두 시점
        // (초기 조회 반영 · 일괄 작업 종료 후 실제 레지스트리 상태로 재동기) 뒤에 불린다.
        // 대입은 마스터 자신의 Toggled를 다시 발화시키므로 전용 가드로 감싼다(_suppressToggle 금지 —
        // 필드 주석 참고: 그 플래그를 켜면 모듈 토글의 등록 흐름까지 막힌다).
        void RecomputeMaster()
        {
            if (moduleToggles.Count == 0) return; // 연결 가능한 모듈이 없으면 "전부 켜짐"도 성립하지 않는다
            var allOn = moduleToggles.All(t => t.IsOn);
            if (masterToggle.IsOn == allOn) return;
            _suppressMasterToggle = true;
            masterToggle.IsOn = allOn;
            _suppressMasterToggle = false;
        }

        masterToggle.Toggled += (_, _) =>
        {
            if (_suppressMasterToggle) return; // 위 되계산이 만든 발화 — 사람이 누른 게 아니다
            // A292: 펼침이 닫혀 있어도 펼치지 않는다 — A235 ②의 자동 펼침을 A257이 걷어낸 뒤로
            // 마스터는 화면 상태를 만지지 않는다. 결과는 defaults 줄·공용 상태 줄이 말한다.
            var turnedOn = masterToggle.IsOn;  // 아래 대입이 되계산을 부를 수 있으니 목표값을 먼저 잡는다
            foreach (var moduleToggle in moduleToggles)
            {
                // 이미 목표 상태면 그대로 둔다(IsOn 대입이 없으면 Toggled도 없다 = 헛작업 없음).
                // 작업 중인 토글(IsEnabled = false)은 건너뛴다 — 기다리지도, 따로 안내하지도 않는다.
                // 그 토글만 작업이 끝난 뒤 사용자가 다시 누르면 된다(A227 사양 확정 그대로).
                if (moduleToggle.IsEnabled && moduleToggle.IsOn != turnedOn)
                    moduleToggle.IsOn = turnedOn;
            }
            // 모듈 5개 × 확장자 10~14개가 한꺼번에 워커 큐에 쌓이지만 워커는 직렬이라(A195) 서로의
            // 레지스트리 쓰기를 지우지 않는다. 마스터 자신의 표시는 모듈 하나가 끝날 때마다
            // RecomputeMaster가 잡는다.
        };

        // A220과 같은 그룹 문법(카드 없음 · 안쪽 Spacing 6 · 아래 여백은 A235 ④에서 0). 진행 링이 없어 두 칸이면 된다.
        var masterHeaderRow = new Grid { ColumnSpacing = 8 };
        masterHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        masterHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var masterTitle = new TextBlock
        {
            Text = "Register all file associations",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(masterToggle, 0);
        Grid.SetColumn(masterTitle, 1);
        masterHeaderRow.Children.Add(masterToggle);
        masterHeaderRow.Children.Add(masterTitle);

        // A235 ③: 부연 설명 줄("Turns every module's file association on or off in one go.")은 뺐다 —
        // 제목이 이미 같은 말을 하는데 절만 길어졌다는 사용자 지시(2026-08-27). 지역 요소였으므로
        // 남는 참조가 없다. 자식이 헤더 행 하나뿐이어도 묶음은 유지한다(절 안 다른 그룹과 같은 문법).
        var masterCardBody = new StackPanel { Spacing = 6 };
        masterCardBody.Children.Add(masterHeaderRow);
        Root.Children.Add(masterCardBody);

        // ── A292: "Advanced options" 링크 + 펼침. 펼치면 모듈 블록 5개가 나온다(A257의 상시 펼침
        // 5그룹을 다시 접은 셈이다).
        // A326: 펼침 안의 단위가 확장자에서 다시 모듈로 돌아왔다 — 링크·닫힘 시작·비저장은 무변경.
        // 컨트롤 선례: WinUI Expander는 저장소 선례 0건이라 쓰지 않는다(A182와 같은 판단) —
        // LearnMore와 같은 HyperlinkButton + Visibility 토글 문법을 그대로 쓴다.
        // 펼침 상태는 저장하지 않는다(사양 확정 — 세션마다 닫힘으로 시작).
        // 안쪽 모듈 블록 사이 간격 12는 이 상자의 Spacing이 만든다(A257의 moduleOptions와 같은 책임).
        var advancedBody = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(0, 4, 0, 0), // 링크와 첫 블록 사이 — LearnMore 본문과 같은 값
            Visibility = Visibility.Collapsed,
        };
        var advancedLink = new HyperlinkButton
        {
            // LearnMore 버튼과 같은 규격(FontSize 12 · Padding 0 · 좌측 정렬) — 라벨만 다르다.
            Content = "Advanced options",
            FontSize = 12,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        advancedLink.Click += (_, _) =>
        {
            var expanding = advancedBody.Visibility == Visibility.Collapsed;
            advancedBody.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
            advancedLink.Content = expanding ? "Hide advanced options" : "Advanced options";
        };
        // 링크와 펼침 본문을 StackPanel 하나로 묶어 Root에 넣는다(LearnMore와 같은 근거 —
        // 접혔을 때 Root의 Spacing 12가 빈 본문 위아래로 두 번 붙지 않게).
        var advancedGroup = new StackPanel();
        advancedGroup.Children.Add(advancedLink);
        advancedGroup.Children.Add(advancedBody);
        Root.Children.Add(advancedGroup);

        // 토글 순서(A35, 사용자 확정 2026-08-10): 이미지 → 비디오 → 오디오 → 문서 → 압축.
        // 시작 메뉴 번호 순서(1이미지 2영상 3오디오 4문서 5압축)와 일치시킨 것 —
        // v0.28.0의 "압축→문서→영상→이미지"를 대체한다.
        // 파일을 다루지 않는 모듈(hardware)은 연결할 확장자가 없으므로 토글을 만들지 않는다.
        // A59(v0.113.0): All Readable도 이 섹션에 없다 — 담당 확장자가 다른 모듈의 합집합이라
        // 함께 등록하면 확장자마다 ProgID·UserChoice·Capabilities를 서로 덮어쓴다
        // (제외 판단의 단일 소스 = IModule.RegistersFileAssociations).
        string[] associationOrder = ["image", "video", "audio", "document", "archive"];
        var associationModules = router.Modules
            .Where(m => m.SupportedExtensions.Count > 0 && m.RegistersFileAssociations)
            .OrderBy(m =>
            {
                var i = Array.IndexOf(associationOrder, m.Id);
                return i < 0 ? int.MaxValue : i;
            });

        foreach (var module in associationModules)
        {
            // A326: 모듈 하나 = Advanced 펼침 안의 블록 하나(헤더 행[스위치 + 소제목] + defaults 줄).
            // A292의 확장자 토글 나열은 이 헤더 행의 스위치 하나로 접혔다 — 복원 참조 = A326 직전 git 이력.
            // 카드(A183)·진행 링(A197)은 A292가 걷어낸 뒤로 돌아오지 않았다: 진행은 공용 상태 줄이 말한다.
            var moduleBlock = new StackPanel { Spacing = 6 };

            // 헤더 행 문법 = A197 최소형(스위치가 제목 왼쪽 · 내장 On/Off 문구 제거 · MinWidth 0).
            // 소제목 = 모듈 BrandName(A52 계보 — 우클릭 메뉴 라벨과 같은 출처).
            var moduleToggle = new ToggleSwitch
            {
                OnContent = string.Empty,
                OffContent = string.Empty,
                MinWidth = 0,
                // A195와 같은 이유로 IsOn 초기값을 여기서 읽지 않는다(아래 워커 조회).
                // 답이 오기 전·읽기 실패는 둘 다 Off로 보인다 — 종전 폴백과 같은 표시.
                VerticalAlignment = VerticalAlignment.Center,
            };
            moduleToggles.Add(moduleToggle); // A227/A326: 마스터가 대신 눌러 줄 대상 — 화면 순서 그대로

            var moduleHeaderRow = new Grid { ColumnSpacing = 8 };
            moduleHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            moduleHeaderRow.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var moduleTitle = new TextBlock
            {
                Text = module.BrandName,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(moduleToggle, 0);
            Grid.SetColumn(moduleTitle, 1);
            moduleHeaderRow.Children.Add(moduleToggle);
            moduleHeaderRow.Children.Add(moduleTitle);
            moduleBlock.Children.Add(moduleHeaderRow);

            // A25(v0.61.0): 현재 기본 앱 현황(n/m) + 확장자별 '연결 프로그램' 대화상자 진입 —
            // 모듈 카드에서 그대로 옮겨 왔다(A292). "Set default..."는 보호 확장자(.pdf 등)의
            // 유일한 앱 내 지정 진입로라 이 블록에 남아야 한다.
            // A326: 이 줄은 모듈 스위치가 부분 등록을 Off로 감출 때 실제 상태를 드러내는 안전판이기도
            // 하다 — 일괄 작업이 끝날 때마다(ModuleOutcome.Defaults) 다시 세어 넣는다.
            var defaultsText = new TextBlock
            {
                FontSize = 12,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
                // 조회 결과가 오기 전 자리 — 숫자만 나중에 채워져 줄 너비가 튀지 않는다.
                Text = $"Default app for .../{module.SupportedExtensions.Count} extensions",
            };

            void ShowDefaults(int count) =>
                defaultsText.Text = $"Default app for {count}/{module.SupportedExtensions.Count} extensions";

            // A77: 레지스트리 조회는 워커에서, 대입만 UI에서. 큐가 직렬이라 등록 작업 뒤에 넣으면
            // 항상 '작업이 끝난 뒤의 값'을 읽는다.
            void RefreshDefaultsAsync()
            {
                var dispatcher = DispatcherQueue;
                Worker.Post(() =>
                {
                    var count = SafeCountDefaults(module);
                    dispatcher.TryEnqueue(() =>
                    {
                        if (_uiAlive) ShowDefaults(count);
                    });
                });
            }
            RefreshDefaultsAsync();

            var setDefaultButton = new DropDownButton
            {
                Content = "Set default...",
                FontSize = 12,
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var flyout = new MenuFlyout();
            foreach (var ext in module.SupportedExtensions)
            {
                var item = new MenuFlyoutItem { Text = ext };
                item.Click += (_, _) =>
                {
                    ExplorerIntegration.ShowSetDefaultDialog(GetHwnd(), ext);
                    RefreshDefaultsAsync(); // 대화상자에서 고르면 즉시 반영된다
                };
                flyout.Items.Add(item);
            }
            setDefaultButton.Flyout = flyout;

            // A292: 진행 문구 칸(구 progressText)이 빠져 두 칸이면 된다 — 진행도 결과도 공용 상태
            // 줄이 말한다(A326의 일괄 처리 n/m 진행 문구도 같은 줄에 쓴다).
            var defaultsRow = new Grid { ColumnSpacing = 12 };
            defaultsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            defaultsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(defaultsText, 0);
            Grid.SetColumn(setDefaultButton, 1);
            defaultsRow.Children.Add(defaultsText);
            defaultsRow.Children.Add(setDefaultButton);
            moduleBlock.Children.Add(defaultsRow);

            // 같은 모듈의 재진입 방지 플래그(A77 계보). 작업 중에는 토글도 비활성이라 사람
            // 조작으로는 도달하지 않지만, 표시 재동기나 마스터의 프로그램적 변경까지 막아 준다.
            var busy = false;
            var extensionCount = module.SupportedExtensions.Count;

            moduleToggle.Toggled += async (_, _) =>
            {
                if (_suppressToggle || busy) return;
                var turnedOn = moduleToggle.IsOn;

                // A326: 모듈 하나 = 확장자 10~14개의 레지스트리 작업이라 진행이 눈에 보인다.
                // 진행 링(A197)은 되살리지 않고 공용 상태 줄에 n/m을 쓴다 — 진행·결과 창구가 하나다.
                // 반영 트리거는 종전 관례 그대로 토글 즉시 1회다(스로틀 없음 — 연타는 잠금이 막는다).
                busy = true;
                moduleToggle.IsEnabled = false;

                var progressDispatcher = DispatcherQueue;
                var verb = turnedOn ? "registering" : "removing";
                _status.Text = $"{module.BrandName}: {verb} {extensionCount} file associations...";

                // 워커 스레드에서 불린다 — UI 대입은 반드시 디스패처를 거친다(ARCHITECTURE §11.1).
                void ReportProgress(int done, int total) =>
                    progressDispatcher.TryEnqueue(() =>
                    {
                        if (!_uiAlive) return;
                        _status.Text = $"{module.BrandName}: {verb} {done}/{total} file associations...";
                    });

                ModuleOutcome outcome;
                try
                {
                    outcome = await Worker.Run(ctx => ApplyModuleAssociation(module, turnedOn, ReportProgress));
                }
                catch (Exception ex)
                {
                    // 워커가 이미 닫혔거나(뷰 이탈) 예상 못 한 실패 — 아무것도 쓰지 못했다고 본다.
                    // 표시는 누르기 전 값(!turnedOn)으로 되돌리고, 개수는 -1 = "모름"으로 둔다.
                    outcome = new ModuleOutcome(false, ex.Message, 0, !turnedOn, -1);
                }

                // 여기부터는 UI 스레드. 화면을 떠났어도 잠금은 풀어 둔다(다시 로드되면 그대로 쓰인다).
                busy = false;
                moduleToggle.IsEnabled = true;
                if (!_uiAlive) return;

                if (outcome.Defaults >= 0) ShowDefaults(outcome.Defaults);

                // A326: 표시는 늘 "그 모듈의 확장자가 전부 등록됐는가"로 다시 맞춘다(사양 3 — 부분
                // 등록은 Off). 일괄 처리는 부분 실패가 가능해서, A292의 확장자 판처럼 그냥 원위치로
                // 되돌리면(한 건짜리라 그것으로 충분했다) 화면이 레지스트리와 어긋난다.
                // 대입은 _suppressToggle로 감싼다 — 이 핸들러의 재귀 발화를 막는 표지다.
                if (moduleToggle.IsOn != outcome.AllRegistered)
                {
                    _suppressToggle = true;
                    moduleToggle.IsOn = outcome.AllRegistered;
                    _suppressToggle = false;
                }
                // A227: 모듈 토글 값이 확정된 뒤 한 번 — 성공·실패 어느 쪽이든 여기 한 곳이면 된다.
                RecomputeMaster();

                if (!outcome.Ok)
                {
                    _status.Text = "Failed to apply: " + outcome.Error;
                    return;
                }

                if (!turnedOn)
                {
                    _status.Text = $"{module.BrandName}: removed {extensionCount} file associations.";
                    return;
                }

                // 켤 때만: A38 — 기본 앱까지 자동 지정 시도(ApplyModuleAssociation 안에서 끝났다).
                // A166의 안내는 유지하되 설정 딥링크 자동 열기는 하지 않는다 — 마스터가 모듈을
                // 연달아 누르면 설정 페이지가 연발로 열리기 때문이다(A292 확정. "Set default..."
                // 진입로는 위 defaults 줄에 있다). 확장자별 두 갈래(보호 확장자/일반)는 일괄 처리라
                // 한 문장으로 합쳤다 — 어느 확장자가 걸렸는지는 defaults 줄의 n/m이 말한다.
                _status.Text = outcome.DefaultsSet >= extensionCount
                    ? $"{module.BrandName}: registered {extensionCount} extensions and set as the default app for all of them."
                    : $"{module.BrandName}: registered {extensionCount} extensions. Windows kept the current "
                      + $"default app for some of them. Use \"Set default...\" and choose {module.BrandName}, "
                      + "or pick it in Windows Settings.";
            };

            // A195: 토글 초기값(레지스트리 읽기)도 워커에서 — 관용구는 위 RefreshDefaultsAsync와
            // 같다: 워커에서 읽고, DispatcherQueue로 돌아와 대입한다. 같은 워커 큐라 등록/해제
            // 작업과 순서가 보장되고(직렬), 화면을 떠난 뒤 도착한 답은 _uiAlive가 막는다.
            // Toggled를 다시 발화시키지 않도록 _suppressToggle로 감싼다.
            // 이름이 dispatcher가 아닌 이유 — RefreshDefaultsAsync 안의 지역 변수와 이름이 겹친다.
            var stateDispatcher = DispatcherQueue;
            Worker.Post(() =>
            {
                var registered = AllExtensionsRegistered(module);
                stateDispatcher.TryEnqueue(() =>
                {
                    // busy = 답이 오기 전에 사람이 먼저 토글했다 — 그 작업 결과가 우선이다.
                    if (!_uiAlive || busy) return;
                    _suppressToggle = true;
                    moduleToggle.IsOn = registered;
                    _suppressToggle = false;
                    // A227: 초기 조회는 모듈마다 따로 도착한다 — 도착할 때마다 마스터를 다시 잰다.
                    // 다 도착하기 전에는 아직 안 읽은 토글이 Off로 보이므로 마스터도 Off가 자연스럽다.
                    RecomputeMaster();
                });
            });

            // A257의 moduleOptions와 같은 책임 이동 — 이제 Advanced 펼침 본문이 블록 사이 간격 12를 만든다.
            advancedBody.Children.Add(moduleBlock);
        }

        // 공용 상태 줄은 그룹 밖에 남는다 — 모듈 토글과 이 메뉴 토글이 함께 쓰는 한 줄이라
        // 어느 그룹에도 속하지 않는다(A292: 모듈별 progressText가 사라져 이제 진행·결과가 전부 이
        // 줄로 온다. A326의 일괄 처리 n/m 진행 문구도 여기다).
        Root.Children.Add(_status);

        // A292: Playback 절이 Explorer integration 바로 아래로 올라왔다(A258의 "Updates 바로 앞"을
        // 대체 — 마스터·Advanced 링크·Playback이 대부분 한 화면에 함께 보이게 한다는 사용자 사양).
        // A258이 피하려던 문제(머리글 없는 아래 네 카드가 Playback 밑으로 읽힘)는 아래
        // "Troubleshooting" 머리글 신설로 푼다.
        BuildPlaybackSection();

        // A292: 진단 카드 3장 + 설정 파일 카드는 지금까지 머리글 없이 Explorer integration에 딸려
        // 읽혔다 — Playback이 그 위로 끼어들면서 소속이 흐려져 전용 머리글을 신설한다(네 카드 전부
        // "고장 수리·직접 편집" 성격이라 이 이름으로 묶인다). 카드 자체·Updates와의 상대 순서는 무변경.
        AddHeader("Troubleshooting");
        BuildShellDiagnosticsSection(); // A234: 설정 파일 안내 바로 위 — 셸 키 진단 오버레이 토글
        BuildEditorDecorDiagnosticsSection(); // A285: 그 바로 옆 — 에디터 장식 EOF 계측 토글
        BuildAudioSwapDiagnosticsSection(); // A301: 그 바로 옆 — 오디오 비주얼라이저 교체 계측 토글
        BuildNavTimingDiagnosticsSection(); // 그 바로 옆 — 폴더 항해 계측판 토글
        BuildSettingsFileSection(); // A36: "Open settings.json"(A292부터 Troubleshooting 절 끝)

        AddHeader("Updates");
        var currentVersion = typeof(SettingsView).Assembly.GetName().Version?.ToString(3) ?? "?";
        BuildUpdatesSection(currentVersion);

        AddHeader("About");
        // 저장소 주소는 클릭해서 이동 가능 (v0.52.0 사용자 요청)
        var aboutLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        aboutLine.Children.Add(new TextBlock
        {
            Text = $"KOTU v{currentVersion} ·",
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        });
        aboutLine.Children.Add(new HyperlinkButton
        {
            Content = "github.com/zpstudios/kotu",
            NavigateUri = new Uri(Branding.RepoUrl), // A162: 주소 리터럴은 Branding 한 곳에만 둔다
            Padding = new Thickness(0),
        });
        Root.Children.Add(aboutLine);
        Root.Children.Add(new TextBlock
        {
            Text = "Mission Statement",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0),
        });
        Root.Children.Add(new TextBlock
        {
            Text = Branding.MissionStatement,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
        });
        // Patreon 후원 문구는 About 본문이 아니라 하단 바에 표시한다 (v0.52.0 사용자 정정).
    }

    /// <summary>
    /// Display 섹션: 앱 UI 스케일 — 옵션은 윈도우 디스플레이 설정과 같은 배율 목록(UiScale.Percents),
    /// 바꾸면 저장 후 열린 모든 창에 즉시 적용된다(UiScale.Changed → MainWindow.ApplyUiScale).
    /// (문서 편집기 폭 콤보는 A181에서 제거 — 아래 주석 참고. A48의 "Windows display scale
    /// (all apps)" 콤보·Integration/DisplayScale.cs는 A221(2026-08-24)에서 전면 제거 —
    /// "우리 앱에서 건드릴 게 아님" 사용자 지시. 복원 참조 = A48 도입(v0.214.0) 이후 git 이력.)
    /// </summary>
    private void BuildDisplaySection()
    {
        AddHeader("Display");
        // A162(v0.171.0): "System default"의 뜻과 적용 범위 설명은 가이드 "UI scale" 절로 옮겼다.
        Root.Children.Add(new TextBlock
        {
            Text = $"Scale of the {Branding.AppName} interface, applied to all open windows immediately.",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        // A182: 축약 때 가이드로 옮겼던 문장을 앱 안 펼침으로 되돌린다(원문 = b80c437^).
        Root.Children.Add(LearnMore(
            "\"System default\" follows the Windows display scaling; picking a value overrides it "
            + "for this app only."));
        // A246: A41의 단축키 안내 한 줄(Ctrl+±·Ctrl+넘패드 *·하단 바 Ctrl+휠)은 제거됐다 —
        // 그 키·휠 진입로 자체가 회수돼 이 콤보가 UI 배율의 유일한 변경 진입로다.

        // A44(A21 보강): 현재 윈도우 배율을 별도 줄이 아니라 배율 목록 항목 옆에 표기한다.
        // XamlRoot.RasterizationScale = 이 창이 떠 있는 모니터의 시스템 배율(앱 자체 배율과 무관).
        // 항목을 ComboBoxItem으로 만들어 Content만 갱신 — 선택 상태를 건드리지 않고 라이브 갱신 가능.
        // 생성 시점엔 XamlRoot가 없으므로 Loaded에서 채우고, 모니터 이동/배율 변경(Changed)에 추종.
        // 헤더 "(this app only)"는 A48이 OS 배율 콤보와 구분하려 붙인 것 — A221로 그 콤보는
        // 사라졌지만 "OS 배율이 아니라 앱 배율"이라는 구분 자체는 계속 유효해 문구를 유지한다.
        var scaleBox = new ComboBox { Header = "UI scale (this app only)", MinWidth = 200 };
        scaleBox.Items.Add(new ComboBoxItem { Content = "System default" });
        foreach (var p in UiScale.Percents)
            scaleBox.Items.Add(new ComboBoxItem { Content = $"{p}%", Tag = p });

        // 윈도우 배율이 특이값(예: 커스텀 110%)이라 목록에 일치 항목이 없을 때만 보이는 안내 줄.
        var offListNote = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        void UpdateWindowsScaleMark()
        {
            if (XamlRoot is not { } xr) return;
            var winPercent = (int)Math.Round(xr.RasterizationScale * 100);
            var matched = false;
            foreach (var item in scaleBox.Items)
            {
                if (item is not ComboBoxItem { Tag: int p } cbi) continue;
                var text = p == winPercent ? $"{p}% (current Windows setting)" : $"{p}%";
                if (!Equals(cbi.Content as string, text)) cbi.Content = text;
                matched |= p == winPercent;
            }
            offListNote.Text = matched
                ? string.Empty
                : $"Current Windows display scaling on this monitor is {winPercent}%, which is not in the list above.";
            offListNote.Visibility = matched ? Visibility.Collapsed : Visibility.Visible;
        }
        Loaded += (_, _) =>
        {
            UpdateWindowsScaleMark();
            if (XamlRoot is { } xr) xr.Changed += (_, _) => UpdateWindowsScaleMark();
        };

        var current = _settings.Get(UiScale.SettingKey, 0);
        var index = Array.IndexOf(UiScale.Percents, current);
        scaleBox.SelectedIndex = current <= 0 || index < 0 ? 0 : index + 1;

        scaleBox.SelectionChanged += (_, _) =>
        {
            var value = scaleBox.SelectedIndex <= 0 ? 0 : UiScale.Percents[scaleBox.SelectedIndex - 1];
            if (value == _settings.Get(UiScale.SettingKey, 0)) return;
            _settings.Set(UiScale.SettingKey, value);
            _settings.Save();
            UiScale.NotifyChanged();
        };
        // A41→A246: 단축키(Ctrl+±·Ctrl+휠) 진입로는 회수됐지만 이 동기 구독은 유지한다 —
        // 다른 창의 설정 콤보가 배율을 바꾸면 열려 있는 이 화면의 콤보 표시도 따라와야 한다.
        // 위 SelectionChanged의 "저장값과 같으면 return" 가드가 재저장 루프를 끊고, 다른 창
        // (다른 UI 스레드)에서 발화될 수 있어 MainWindow.ApplyUiScale과 같은 마샬링을 거친다.
        // 구독 해제는 Unloaded(모듈 전환으로 설정 화면이 내려갈 때) — 같은 클로저라 -=가 성립한다.
        void SyncScaleBoxFromSetting()
        {
            if (DispatcherQueue is { } dq && !dq.HasThreadAccess)
            {
                dq.TryEnqueue(SyncScaleBoxFromSetting);
                return;
            }
            var saved = _settings.Get(UiScale.SettingKey, 0);
            var savedIndex = Array.IndexOf(UiScale.Percents, saved);
            scaleBox.SelectedIndex = saved <= 0 || savedIndex < 0 ? 0 : savedIndex + 1;
        }
        UiScale.Changed += SyncScaleBoxFromSetting;
        Unloaded += (_, _) => UiScale.Changed -= SyncScaleBoxFromSetting;
        Root.Children.Add(scaleBox);
        Root.Children.Add(offListNote);

        // A48(v0.214.0)의 "Windows display scale (all apps)" 콤보·안내·딥링크 폴백은
        // A221(2026-08-24)에서 전면 제거 — OS 배율은 이 앱이 건드릴 축이 아니라는 사용자 지시.
        // Integration/DisplayScale.cs(비공식 DisplayConfig 래퍼)도 함께 삭제됐다.

        // A171의 "Document editor width" 콤보는 A181에서 제거 — 본문은 항상 창 폭을 꽉 채우고,
        // 크기 조절은 문서 편집기 안의 Ctrl+휠 줌(document.zoom, 즉시 저장)이 대신한다.
        // 설정 화면 UI는 두지 않는다(뷰에서 직접 조작·표시하는 값이라 여기 둘 게 없다).
    }

    // Windows 섹션(A24 창 재사용 토글)은 A222(2026-08-24)에서 제거 — 복원 참조 = git 이력
    // (v0.65.0 A24 도입 → A222 직전까지 존치). 명시적 새 인스턴스 조작(Shift+N·Shift+더블클릭·
    // 우클릭 메뉴)은 설정과 무관하게 그대로 살아 있다.

    /// <summary>
    /// A234 배치 1: 셸 키(F11/F12) 진단 오버레이 토글 — 설정 파일 안내 바로 위의 단일 카드.
    /// 배선은 표시 계열 토글의 최소형이다: Set → Save → NotifyChanged 세 줄(UiScale 콤보와
    /// 같은 즉시 반영 축 — 열린 모든 창의 MainWindow가 ShellDiagnostics.Changed를 구독한다).
    /// 레지스트리·워커와 무관하므로 위 파일 연결 토글들의 busy·<see cref="_suppressToggle"/>
    /// 축은 일절 쓰지 않는다(그 가드는 파일 연결 전용 — 필드 주석 참고). 되돌아오는 동기화
    /// 구독도 두지 않는다 — 변경 진입로가 이 토글 하나뿐이라 불필요(UiScale 콤보가 A41 전에는
    /// 구독하지 않던 것과 같은 근거). 카드 문법은 A197/A220(스위치 왼쪽 + 제목 TextBlock,
    /// 내장 On/Off 문구 제거, 그룹 간 여백 구획) 그대로다.
    /// </summary>
    private void BuildShellDiagnosticsSection()
    {
        var toggle = new ToggleSwitch
        {
            // A197과 같은 문법 — 스위치가 제목 왼쪽, 내장 On/Off 문구 제거, MinWidth 0(기본 154 해제).
            OnContent = string.Empty,
            OffContent = string.Empty,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
            IsOn = _settings.Get(ShellDiagnostics.SettingKey, false), // 파일 저장 — 재시작 후에도 유지
        };
        toggle.Toggled += (_, _) =>
        {
            _settings.Set(ShellDiagnostics.SettingKey, toggle.IsOn);
            _settings.Save();
            ShellDiagnostics.NotifyChanged(); // 열린 모든 창의 스트립이 즉시 켜지고 꺼진다
        };

        var headerRow = new Grid { ColumnSpacing = 8 };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var title = new TextBlock
        {
            Text = "Shell key diagnostics",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(toggle, 0);
        Grid.SetColumn(title, 1);
        headerRow.Children.Add(toggle);
        headerRow.Children.Add(title);

        var cardBody = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        cardBody.Children.Add(headerRow);
        cardBody.Children.Add(new TextBlock
        {
            Text = "Shows a live overlay with keyboard focus and key routing state. For troubleshooting only.",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        Root.Children.Add(cardBody);
    }

    /// <summary>
    /// A285: 에디터 장식 EOF 계측 토글 — 위 A234 카드(BuildShellDiagnosticsSection) 바로 옆·
    /// 같은 방식이다. 배선도 그대로 최소형(Set → Save → NotifyChanged 세 줄 — 열린 문서 뷰의
    /// DocumentView가 EditorDecorDiagnostics.Changed를 구독한다). busy·<see cref="_suppressToggle"/>
    /// 축 불사용·되돌아오는 동기화 구독 없음(변경 진입로가 이 토글 하나뿐)·카드 문법
    /// A197/A220까지 전부 A234 카드와 같은 근거·같은 형태다.
    /// </summary>
    private void BuildEditorDecorDiagnosticsSection()
    {
        var toggle = new ToggleSwitch
        {
            // A197과 같은 문법 — 스위치가 제목 왼쪽, 내장 On/Off 문구 제거, MinWidth 0(기본 154 해제).
            OnContent = string.Empty,
            OffContent = string.Empty,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
            IsOn = _settings.Get(EditorDecorDiagnostics.SettingKey, false), // 파일 저장 — 재시작 후에도 유지
        };
        toggle.Toggled += (_, _) =>
        {
            _settings.Set(EditorDecorDiagnostics.SettingKey, toggle.IsOn);
            _settings.Save();
            EditorDecorDiagnostics.NotifyChanged(); // 열린 문서 뷰의 오버레이가 즉시 켜지고 꺼진다
        };

        var headerRow = new Grid { ColumnSpacing = 8 };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var title = new TextBlock
        {
            Text = "Editor decor diagnostics",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(toggle, 0);
        Grid.SetColumn(title, 1);
        headerRow.Children.Add(toggle);
        headerRow.Children.Add(title);

        var cardBody = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        cardBody.Children.Add(headerRow);
        cardBody.Children.Add(new TextBlock
        {
            Text = "Shows live end-of-file marker geometry inside the document editor. For troubleshooting only.",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        Root.Children.Add(cardBody);
    }

    /// <summary>
    /// A301: 오디오 비주얼라이저 교체 계측 토글 — 위 A285 카드(BuildEditorDecorDiagnosticsSection)
    /// 바로 옆·같은 방식이다. 배선도 그대로 최소형(Set → Save → NotifyChanged 세 줄 — 열린
    /// 오디오 뷰의 AudioPlayerView가 AudioDiagnostics.Changed를 구독한다). busy·
    /// <see cref="_suppressToggle"/> 축 불사용·되돌아오는 동기화 구독 없음(변경 진입로가 이 토글
    /// 하나뿐)·카드 문법 A197/A220까지 전부 A285 카드와 같은 근거·같은 형태다.
    /// </summary>
    private void BuildAudioSwapDiagnosticsSection()
    {
        var toggle = new ToggleSwitch
        {
            // A197과 같은 문법 — 스위치가 제목 왼쪽, 내장 On/Off 문구 제거, MinWidth 0(기본 154 해제).
            OnContent = string.Empty,
            OffContent = string.Empty,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
            IsOn = _settings.Get(AudioDiagnostics.SettingKey, false), // 파일 저장 — 재시작 후에도 유지
        };
        toggle.Toggled += (_, _) =>
        {
            _settings.Set(AudioDiagnostics.SettingKey, toggle.IsOn);
            _settings.Save();
            AudioDiagnostics.NotifyChanged(); // 열린 오디오 뷰의 오버레이가 즉시 켜지고 꺼진다
        };

        var headerRow = new Grid { ColumnSpacing = 8 };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var title = new TextBlock
        {
            Text = "Audio visualizer diagnostics",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(toggle, 0);
        Grid.SetColumn(title, 1);
        headerRow.Children.Add(toggle);
        headerRow.Children.Add(title);

        var cardBody = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        cardBody.Children.Add(headerRow);
        cardBody.Children.Add(new TextBlock
        {
            Text = "Shows visualizer style swap timing inside the audio player. For troubleshooting only.",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        Root.Children.Add(cardBody);
    }

    /// <summary>
    /// 폴더 항해 계측판 토글 — 위 A301 카드(BuildAudioSwapDiagnosticsSection) 바로 옆·같은
    /// 방식이다. 배선도 그대로 최소형(Set → Save → NotifyChanged 세 줄 — 열린 모든 창의
    /// MainWindow가 NavDiagnostics.Changed를 구독해 스트립과 하트비트를 즉시 켜고 끈다).
    /// busy·<see cref="_suppressToggle"/> 축 불사용·되돌아오는 동기화 구독 없음(변경 진입로가
    /// 이 토글 하나뿐)·카드 문법 A197/A220까지 앞 세 진단 카드와 같은 근거·같은 형태다.
    /// </summary>
    private void BuildNavTimingDiagnosticsSection()
    {
        var toggle = new ToggleSwitch
        {
            // A197과 같은 문법 — 스위치가 제목 왼쪽, 내장 On/Off 문구 제거, MinWidth 0(기본 154 해제).
            OnContent = string.Empty,
            OffContent = string.Empty,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
            IsOn = _settings.Get(NavDiagnostics.SettingKey, false), // 파일 저장 — 재시작 후에도 유지
        };
        toggle.Toggled += (_, _) =>
        {
            _settings.Set(NavDiagnostics.SettingKey, toggle.IsOn);
            _settings.Save();
            NavDiagnostics.NotifyChanged(); // 열린 모든 창의 계측판이 즉시 켜지고 꺼진다
        };

        var headerRow = new Grid { ColumnSpacing = 8 };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var title = new TextBlock
        {
            Text = "Folder navigation timing",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(toggle, 0);
        Grid.SetColumn(title, 1);
        headerRow.Children.Add(toggle);
        headerRow.Children.Add(title);

        var cardBody = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        cardBody.Children.Add(headerRow);
        cardBody.Children.Add(new TextBlock
        {
            Text = "Shows how long each stage of a folder change takes, and the longest gap the "
                + "interface stayed frozen. For troubleshooting only.",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        Root.Children.Add(cardBody);
    }

    /// <summary>
    /// A258(v0.258.0): Playback 절 — 영상·오디오 <b>공용</b> 재생 옵션. 첫 항목은
    /// "Auto-play next file" 토글이다(플레이어 바에는 대응 버튼을 두지 않는다 — A255
    /// 루프 버튼과 뜻이 겹쳐 보이기 때문). 배선은 A234 셸 진단 토글의 최소형에서 알림 한 줄까지
    /// 뺀 것이다: Set → Save로 끝난다 — 두 플레이어가 파일이 끝날 때마다 값을 새로 읽으므로
    /// 열린 창에 전파할 이벤트가 없다(<see cref="PlaybackSettings"/> 주석). 레지스트리·워커와
    /// 무관하므로 위 파일 연결 토글들의 busy·<see cref="_suppressToggle"/> 축은 쓰지 않는다.
    /// 값이 <b>루프 모드가 '없음'일 때만</b> 효력이 있다는 사실은 아래 설명 줄이 고지한다.
    /// 카드 문법은 A197/A220(스위치 왼쪽 + 제목 TextBlock, 내장 On/Off 문구 제거) 그대로다.
    /// A306(v0.290.0): 둘째 항목 "Keep the display awake while a video plays"가 그 아래 선다.
    /// 카드 문법은 같지만 <b>알림 한 줄이 되살아난다</b>(Set → Save → NotifyKeepDisplayAwakeChanged)
    /// — 재생 도중에 끄면 그 자리에서 억제가 풀려야 하기 때문이다(A258 키처럼 EOF에 읽는 값이
    /// 아니다). 구독자는 열린 영상 뷰들이다(UiScale.Changed → MainWindow와 같은 축).
    /// 카드 사이 간격은 Root의 Spacing 12가 만든다 — 카드에 아래 여백을 따로 두지 않는다.
    /// </summary>
    private void BuildPlaybackSection()
    {
        AddHeader("Playback");

        var toggle = new ToggleSwitch
        {
            OnContent = string.Empty,
            OffContent = string.Empty,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
            IsOn = _settings.Get(PlaybackSettings.AutoNextKey, PlaybackSettings.AutoNextDefault),
        };
        toggle.Toggled += (_, _) =>
        {
            _settings.Set(PlaybackSettings.AutoNextKey, toggle.IsOn);
            _settings.Save(); // 즉시 저장 — 재생 설정 관용구(EQ·루프 모드와 같은 축)
        };

        var headerRow = new Grid { ColumnSpacing = 8 };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var title = new TextBlock
        {
            Text = "Auto-play next file",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(toggle, 0);
        Grid.SetColumn(title, 1);
        headerRow.Children.Add(toggle);
        headerRow.Children.Add(title);

        var cardBody = new StackPanel { Spacing = 6 };
        cardBody.Children.Add(headerRow);
        cardBody.Children.Add(new TextBlock
        {
            Text = "When a video or track ends, the next file in the same folder starts. "
                + "A loop mode, when one is set, plays on regardless of this switch.",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        Root.Children.Add(cardBody);

        // A306: 화면보호기·화면 꺼짐 억제 토글. 위 카드와 같은 문법이고, 저장 뒤 알림 한 줄만 더 붙는다.
        var awakeToggle = new ToggleSwitch
        {
            OnContent = string.Empty,
            OffContent = string.Empty,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
            IsOn = _settings.Get(PlaybackSettings.KeepDisplayAwakeKey,
                PlaybackSettings.KeepDisplayAwakeDefault),
        };
        awakeToggle.Toggled += (_, _) =>
        {
            _settings.Set(PlaybackSettings.KeepDisplayAwakeKey, awakeToggle.IsOn);
            _settings.Save(); // 즉시 저장 — 재생 설정 관용구(EQ·루프 모드와 같은 축)
            // 재생 중에 끄면 그 자리에서 억제가 풀려야 한다 — 열린 영상 뷰들이 이 알림을 듣는다.
            PlaybackSettings.NotifyKeepDisplayAwakeChanged();
        };

        var awakeHeaderRow = new Grid { ColumnSpacing = 8 };
        awakeHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        awakeHeaderRow.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var awakeTitle = new TextBlock
        {
            Text = "Keep the display awake while a video plays",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(awakeToggle, 0);
        Grid.SetColumn(awakeTitle, 1);
        awakeHeaderRow.Children.Add(awakeToggle);
        awakeHeaderRow.Children.Add(awakeTitle);

        var awakeCardBody = new StackPanel { Spacing = 6 };
        awakeCardBody.Children.Add(awakeHeaderRow);
        awakeCardBody.Children.Add(new TextBlock
        {
            Text = "The screen saver and display timeout stay off while a video is actually "
                + "playing, and come back as soon as it is paused, stopped or closed. "
                + "Audio playback is not affected.",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        Root.Children.Add(awakeCardBody);
    }

    /// <summary>
    /// A36(v0.109.0): 연결 섹션 아래 "Open settings.json" 버튼 + 경로·주의 안내.
    /// 저장 위치·포맷은 현행 %AppData%\KOTU\settings.json 그대로고(부록 B 37번 확정)
    /// 표기만 실제 파일명에 맞춘다 — 경로 문자열은 하드코딩하지 않고 ISettingsService.FilePath에서 읽는다.
    /// 여는 방식은 <b>새 인스턴스</b>(WindowManager.OpenFileInNewWindow) — 보고 있던 설정 화면을 잃지 않게.
    /// .json은 어느 모듈의 SupportedExtensions에도 없어서 App의 라우팅 재정의(.json → document)가
    /// 이 파일을 문서 모듈(KOTU-doc) 에디터로 보낸다.
    /// 저장 후 자동 재로드는 넣지 않는다(사용자 확정) — 재시작 반영이며 아래 안내 줄이 그 고지다.
    /// </summary>
    private void BuildSettingsFileSection()
    {
        var openButton = new Button
        {
            Content = "Open settings.json",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Root.Children.Add(openButton);

        // A162(v0.171.0): "직접 편집하면 설정이 깨질 수 있다"는 경고는 가이드 "The settings file" 절로
        // 옮겼다. 경로는 화면에 남긴다 — 복사해 가는 것이 이 줄의 본래 용도다.
        Root.Children.Add(new TextBlock
        {
            Text = $"{_settings.FilePath} - changes apply after restart.",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true, // 경로를 그대로 복사해 갈 수 있게
        });
        // A182: 직접 편집 경고를 다시 앱 안에서 펼쳐 본다(원문 = b80c437^ 그대로).
        Root.Children.Add(LearnMore("Editing this file directly can break your settings."));

        // 실패 사유 전용 줄(성공하면 보이지 않는다) — 공용 _status는 연결 토글 결과가 쓴다.
        var status = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        Root.Children.Add(status);

        openButton.Click += (_, _) =>
        {
            var path = _settings.FilePath;
            try
            {
                // 설정을 한 번도 바꾸지 않은 프로필에는 파일이 아직 없다 — 현재 값을 먼저 디스크로 내린다.
                if (!File.Exists(path)) _settings.Save();
                if (!File.Exists(path))
                {
                    status.Text = "Could not create the settings file.";
                    status.Visibility = Visibility.Visible;
                    return;
                }

                status.Visibility = Visibility.Collapsed;
                App.Services.GetRequiredService<WindowManager>().OpenFileInNewWindow(path);
            }
            catch (Exception ex)
            {
                status.Text = "Could not open the settings file: " + ex.Message;
                status.Visibility = Visibility.Visible;
            }
        };
    }

    /// <summary>
    /// Updates 섹션(A95, v0.117.0 — 확인 정책은 A114, v0.136.0). 구성은 위에서부터
    /// <b>현재 버전 · 최신 버전 · 마지막 확인 시각 · 다음 확인까지 남은 시간 · [Update to vX] · 안내 문구</b>
    /// (남은 시간 줄은 A167, v0.171.0에서 추가 — 확인 중에는 진행을 말하고, 예정 시각이 없으면 접힌다).
    /// 확인은 <b>이 화면이 붙을 때(설정 진입) 1회</b> · 머무는 동안 2분 주기 타이머 둘 다 돌지만
    /// 새 버전 알림은 여기 표시가 전부다 — <b>토스트·팝업은 금지</b>(A114 알림 방식 b).
    /// A125(v0.148.0): 수동 확인 버튼을 없앴다 — 이 화면은 이제 <b>확인을 시키는 손잡이가 없고</b>
    /// 진입과 체류가 곧 확인이다(코디네이터의 <c>CheckNowAsync</c>는 위 두 경로가 계속 쓴다).
    /// A206(v0.215.0): 주기 확인은 이 화면을 <b>떠나면 멈춘다</b> — 확인 결과를 보여 주는 자리가
    /// 여기뿐이라, 나가 있는 동안의 확인은 아무도 보지 않는다. 그래서 이 화면을 나가면
    /// 다음 확인 예정(카운트다운)도 사라진다.
    /// (v0.17.0 → v0.105.0 → v0.117.0 → v0.136.0 → v0.148.0 → v0.215.0으로 여섯 번 뒤집힌 정책이다.
    /// 상세는 UpdateCoordinator 주석).
    /// 실제 확인은 전역 <see cref="UpdateCoordinator"/>가 소유하고 여기서는 그 상태를 <b>표시만</b> 한다 —
    /// 다른 창에서 확인해도 이 화면이 따라 갱신된다.
    /// 업데이트 불가 빌드에서는 표시를 숨기지 않고 비활성으로 남긴다(사용자 확정).
    /// </summary>
    private void BuildUpdatesSection(string currentVersion)
    {
        var available = UpdateCoordinator.IsAvailable;

        // TextBlock은 Control이 아니라 IsEnabled가 없다 — 업데이트 불가 빌드의 '비활성' 표현은
        // 흐리게(Opacity)로 대신한다. (v0.108.1) A125(v0.148.0)로 IsEnabled를 잠글 버튼이 사라졌다 —
        // 적용 버튼은 불가 빌드에서 애초에 나타나지 않으므로(PendingUpdate가 없다) 흐리게 +
        // 아래 안내 문구가 '불가'를 알리는 전부다.
        var dim = available ? 0.7 : 0.4;
        var latest = new TextBlock { FontSize = 12, Opacity = dim };
        var lastChecked = new TextBlock { FontSize = 12, Opacity = dim };
        // A167(v0.171.0): 다음 자동 확인까지 남은 시간. 마지막 확인 줄 바로 아래, 같은 규격
        // (FontSize 12 · 같은 Opacity)이다. 보여 줄 값이 없을 때는 줄 자체를 접는다.
        var nextCheck = new TextBlock { FontSize = 12, Opacity = dim, Visibility = Visibility.Collapsed };
        var status = new TextBlock { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        // A125(v0.148.0): 버튼이 적용 하나만 남아 가로 StackPanel(구 buttonRow)을 없애고 Root에 직접 넣는다 —
        // 숨겨진 버튼만 담은 빈 줄이 남으면 Root의 Spacing이 위아래로 두 번 붙어 헛간격이 생긴다.
        // 세로 StackPanel의 자식은 기본이 Stretch라 폭이 화면 끝까지 늘어나므로 Left로 고정한다
        // ("Open settings.json" 버튼과 같은 처리).
        var updateButton = new Button
        {
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        Root.Children.Add(new TextBlock { Text = $"Current version: v{currentVersion}", Opacity = 0.8 });
        Root.Children.Add(latest);
        Root.Children.Add(lastChecked);
        Root.Children.Add(nextCheck); // A167 — 마지막 확인 줄 바로 아래
        Root.Children.Add(updateButton);
        Root.Children.Add(status); // 안내 문구는 적용 버튼 '밑'이다(A95 — 순서 유지).

        // 다운로드·설치 중에는 그 진행 문구를 전역 상태 갱신이 덮어쓰지 않게 한다.
        var installing = false;

        // A167(v0.171.0): 남은 시간 한 줄. 1초 타이머와 Render 양쪽에서 부른다.
        //  · 확인 중이면 남은 시간 대신 진행을 말한다.
        //  · 예정 시각이 없거나(불가 빌드 = 타이머 없음) 이미 지났으면 줄을 접는다 —
        //    "0:00"을 오래 띄우지 않는다(틱과 틱 사이의 짧은 공백은 아무 말도 하지 않는 게 낫다).
        void RenderCountdown()
        {
            if (UpdateCoordinator.IsChecking)
            {
                nextCheck.Text = "Checking for updates...";
                nextCheck.Visibility = Visibility.Visible;
                return;
            }

            if (UpdateCoordinator.NextCheckAt is not { } at)
            {
                nextCheck.Visibility = Visibility.Collapsed;
                return;
            }

            var left = at - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                nextCheck.Visibility = Visibility.Collapsed;
                return;
            }

            // 올림으로 세어 1초 미만이 "0:00"이 되지 않게 한다(0:01에서 바로 접힌다).
            var seconds = (int)Math.Ceiling(left.TotalSeconds);
            nextCheck.Text = $"Next check in {seconds / 60}:{seconds % 60:00}";
            nextCheck.Visibility = Visibility.Visible;
        }

        void Render()
        {
            lastChecked.Text = UpdateCoordinator.DescribeLastCheck();
            RenderCountdown();

            // 한 번 찾은 업데이트는 뒤이은 확인이 실패해도 적용 버튼을 유지한다.
            if (UpdateCoordinator.PendingUpdate is { } pending)
            {
                latest.Text = $"Latest version: v{pending.TargetFullRelease.Version}";
                updateButton.Content = $"Update to v{pending.TargetFullRelease.Version}";
                updateButton.Visibility = Visibility.Visible;
            }
            else if (UpdateCoordinator.LastCheckedAt is null)
            {
                latest.Text = "Latest version: not checked yet";
            }
            else
            {
                // 확인은 했는데 새 버전이 없다 = 지금 것이 최신. 실패했으면 최신이 뭔지 알 수 없다.
                latest.Text = UpdateCoordinator.LastCheckError.Length > 0
                    ? "Latest version: unknown"
                    : $"Latest version: v{currentVersion}";
            }

            if (installing) return;

            if (!available)
            {
                status.Text = "In-app updates are unavailable in this build. "
                            + "Install with Setup.exe from Releases to enable them.";
            }
            else if (UpdateCoordinator.IsChecking)
            {
                // A167(v0.171.0): "Checking for updates..."는 이제 위 카운트다운 줄이 말한다 —
                // 같은 문장을 두 줄에 겹쳐 띄우지 않으려고 여기서는 비운다(전달되는 정보는 그대로).
                status.Text = string.Empty;
            }
            else if (UpdateCoordinator.LastCheckError.Length > 0)
            {
                status.Text = "Update check failed: " + UpdateCoordinator.LastCheckError;
            }
            else if (UpdateCoordinator.PendingUpdate is not null)
            {
                status.Text = string.Empty; // 새 버전은 위 줄과 적용 버튼이 이미 말한다.
            }
            else
            {
                // 아직 한 번도 확인하지 않았으면 아무 말도 하지 않는다(A95).
                status.Text = UpdateCoordinator.LastCheckedAt is null
                    ? string.Empty
                    : "You are on the latest version.";
            }
        }

        updateButton.Click += async (_, _) =>
        {
            if (UpdateCoordinator.PendingUpdate is not { } info) return;
            installing = true;
            await DownloadAndInstallAsync(status, updateButton, info);
            installing = false;
        };

        // A167(v0.171.0): 카운트다운 전용 1초 타이머. Changed는 2분에 한 번(확인 시작·종료)뿐이라
        // 초 단위 표시를 그것만으로는 못 만든다.
        // 수명 규칙 — 이 뷰 하나당 타이머 하나이고, 화면에 붙어 있는 동안에만 돈다:
        //  · 생성: 필드 초기화 한 곳뿐(_countdownTimer). 여기서는 Tick만 매단다 —
        //    이 메서드 자체가 생성자 → Build()에서 딱 한 번 불리므로 Tick 구독도 한 번뿐이다.
        //  · 시작: Loaded. 반복 로드돼도 같은 인스턴스를 다시 Start할 뿐 새로 만들지 않는다.
        //  · 정지: Unloaded. 설정 화면을 닫으면 멈추고, 멈춘 DispatcherTimer는 디스패처가 붙들지
        //    않으므로 뷰와 함께 수거된다. 설정을 열 때마다 새 SettingsView가 만들어지지만
        //    (MainWindow.ShowSettingsAsync) 앞 뷰의 타이머는 그 뷰의 Unloaded에서 이미 멈춰 있다.
        _countdownTimer.Tick += (_, _) => RenderCountdown();

        UpdateCoordinator.Changed += Render;
        Loaded += (_, _) =>
        {
            // A206(v0.215.0): 자동 확인은 이 화면이 떠 있는 동안에만 돈다. 열림을 알리는 자리는
            // 생성자가 아니라 여기다 — Unloaded와 짝이 맞아야 카운트가 새지 않기 때문이고,
            // 진입 즉시 1회 확인도 같은 훅 안에 있어야 "열림 = 확인 시작"이 한 곳에서 읽힌다.
            HoldUpdateWatch();
            RenderCountdown();          // 화면에 붙는 즉시 한 번 — 첫 1초를 빈 줄로 두지 않는다
            _countdownTimer.Start();
        };
        Unloaded += (_, _) =>
        {
            UpdateCoordinator.Changed -= Render;
            _countdownTimer.Stop();
            // 구독을 끊은 뒤에 놓는다 — 코디네이터가 정지를 알릴 때 이미 내려간 이 뷰가 다시
            // 그려지지 않게(표시할 화면이 없다는 게 정지의 전제다).
            ReleaseUpdateWatch();
        };
        Render();
    }

    /// <summary>
    /// A206(v0.215.0): 설정 화면 열림을 <see cref="UpdateCoordinator"/>에 알리고(전역 열림 수가
    /// 0→1이면 2분 주기 자동 확인 타이머가 선다) 진입 즉시 1회 확인을 쏜다.
    /// 진입 1회 확인이 A114 이래 이 화면의 몫이었던 것은 그대로고, 부르는 자리만
    /// 생성자(<see cref="BuildUpdatesSection"/> 끝)에서 Loaded로 옮겼다 — 생성자에는 짝이 될
    /// 닫힘 신호가 없어 카운트가 샜을 것이기 때문이다.
    /// 이미 쥐고 있으면 아무것도 하지 않는다: 뷰가 떼였다 다시 붙어도 카운트는 +1을 넘지 않고
    /// 확인 요청도 두 번 나가지 않는다.
    /// 주기 타이머와 이 확인이 겹쳐도 <c>CheckNowAsync</c>가 진행 중이면 되돌아간다(요청 1개).
    /// </summary>
    private void HoldUpdateWatch()
    {
        if (_updateWatchHeld) return;
        _updateWatchHeld = true;
        UpdateCoordinator.NotifySettingsOpened();
        // 발사 후 망각 — 예외는 코디네이터가 삼키고 결과는 Changed → Render로 돌아온다.
        _ = UpdateCoordinator.CheckNowAsync();
    }

    /// <summary>
    /// A206(v0.215.0): <see cref="HoldUpdateWatch"/>의 짝. 전역 열림 수가 1→0이면 자동 확인
    /// 타이머가 멈추고 카운트다운 줄이 접힌다(NextCheckAt = null).
    /// Unloaded가 정상 경로이고, 설정 화면을 띄운 채 창을 닫아 Unloaded가 오지 않는 경우를 위해
    /// <see cref="MainWindow"/>의 Closed도 이 메서드를 부른다 — 둘이 겹쳐도 위 bool이 -1을 한 번으로 막는다.
    /// </summary>
    internal void ReleaseUpdateWatch()
    {
        if (!_updateWatchHeld) return;
        _updateWatchHeld = false;
        UpdateCoordinator.NotifySettingsClosed();
    }

    /// <summary>다운로드 → 사람 확인(Install and restart / Later) 대기 → 적용. 자동 재시작 없음.</summary>
    private async Task DownloadAndInstallAsync(TextBlock status, Button updateButton, Velopack.UpdateInfo info)
    {
        var version = info.TargetFullRelease.Version;
        updateButton.IsEnabled = false;
        try
        {
            await UpdateService.DownloadAsync(info, percent =>
                DispatcherQueue.TryEnqueue(() => status.Text = $"Downloading v{version}... {percent}%"));
            status.Text = $"v{version} downloaded.";

            var confirm = new ContentDialog
            {
                Title = "Ready to install",
                Content = $"KOTU will close and restart to finish installing v{version}. Install now?",
                PrimaryButtonText = "Install and restart",
                CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            {
                status.Text = "Applying and restarting...";
                UpdateService.ApplyAndRestart(info);
            }
            else
            {
                status.Text = $"v{version} downloaded - click the button again to install when ready.";
                updateButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            status.Text = "Update failed: " + ex.Message;
            updateButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 축약한 설명 줄 바로 아래 붙는 "Learn more" 접기/펼치기 (A182 — A162의 부분 반전).
    /// A162(v0.171.0)에서는 같은 자리가 사용자 가이드로 나가는 <see cref="HyperlinkButton"/>
    /// 링크였지만, 지금은 <b>앱 밖으로 나가지 않고</b> 그 자리 아래에 상세 문장을 펼친다
    /// (사용자 지시 2026-08-19: "축약 전에 앱 안에서 모든 문장이 보이던 것처럼").
    /// <para>
    /// 형태는 저장소에 이미 있는 것만 조합했다 — WinUI <c>Expander</c> 사용 선례가 0건이라
    /// 쓰지 않고, 종전 Learn more 버튼(About 줄 링크와 같은 규격의 HyperlinkButton)에
    /// Click을 매달아 상세 <see cref="TextBlock"/>의 Visibility만 토글한다. 라벨은
    /// "Learn more"와 "Show less"를 오간다.
    /// </para>
    /// <para>
    /// 버튼과 상세를 <b>StackPanel 하나로 묶어</b> Root에 넣는 이유: Root의 Spacing 12를 상쇄하는
    /// 음수 여백(-8)을 종전처럼 <b>한 군데</b>만 두기 위함이다(A25 defaults 줄과 같은 처리).
    /// 요소 둘을 Root에 따로 넣으면 그 12가 펼침 문장 위에도 붙어 설명에서 떨어져 보이고,
    /// 접었을 때는 빈 자리가 남는다. 묶음 안쪽 간격은 상세 쪽 위 여백 4로만 준다.
    /// </para>
    /// </summary>
    /// <param name="detail">
    /// 축약 직전 원문(커밋 b80c437^)에서 <b>위 축약 줄이 이미 말한 부분을 뺀 나머지</b>.
    /// 축약 줄 + 이 문장 = 원문 정보량이다. 문구는 영어만 쓴다.
    /// </param>
    private static StackPanel LearnMore(string detail)
    {
        var body = new TextBlock
        {
            Text = detail,
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed, // 접힌 상태로 시작 — 축약의 목적이 첫 화면을 짧게 두는 것
        };

        var toggle = new HyperlinkButton
        {
            Content = "Learn more",
            FontSize = 12,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        toggle.Click += (_, _) =>
        {
            var expanding = body.Visibility == Visibility.Collapsed;
            body.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = expanding ? "Show less" : "Learn more";
        };

        var group = new StackPanel { Margin = new Thickness(0, -8, 0, 0) };
        group.Children.Add(toggle);
        group.Children.Add(body);
        return group;
    }

    // A183의 구획 카드(NewCard — Border·Padding 12)는 A220(2026-08-24)에서 해체됐다:
    // 안쪽 Padding 때문에 스위치 좌변이 절의 다른 토글들과 어긋나 보인다는 사용자 보고.
    // 구획은 이제 그룹 StackPanel의 아래 여백 8(+Root Spacing 12)로 만든다 — 되살릴 일이
    // 생기면 git 이력의 v0.194.0(A183) NewCard를 참조할 것.

    /// <summary>섹션 머리글 추가.</summary>
    private void AddHeader(string text)
    {
        Root.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
        });
    }

    /// <summary>
    /// 모듈 토글 한 번(= 그 모듈의 전 확장자 일괄 처리)의 결과
    /// (A326 — A292의 <c>ExtensionOutcome</c>을 다시 모듈 단위로 되돌린 것. 계보는 A77의
    /// <c>AssociationOutcome</c>). 워커에서 만들어 UI 스레드로 통째로 건너온다.
    /// </summary>
    /// <param name="Ok">확장자 전부가 실패 없이 처리됐는지. false면 <paramref name="Error"/>를 표시한다
    /// (표시 값은 되돌리지 않고 <paramref name="AllRegistered"/>로 다시 맞춘다 — 부분 실패가 가능하다).</param>
    /// <param name="Error">첫 실패 사유(전부 성공이면 null).</param>
    /// <param name="DefaultsSet">기본 앱 지정(A38)까지 성공한 확장자 수 — 실패해도 토글은 되돌리지 않는다(A77 확정).</param>
    /// <param name="AllRegistered">작업 후 레지스트리를 다시 읽어 판정한 "이 모듈의 확장자가 전부 등록됨".
    /// 모듈 토글의 표시 규칙(사양 3 — 부분 등록은 Off)이 이 값 하나로 정해진다.</param>
    /// <param name="Defaults">작업 후 다시 센 모듈의 "기본 앱인 확장자" 개수. 세지 못했으면 -1(화면 숫자 유지).</param>
    private readonly record struct ModuleOutcome(
        bool Ok, string? Error, int DefaultsSet, bool AllRegistered, int Defaults);

    /// <summary>
    /// 워커 스레드 전용 (A326) — 모듈 하나의 <b>전 확장자</b> 등록/해제 + (켤 때만) 기본 앱 지정 +
    /// 등록 재판정 + 기본 앱 개수 조회를 한 작업으로 묶어 처리한다. UI 요소는 일절 건드리지 않고
    /// 진행은 <paramref name="report"/> 콜백(n/m)으로만 알린다 — 호출 측이 디스패처로 마샬링한다.
    /// 레지스트리 층은 무접촉이다: A292가 만든 확장자 단위 API를 확장자 수만큼 직렬로 부를 뿐이다.
    /// 확장자 하나가 실패해도 <b>멈추지 않고</b> 나머지를 마저 처리한다 — 그래야 화면의 "n/m" 줄과
    /// 토글 표시가 레지스트리의 실제 상태와 어긋나지 않는다(첫 실패 사유만 남긴다).
    /// </summary>
    private static ModuleOutcome ApplyModuleAssociation(IModule module, bool turnOn, Action<int, int> report)
    {
        var total = module.SupportedExtensions.Count;
        var done = 0;
        string? error = null;
        var defaultsSet = 0;

        foreach (var ext in module.SupportedExtensions)
        {
            try
            {
                if (turnOn)
                {
                    ExplorerIntegration.RegisterExtensionAssociation(module, ext);
                    // A38: 켤 때만 기본 앱까지 지정 시도. 지정 실패는 등록 성공을 무르지 않는다(A77 확정) —
                    // 보호 확장자(A166)는 아예 시도하지 않고 false를 돌려준다.
                    try
                    {
                        if (ExplorerIntegration.SetAsDefaultForExtension(module, ext)) defaultsSet++;
                    }
                    catch
                    {
                        // 기본 앱 지정 실패는 안내 문구로만 다룬다("Set default..." 진입로).
                    }
                }
                else
                {
                    ExplorerIntegration.UnregisterExtensionAssociation(module, ext);
                }
            }
            catch (Exception ex)
            {
                error ??= ex.Message; // 첫 사유만 — 뒤 확장자는 계속 처리한다
            }
            report(++done, total);
        }

        return new ModuleOutcome(
            error is null, error, defaultsSet, AllExtensionsRegistered(module), SafeCountDefaults(module));
    }

    /// <summary>
    /// A326: "이 모듈의 확장자가 <b>전부</b> 등록됐는가" — 모듈 토글의 표시 판정(사양 4·마스터의
    /// '전부 켜짐일 때만 On'을 한 층 아래에 적용). 정본은 레지스트리다(설정 파일 키 없음).
    /// 조회 실패는 <see cref="Safe"/>와 같은 규칙으로 '꺼짐'으로 본다. 워커에서만 호출한다.
    /// </summary>
    private static bool AllExtensionsRegistered(IModule module)
    {
        try
        {
            return module.SupportedExtensions.Count > 0
                && module.SupportedExtensions.All(
                    ext => ExplorerIntegration.IsExtensionAssociationRegistered(module, ext));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>기본 앱인 확장자 개수 — 조회 실패는 0으로 본다(워커에서만 호출).</summary>
    private static int SafeCountDefaults(IModule module)
    {
        try { return ExplorerIntegration.CountDefaults(module); }
        catch { return 0; }
    }

    /// <summary>
    /// 워커 스레드 전용 (A195) — 우클릭 메뉴 등록/해제. 스위치 하나가 "여기에 풀기"(압축 파일)와
    /// "압축하기"(모든 파일) 둘을 함께 다루므로 한 작업으로 묶는다(종전 <c>Apply</c>의 두 델리게이트).
    /// UI 요소는 일절 건드리지 않고 실패 사유만 돌려준다 — 성공이면 null.
    /// 앞쪽 등록이 성공한 뒤 뒤쪽이 실패하면 앞쪽은 남는데, 이는 종전 UI 스레드 판과 같은 동작이다.
    /// </summary>
    private static string? ApplyMenuRegistration(
        IReadOnlyList<string> archiveExtensions, string brandLabel, bool turnOn)
    {
        try
        {
            if (turnOn)
            {
                ExplorerIntegration.RegisterExtractHereMenu(archiveExtensions, brandLabel);
                ExplorerIntegration.RegisterCompressMenu(brandLabel);
            }
            else
            {
                ExplorerIntegration.UnregisterExtractHereMenu(archiveExtensions);
                ExplorerIntegration.UnregisterCompressMenu();
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>레지스트리 조회 실패를 '꺼짐'으로 보는 폴백 (워커에서만 호출 — A195).</summary>
    private static bool Safe(Func<bool> check)
    {
        try { return check(); }
        catch { return false; }
    }

    /// <summary>'연결 프로그램' 대화상자 소유자용 창 핸들 — Window 객체 없이 XamlRoot 경유 (A25).</summary>
    private nint GetHwnd()
    {
        var environment = XamlRoot?.ContentIslandEnvironment
            ?? throw new InvalidOperationException("Cannot determine the window handle.");
        return Microsoft.UI.Win32Interop.GetWindowFromWindowId(environment.AppWindowId);
    }
}
