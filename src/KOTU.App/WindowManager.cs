using Microsoft.UI.Xaml;
using KOTU.Core.Cli;
using KOTU.Core.Routing;
using KOTU.Core.Settings;

namespace KOTU.App;

/// <summary>
/// 멀티 윈도우 관리자 (v0.14.0). 단일 프로세스 안에서 창을 여러 개 관리한다
/// — 사진을 열어둔 채 동영상도 보는 시나리오의 중심.
///
/// 창 선택 규칙(파일 열기 시):
///  1) 같은 모듈을 보여주는 창이 있으면 재사용 (이미지 ←/→ 탐색 컨텍스트 유지)
///     — 단 "항상 새 창" 설정(A24)이면 이 단계를 건너뛴다
///  2) 아직 아무것도 안 연 빈 셸 창이 있으면 재사용 (시작 직후 등)
///  3) 없으면 새 창
/// 명시적 새 창 수단(Shift+N(A84 — 기존 Ctrl+N)·Shift+더블클릭·우클릭 메뉴, A24)은 규칙과 무관하게 항상 새 창.
/// 마지막 창이 닫히면 앱을 종료한다 — 트레이로 숨긴 창(A69)도 닫히기 전까지는 열린 창이다
/// (목록 제거는 Closed에서만 일어나므로, 마지막 창까지 숨겨도 프로세스는 유지된다).
/// 모든 메서드는 UI 스레드에서 호출해야 한다.
/// </summary>
public sealed class WindowManager
{
    /// <summary>창 재사용 규칙 설정 키(A24): true = 파일을 열 때마다 새 창(기본 false = 재사용).</summary>
    public const string AlwaysNewWindowKey = "window.alwaysNewWindow";

    private readonly FileTypeRouter _router;
    private readonly ISettingsService _settings;

    /// <summary>열린 창 목록. 끝쪽이 가장 최근에 활성화된 창(MRU).</summary>
    private readonly List<MainWindow> _windows = [];

    /// <summary>
    /// 창 생성 순서 목록 (A2, v0.58.0) — 인스턴스 번호(1~9)의 기준.
    /// MRU(_windows)는 활성화마다 순서가 바뀌므로 번호용으로는 따로 유지한다.
    /// 창이 닫히면 제거되어 뒷번호가 자연히 당겨진다.
    /// </summary>
    private readonly List<MainWindow> _ordered = [];

    public WindowManager(FileTypeRouter router, ISettingsService settings)
    {
        _router = router;
        _settings = settings;
    }

    /// <summary>가장 최근 활성화된 창. 업데이트 다이얼로그 등 공용 UI의 호스트로 쓴다.</summary>
    public MainWindow? ActiveWindow => _windows.Count > 0 ? _windows[^1] : null;

    /// <summary>
    /// 현재 열린 창 수. MainWindow 생성자(계단식 오프셋, A55)에서 부르면 새 창은 아직
    /// 등록 전이므로 "기존 창 수 = 새 창의 인스턴스 번호 - 1"이 된다.
    /// </summary>
    public int OpenWindowCount => _windows.Count;

    /// <summary>파일 없이 시작할 때의 첫 창 — 기본 화면은 Info(하드웨어) 모듈(사용자 지정).</summary>
    public MainWindow OpenInitialWindow()
    {
        var window = Create();
        window.ShowDefaultModule();
        // A81: 파일 인자 없이 모듈로 시작한 창은 좌·우 불투명 밀어내기가 기본(부록 B 30번).
        // 기본 화면(H/W)에서는 오버레이가 숨겨진 채 상태만 남고, 파일 모듈로 전환하는 순간
        // 도크로 나타난다. 주입은 이 진입 1회뿐 — 이후에는 사용자가 바꾼 상태를 존중한다.
        window.SetDockedState(listDocked: true, infoDocked: true);
        window.Activate();
        return window;
    }

    /// <summary>실행 요청(첫 실행·재전달 공통 진입점)을 알맞은 창으로 보낸다.</summary>
    public void Dispatch(LaunchRequest request)
    {
        if (request.FilePath is not { } file || !File.Exists(file))
        {
            // 파일 없는 실행: 창이 없으면 하나 열고, 있으면 최근 창만 앞으로
            if (_windows.Count == 0) OpenInitialWindow();
            else ActiveWindow!.BringToFront();
            return;
        }

        if (request.Verb == LaunchVerb.Open)
        {
            OpenFile(file);
        }
        else
        {
            // 탐색기 우클릭 동사(여기에 풀기/압축)는 압축 모듈 담당
            var target = FindReusable("archive");
            target.OpenVerb(request);
            target.BringToFront();
        }
    }

    /// <summary>파일을 담당 모듈 창으로 라우팅해 연다. 창 선택은 재사용 규칙(A24)을 따른다.</summary>
    public void OpenFile(string path)
    {
        var target = FindReusable(_router.Resolve(path)?.Id);
        target.OpenFile(path);
        target.BringToFront();
    }

