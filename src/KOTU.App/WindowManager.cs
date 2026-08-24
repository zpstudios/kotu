using Microsoft.UI.Xaml;
using KOTU.Core.Cli;
using KOTU.Core.Routing;

namespace KOTU.App;

/// <summary>
/// 멀티 윈도우 관리자 (v0.14.0). 단일 프로세스 안에서 창을 여러 개 관리한다
/// — 사진을 열어둔 채 동영상도 보는 시나리오의 중심.
///
/// 창 선택 규칙(파일 열기 시):
///  1) 같은 모듈을 보여주는 창이 있으면 재사용 (이미지 ←/→ 탐색 컨텍스트 유지)
///  2) 아직 아무것도 안 연 빈 셸 창이 있으면 재사용 (시작 직후 등)
///  3) 없으면 새 창
/// 명시적 새 창 수단(Shift+N(A84 — 기존 Ctrl+N)·Shift+더블클릭·우클릭 메뉴, A24)은 규칙과 무관하게 항상 새 창.
/// ※ A222(2026-08-24): A24의 "Always open files in a new instance" 설정 토글
/// (window.alwaysNewWindow — 1단계 건너뛰기 게이트)은 폐지 — 재사용 규칙만 남는다.
/// 마지막 창이 닫히면 앱을 종료한다 — 트레이로 숨긴 창(A69)도 닫히기 전까지는 열린 창이다
/// (목록 제거는 Closed에서만 일어나므로, 마지막 창까지 숨겨도 프로세스는 유지된다).
/// 모든 메서드는 UI 스레드에서 호출해야 한다.
/// </summary>
public sealed class WindowManager
{
    private readonly FileTypeRouter _router;

    /// <summary>열린 창 목록. 끝쪽이 가장 최근에 활성화된 창(MRU).</summary>
    private readonly List<MainWindow> _windows = [];

    /// <summary>
    /// 창 생성 순서 목록 (A2, v0.58.0) — 인스턴스 번호(1~9)의 기준.
    /// MRU(_windows)는 활성화마다 순서가 바뀌므로 번호용으로는 따로 유지한다.
    /// 창이 닫히면 제거되어 뒷번호가 자연히 당겨진다.
    /// </summary>
    private readonly List<MainWindow> _ordered = [];

    public WindowManager(FileTypeRouter router)
    {
        _router = router;
    }

    /// <summary>가장 최근 활성화된 창. 업데이트 다이얼로그 등 공용 UI의 호스트로 쓴다.</summary>
    public MainWindow? ActiveWindow => _windows.Count > 0 ? _windows[^1] : null;

    /// <summary>
    /// 현재 열린 창 수. MainWindow 생성자에서 부르면 새 창은 아직 등록 전이므로
    /// "기존 창 수 = 새 창의 인스턴스 번호 - 1"이 된다.
    /// ※ A55의 계단식 오프셋 용도는 A89(v0.114.0)에서 폐기됐다(오프셋 없이 그대로 승계).
    /// 값 자체는 다른 항목이 쓸 수 있어 그대로 둔다.
    /// </summary>
    public int OpenWindowCount => _windows.Count;

    /// <summary>파일 없이 시작할 때의 첫 창 — 기본 화면은 Info(하드웨어) 모듈(사용자 지정).</summary>
    public MainWindow OpenInitialWindow()
    {
        var window = Create();
        window.ShowDefaultModule();
        // A81: 파일 인자 없이 모듈로 시작한 창은 좌·우 불투명 밀어내기가 기본(부록 B 30번).
        // 기본 화면(H/W)에서는 오버레이가 숨겨진 채 상태만 남고, 파일 모듈로 전환하는 순간
        // 도크로 나타난다. 창 생성 시 주입은 이 진입 1회뿐 — 파일 열기 뒤에는 사용자가 바꾼 상태를
        // 존중한다. ※ A109(v0.136.0)부터 **모듈 전환**에서는 셸(ShowModule)이 같은 기본을 다시 준다.
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
        // ※ A109(v0.136.0) 이후에는 위 두 경로(OpenModuleById·ShowDefaultModule)가 모듈 실행이라
        // 같은 기본을 스스로 준다 — 이 줄은 결과가 겹치지만(같은 값 재대입, 부작용 없음)
        // "창 생성 진입의 기본 상태는 여기서 정한다"는 A81 계약을 명시로 남겨 두는 쪽을 택했다.
        window.SetDockedState(listDocked: true, infoDocked: true);
        window.Activate();
    }

