using Microsoft.UI.Xaml;
using WinUtil.Core.Cli;
using WinUtil.Core.Routing;

namespace WinUtil.App;

/// <summary>
/// 멀티 윈도우 관리자 (v0.14.0). 단일 프로세스 안에서 창을 여러 개 관리한다
/// — 사진을 열어둔 채 동영상도 보는 시나리오의 중심.
///
/// 창 선택 규칙(파일 열기 시):
///  1) 같은 모듈을 보여주는 창이 있으면 재사용 (이미지 ←/→ 탐색 컨텍스트 유지)
///  2) 아직 아무것도 안 연 빈 셸 창이 있으면 재사용 (시작 직후 등)
///  3) 없으면 새 창
/// 마지막 창이 닫히면 앱을 종료한다.
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

    public WindowManager(FileTypeRouter router) => _router = router;

    /// <summary>가장 최근 활성화된 창. 업데이트 다이얼로그 등 공용 UI의 호스트로 쓴다.</summary>
    public MainWindow? ActiveWindow => _windows.Count > 0 ? _windows[^1] : null;

    /// <summary>파일 없이 시작할 때의 첫 창 — 기본 화면은 Info(하드웨어) 모듈(사용자 지정).</summary>
    public MainWindow OpenInitialWindow()
    {
        var window = Create();
        window.ShowDefaultModule();
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

    /// <summary>파일을 담당 모듈 창으로 라우팅해 연다.</summary>
    public void OpenFile(string path)
    {
        var target = FindReusable(_router.Resolve(path)?.Id);
        target.OpenFile(path);
        target.BringToFront();
    }

    /// <summary>모든 창 닫기 = 앱 종료 (트레이 메뉴 'WinUtil 모두 종료').</summary>
    public void CloseAll()
    {
        foreach (var window in _windows.ToArray())
            window.Close();
    }

    private MainWindow FindReusable(string? moduleId)
    {
        // 1) 같은 모듈 창 (여러 개면 가장 최근 활성화된 것)
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
            UpdateInstanceBadges(); // 중간 창이 닫히면 번호 당겨오기 (A2)
            if (_windows.Count == 0)
                Application.Current.Exit();
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
        UpdateInstanceBadges(); // 2번째 창이 뜨는 순간 1번 창에도 배지가 생긴다 (A2)
        return window;
    }

    /// <summary>
    /// 인스턴스 번호 배지 갱신 (A2, v0.58.0): 창이 2개 이상일 때만 생성 순서대로
    /// 1~9번을 표시한다(10번째부터는 표시 없음). 창이 하나면 전부 숨김.
    /// </summary>
    private void UpdateInstanceBadges()
    {
        for (var i = 0; i < _ordered.Count; i++)
            _ordered[i].SetInstanceBadge(_ordered.Count > 1 && i < 9 ? i + 1 : 0);
    }
}