    /// <summary>
    /// 명시적 "새 창으로 열기"(A24: Shift+더블클릭·우클릭 메뉴). 재사용 규칙과 무관하게
    /// 항상 새 창을 만든다 — 빈 셸 재사용도 안 한다(요청한 창을 그대로 두는 게 의도).
    /// </summary>
    public void OpenFileInNewWindow(string path)
    {
        var window = Create();
        window.OpenFile(path);
        window.Activate();
    }

    /// <summary>
    /// 새 창 열기(A24: Shift+N(A84 — 기존 Ctrl+N)·시작 메뉴). 현재 모듈의 빈 인스턴스로 시작한다(사용자 확정) —
    /// 모듈이 없는 창(설정·시작 직후)에서 부르면 기본 화면(하드웨어)으로.
    /// </summary>
    public void OpenNewWindow(string? moduleId)
    {
        var window = Create();
        if (moduleId is not null) window.OpenModuleById(moduleId);
        else window.ShowDefaultModule();
        // A81: 파일 없이 모듈로 여는 새 창도 "모듈 실행" 진입 — 양쪽 불투명 도크가 기본.
        // 파일로 여는 새 창(OpenFileInNewWindow·FindReusable)은 기본이 닫힘이라 주입 없음.
        window.SetDockedState(listDocked: true, infoDocked: true);
        window.Activate();
    }

    /// <summary>
    /// 하드웨어 모니터 열기/활성화 (A18 센서 트레이 좌클릭·메뉴). 하드웨어를 보여주는 창이
    /// 있으면 그 창을, 없으면 빈 셸 또는 새 창에 하드웨어 모듈을 띄운다.
    /// </summary>
    public void ShowHardware()
    {
        var target = FindReusable("hardware");
        target.OpenModuleById("hardware"); // 이미 하드웨어면 no-op
        target.BringToFront();
    }

    /// <summary>
    /// 설정 화면 열기/활성화 (A26, v0.105.0 — 업데이트 토스트 클릭). 최근 창을 재사용하고,
    /// 창이 하나도 없으면(모두 닫히는 경합 등) 새로 만든다.
    /// </summary>
    public void ShowSettings(bool scrollToUpdates = false)
    {
        var target = ActiveWindow ?? Create();
        target.ShowSettings(scrollToUpdates);
        target.BringToFront();
    }

    /// <summary>모든 창 닫기 = 앱 종료 (트레이 메뉴 'Exit KOTU').</summary>
    public void CloseAll()
    {
        foreach (var window in _windows.ToArray())
            window.Close();
    }

    private MainWindow FindReusable(string? moduleId)
    {
        // 1) 같은 모듈 창 (여러 개면 가장 최근 활성화된 것).
        //    "항상 새 창" 설정(A24)이면 건너뛴다 — 빈 셸 재사용(2)은 유지:
        //    방금 뜬 빈 창을 두고 또 창을 만드는 건 규칙의 의도가 아니다.
        if (moduleId is not null && !_settings.Get(AlwaysNewWindowKey, false))
        {
            for (var i = _windows.Count - 1; i >= 0; i--)
                if (_windows[i].CurrentModuleId == moduleId)
                    return _windows[i];
        }

        // 2) 빈 셸 창
        for (var i = _windows.Count - 1; i >= 0; i--)
            if (_windows[i].IsUntouched)
                return _windows[i];

        // 3) 새 창
        return Create();
    }

    private MainWindow Create()
    {
        var window = new MainWindow(this);

        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            _ordered.Remove(window);
            UpdateInstanceNumbers(); // 중간 창이 닫히면 번호 당겨오기 (A2)
            if (_windows.Count == 0)
            {
                // 센서 커널 드라이버 정리(A17) — 안 해도 프로세스는 내려가지만,
                // Close()가 드라이버 서비스를 해제해 재부팅 전까지 남는 걸 막는다.
                KOTU.Module.Hardware.SensorService.Shutdown();
                Application.Current.Exit();
            }
        };

        // MRU 유지: 활성화될 때마다 목록 끝으로
        window.Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated
                && _windows.Remove(window))
            {
                _windows.Add(window);
            }
        };

        _windows.Add(window);
        _ordered.Add(window);
        UpdateInstanceNumbers(); // 2번째 창이 뜨는 순간 1번 창에도 번호가 생긴다 (A2)
        return window;
    }

    /// <summary>
    /// 인스턴스 번호 갱신 (A2, v0.58.0 / A56, v0.87.0): 창이 2개 이상일 때만 생성 순서대로
    /// 번호를 매긴다. 창이 하나면 0(표시 없음).
    /// 10번째 이상도 실제 번호를 그대로 넘긴다 — 색상 배지는 9색뿐이라 창 쪽에서 숨기지만
    /// 제목표시줄 번호는 계속 유효해야 하기 때문(A56).
    /// 번호는 창·트레이 아이콘의 인스턴스 색 테두리·원형 번호 합성에도 쓰인다
    /// (A68 — 팔레트는 같은 9색, 10번째부터 1번 색부터 순환).
    /// </summary>
    private void UpdateInstanceNumbers()
    {
        for (var i = 0; i < _ordered.Count; i++)
            _ordered[i].SetInstanceNumber(_ordered.Count > 1 ? i + 1 : 0);
    }
}