    // 구 ShowHardware(하드웨어 모니터 열기/활성화)는 A101(v0.137.0)에서 제거 — 유일한 호출자가
    // A18 센서 트레이(좌클릭·메뉴)였고 그 아이콘 자체가 폐지됐다. 창별 아이콘 클릭은 TrayIcon의
    // 기존 동작(그 창 활성화) 그대로다.

    /// <summary>모든 창 닫기 = 앱 종료 (트레이 메뉴 'Exit KOTU').</summary>
    public void CloseAll()
    {
        foreach (var window in _windows.ToArray())
            window.Close();
    }

    // ---------- 관리자 재시작 창 세트 복원 (A124) ----------

    /// <summary>
    /// 관리자 재시작(A124) 직전: 열린 창 세트를 세션 파일로 기록한다. 대상은 모듈 ID가 있는
    /// 창뿐이다(설정 화면·빈 셸·미지원 안내 창은 CurrentModuleId가 null → 자연 제외).
    /// 순서는 생성 순서(_ordered) — 복원도 같은 순서로 열어 인스턴스 번호(A2)·트레이 슬롯
    /// (A100)·AUMID(A105)가 재기동 전과 같은 순서로 다시 배정된다(전부 창 생성 경로의 자동
    /// 배정 — 수동 개입 없음). 창 하나의 캡처 실패는 그 창만 빼고 계속하고, 캡처된 창이
    /// 하나도 없으면 파일을 쓰지 않는다(승격 프로세스는 종전대로 기본 1창 시작).
    /// </summary>
    public void WriteRestartSession()
    {
        var snapshots = new List<Integration.RestartSessionFile.WindowSnapshot>();
        foreach (var window in _ordered)
        {
            try
            {
                if (window.CaptureSessionSnapshot() is { } snapshot) snapshots.Add(snapshot);
            }
            catch
            {
                // 이 창만 건너뛴다 — 스냅샷 실패가 재시작을 막으면 안 된다(A124).
            }
        }
        if (snapshots.Count > 0) Integration.RestartSessionFile.Write(snapshots);
    }

    /// <summary>
    /// 앱 시작 시(A124): 유효한 재시작 세션 파일이 있으면 기본 1창 대신 창 세트를 재현한다.
    /// true = 1창 이상 복원(호출자는 기본 시작 창을 만들지 않는다). 파일은 읽는 즉시
    /// 삭제되고(정리 책임 = 읽는 쪽), 파싱 실패·기한(2분) 초과·전 창 복원 실패면 false —
    /// 조용히 기본 시작으로 후퇴한다. 쓰는 쪽이 관리자 재시작(runas) 직전 한 곳뿐이라
    /// 이 복원이 실질 발동하는 것도 승격 재기동뿐이다.
    /// 창 연속 생성의 경합 없음 근거: 전 창이 단일 UI 스레드(A110)이고, 빈 새 창의 열기
    /// 경로는 미저장 가드(ConfirmDiscardAsync)가 완료 태스크를 돌려 await가 동기 연속이다 —
    /// 루프 한 바퀴 안에서 창 하나의 생성·라우팅이 끝난 뒤 다음 창으로 넘어간다.
    /// </summary>
    public bool TryRestoreSession()
    {
        IReadOnlyList<Integration.RestartSessionFile.WindowSnapshot>? snapshots;
        try
        {
            snapshots = Integration.RestartSessionFile.TryConsume();
        }
        catch
        {
            return false; // 세션 파일 문제가 시작을 막으면 안 된다
        }
        if (snapshots is null) return false;

        var restored = 0;
        foreach (var snapshot in snapshots)
        {
            try
            {
                if (RestoreWindow(snapshot)) restored++;
            }
            catch
            {
                // 이 창만 건너뛰고 계속 — 전부 실패면 false = 기본 1창(호출자 폴백).
            }
        }
        return restored > 0;
    }

    /// <summary>
    /// 스냅샷 1건 → 창 1개 복원(A124). 새 병렬 경로 없이 기존 창 생성·라우팅 경로 재사용:
    /// 파일이 있으면 OpenFileInNewWindow와 같은 순서(Create → OpenFile → Activate — 어느
    /// 모듈로 열지는 확장자 라우팅이 다시 정한다), 없으면 OpenNewWindow와 같은 순서
    /// (Create → OpenModuleById → A81 기본 도크 → Activate). 기록된 파일이 사라졌으면 그
    /// 모듈의 빈 컨텍스트로 후퇴하고, 모듈 ID까지 못 찾으면 창을 만들지 않는다(건너뜀).
    /// 기하는 창 표시 전에 적용(ApplySessionBounds — A55 화면 밖 보정 재사용).
    /// 여러 창의 Activate 경합으로 마지막 창만 포커스가 남는 것은 수용(A124 확정).
    /// </summary>
    private bool RestoreWindow(Integration.RestartSessionFile.WindowSnapshot snapshot)
    {
        var file = snapshot.FilePath is { Length: > 0 } path && File.Exists(path) ? path : null;
        var moduleId = snapshot.ModuleId is { Length: > 0 } id
            && _router.Modules.Any(m => m.Id == id) ? id : null;
        if (file is null && moduleId is null) return false;

        var window = Create();
        if (snapshot.Width >= 320 && snapshot.Height >= 240)
        {
            window.ApplySessionBounds(snapshot.X, snapshot.Y,
                snapshot.Width, snapshot.Height, snapshot.Maximized);
        }
        if (file is not null)
        {
            window.OpenFile(file);
        }
        else
        {
            window.OpenModuleById(moduleId!);
            // A81: 파일 없이 모듈로 여는 창의 기본 상태 — OpenNewWindow와 동일하게 명시한다.
            window.SetDockedState(listDocked: true, infoDocked: true);
        }
        window.Activate();
        return true;
    }

    private MainWindow FindReusable(string? moduleId)
    {
        // 1) 같은 모듈 창 (여러 개면 가장 최근 활성화된 것).
        //    A222: "항상 새 창" 설정(A24)의 건너뛰기 게이트는 폐지 — 항상 이 단계부터 본다.
        if (moduleId is not null)
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
        UpdateInstanceNumbers(); // 첫 창부터 번호가 붙는다 (A2 → A136/v0.162.0에서 개수 조건 폐지)
        return window;
    }

    /// <summary>
    /// 인스턴스 번호 갱신 (A2, v0.58.0 / A56, v0.87.0): 생성 순서대로 1부터 번호를 매긴다.
    /// A136(v0.162.0): **창 개수 조건 폐지** — 창이 하나뿐이어도 1을 준다("1-KOTU").
    /// 종전에는 창이 하나면 0(표시 없음)이었다.
    /// 10번째 이상도 실제 번호를 그대로 넘긴다 — 제목 접두 번호는 자릿수 제한이 없다
    /// (A104 · 표기 형식은 A103/A136의 "10-KOTU").
    /// A102(v0.130.0)부터 아이콘 합성에는 번호가 쓰이지 않고(테두리 = 모듈 색·번호 배지 제거),
    /// A141(v0.162.0)이 하단 바 색상 배지까지 없앴다 — 즉 번호의 소비처는 창 제목 하나뿐이다.
    /// </summary>
    private void UpdateInstanceNumbers()
    {
        for (var i = 0; i < _ordered.Count; i++)
            _ordered[i].SetInstanceNumber(i + 1);
    }
}
