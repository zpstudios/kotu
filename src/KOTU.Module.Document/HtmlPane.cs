using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace KOTU.Module.Document;

/// <summary>
/// HTML 렌더 판(A248) — .html/.htm 뷰 모드의 WebView2 구현. 코드 전용 컨트롤이다(자체 XAML
/// 없음 — SidePanelHost 선례): WebView2를 XAML에 선언하면 XamlCompiler 네임스페이스 해석이
/// 저장소 선례 0인 위험 축이라, 컨트롤 생성·삽입 전부를 코드로 한다(WMC 부류 컴파일 실패와
/// v0.174.1 런타임 XAML 파스 실패 두 축을 모두 피하는 선택 — CI는 후자를 못 잡는다).
///
/// <b>보안 기본값(로컬 열람기)</b>: 스크립트 off(IsScriptEnabled=false — 사양 확정)에 더해
/// 새 창 열기 억제(NewWindowRequested)·file:// 이외 항해 차단(NavigationStarting — 링크로
/// 브라우저처럼 떠돌지 않게. 로컬 상대 링크는 file://로 풀리므로 허용된다)·컨텍스트 메뉴/
/// DevTools/상태 바/브라우저 단축키 off. 내장 줌(Ctrl+휠·핀치)도 IsZoomControlEnabled=false·
/// IsPinchZoomEnabled=false로 꺼서 줌 축을 앱(_zoomPercent — SetZoomPercent)에만 남긴다.
///
/// <b>줌(A225 관용구의 WebView2 절반)</b>: WinUI 3의 WebView2 컨트롤에는 WPF/WinForms의
/// ZoomFactor 속성이 없다(CoreWebView2Controller 비노출) — 대신 ExecuteScriptAsync로 문서
/// 루트에 CSS zoom을 건다. IsScriptEnabled=false는 문서 안 스크립트만 막고 ExecuteScriptAsync
/// 주입은 막지 않는다는 것이 CoreWebView2Settings 문서의 명시 규정이라 두 사양이 충돌하지
/// 않는다. 적용 시점 = 항해 완료(NavigationCompleted)와 배율 변경(SetZoomPercent) 두 곳.
///
/// <b>런타임 부재(구 Win10 미설치 가능)</b>: EnsureCoreWebView2Async가 던진다 — 실패는
/// 정적 캐시(RuntimeUnavailable)로 세션 내 1회만 판정하고(사양 — 재진입 재시도 비용 억제),
/// 호출부(DocumentView)는 잠금 뷰로 폴백한다. 모든 WebView2 API 호출은 try/catch 격리 —
/// 렌더 실패가 문서 모듈 본기능(편집·저장)을 죽이면 안 된다.
///
/// <b>수명</b>: 뷰가 내려갈 때 반드시 <see cref="Close"/> — WebView2는 별도 브라우저
/// 프로세스를 띄우므로 Close 누락 = 프로세스 잔존이다(PdfPane과 달리 관리 밖 자원).
/// </summary>
public sealed partial class HtmlPane : UserControl
{
    /// <summary>세션 캐시: 런타임 부재/초기화 실패 1회 판정(사양) — 이후 뷰들은 시도 없이
    /// 바로 잠금 뷰로 간다(파일 연속 전환 때마다 실패 초기화를 반복하지 않는다).</summary>
    private static bool s_runtimeUnavailable;

    /// <summary>true = 이 세션에서 WebView2 초기화가 이미 실패했다 — 호출부는 HTML 렌더
    /// 갈래 자체를 건너뛴다(진입 전 게이트 — 판을 띄웠다 내리는 깜빡임 방지).</summary>
    public static bool RuntimeUnavailable => s_runtimeUnavailable;

    private WebView2? _webView;
    private bool _coreConfigured; // 설정·이벤트 배선은 초기화 성공 후 1회
    private bool _closed;         // Close 후 재사용 금지 표지(뷰와 함께 버려진다)
    private int _loadSeq;         // 빠른 파일 전환·토글 연타의 낡은 항해 무산(_openSeq 관용구)
    private double _zoomScale = 1.0;
    private TaskCompletionSource<bool>? _pendingNav; // 진행 중 LoadAsync의 항해 완료 신호

    public HtmlPane() => IsTabStop = false;

    /// <summary>
    /// path를 file:// URI로 항해한다(상대 리소스(이미지·CSS) 성립 — 사양). 저장된 파일
    /// 기준이라 더티 미저장 내용은 반영되지 않는다(사양 수용). 반환 = 항해 성공 여부:
    /// 초기화 실패(런타임 부재)·Navigate 예외·항해 실패(파일 소실 등) 전부 false —
    /// 호출부가 잠금 뷰로 폴백한다. 재진입·연타는 시퀀스로 무산된다(낡은 호출은 false).
    /// </summary>
    public async Task<bool> LoadAsync(string path)
    {
        var seq = ++_loadSeq;
        _pendingNav?.TrySetResult(false); // 직전 호출이 아직 대기 중이면 깨워서 물러나게 한다
        _pendingNav = null;
        if (!await EnsureWebViewAsync()) return false;
        if (seq != _loadSeq || _closed) return false;

        var nav = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingNav = nav;
        try
        {
            // AbsoluteUri가 경로 문자를 file:/// 형태로 이스케이프한다(공백·한글 경로 안전).
            _webView!.CoreWebView2.Navigate(new Uri(path).AbsoluteUri);
        }
        catch (Exception)
        {
            if (ReferenceEquals(_pendingNav, nav)) _pendingNav = null;
            return false;
        }
        var ok = await nav.Task; // NavigationCompleted(또는 Clear/Close/후속 Load)가 깨운다
        return ok && seq == _loadSeq && !_closed;
    }

    /// <summary>앱 줌 축(_zoomPercent — A225)을 이 판에 적용한다. 초기화 전이면 배율만 기억해
    /// 두고 항해 완료 시점(ApplyZoomScript 호출부)이 따라잡는다.</summary>
    public void SetZoomPercent(int percent)
    {
        _zoomScale = percent / 100.0;
        if (_webView?.CoreWebView2 is not null) ApplyZoomScript();
    }

    /// <summary>판을 비운다(파일·PDF·무제 전환 — PdfPane.Clear 관용구). 진행 중 항해는
    /// 시퀀스와 대기 해제로 무산시키고, 이전 문서가 다음 표시 때 비치지 않게 빈 페이지로 보낸다.</summary>
    public void Clear()
    {
        _loadSeq++;
        _pendingNav?.TrySetResult(false);
        _pendingNav = null;
        if (_webView?.CoreWebView2 is not { } core) return;
        try
        {
            core.Navigate("about:blank"); // NavigationStarting 필터가 명시 허용하는 유일한 비 file: 목적지
        }
        catch (Exception)
        {
            // 비우기 실패는 무해하다 — 다음 표시 전에 LoadAsync가 어차피 새로 항해한다.
        }
    }

    /// <summary>
    /// 브라우저 프로세스까지 정리한다(뷰 Unloaded — 누락 = 프로세스 잔존, 함정 ④).
    /// 이후 이 인스턴스는 재사용 불가 — 호출부는 참조를 버리고 필요 시 새로 만든다.
    /// </summary>
    public void Close()
    {
        _closed = true;
        _loadSeq++;
        _pendingNav?.TrySetResult(false);
        _pendingNav = null;
        if (_webView is not { } view) return;
        _webView = null;
        Content = null;
        try
        {
            view.Close();
        }
        catch (Exception)
        {
            // Close 실패까지 왔다면 프로세스 정리는 OS 몫이다 — 앱 종료를 막지 않는다.
        }
    }

    /// <summary>
    /// WebView2 컨트롤 생성(지연 — HTML 뷰를 안 쓰는 세션에는 만들지 않는다) + CoreWebView2
    /// 초기화. 초기화 예외 = 런타임 부재로 판정해 정적 캐시에 굳힌다(세션 내 재시도 없음 — 사양).
    /// EnsureCoreWebView2Async는 멱등이라 토글 재진입의 중복 호출은 무해하다.
    /// </summary>
    private async Task<bool> EnsureWebViewAsync()
    {
        if (s_runtimeUnavailable || _closed) return false;
        if (_webView is null)
        {
            try
            {
                _webView = new WebView2
                {
                    // 흰 배경 플래시 방지(다크 테마) — 페이지가 그리기 전까지 뒤가 비쳐 보인다.
                    DefaultBackgroundColor = Microsoft.UI.Colors.Transparent,
                };
                Content = _webView;
            }
            catch (Exception)
            {
                _webView = null;
                s_runtimeUnavailable = true; // 컨트롤 생성부터 실패 — 초기화 실패와 같은 취급
                return false;
            }
        }
        if (_webView.CoreWebView2 is null)
        {
            try
            {
                await _webView.EnsureCoreWebView2Async();
            }
            catch (Exception)
            {
                s_runtimeUnavailable = true; // 런타임 부재(구 Win10)·환경 생성 실패 — 세션 캐시
                return false;
            }
        }
        if (_webView?.CoreWebView2 is not { } core) return false; // 방어 — 초기화 후에도 null이면 포기
        if (!_coreConfigured)
        {
            ConfigureCore(core);
            _coreConfigured = true;
        }
        return true;
    }

    /// <summary>보안·입력 기본값과 이벤트 배선(초기화 성공 후 1회). 설정 대입은 통째로
    /// try/catch — 어느 하나가 던져도 표시 자체는 계속한다(기본값보다 열린 상태로 남는
    /// 속성이 생길 수 있으나, 스크립트 off가 같은 블록 첫 줄이라 최우선으로 걸린다).</summary>
    private void ConfigureCore(CoreWebView2 core)
    {
        try
        {
            var settings = core.Settings;
            settings.IsScriptEnabled = false;                  // 사양 확정 — 로컬 열람기 보안 기본
            settings.AreDefaultScriptDialogsEnabled = false;   // 스크립트 off와 같은 축(방어 중복)
            settings.IsWebMessageEnabled = false;              // 페이지↔앱 통신 없음
            settings.AreDefaultContextMenusEnabled = false;    // 우클릭 메뉴의 인쇄·저장·소스 보기 차단
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;               // 링크 호버 URL 표시줄 — 열람기에 불요
            settings.IsZoomControlEnabled = false;             // 내장 Ctrl+휠/± 줌 off — 앱 줌 축만 남긴다
            settings.IsPinchZoomEnabled = false;               // 핀치도 같은 축 밖(A225 Min=Max 핀과 같은 의도)
            settings.AreBrowserAcceleratorKeysEnabled = false; // Ctrl+F/P/R 등 브라우저 단축키 차단
        }
        catch (Exception)
        {
            // 설정 실패는 표시를 막지 않는다 — 항해 차단(아래 이벤트)이 독립 방어선으로 남는다.
        }
        core.NewWindowRequested += OnNewWindowRequested;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
    }

    /// <summary>새 창 요청(target=_blank·창 띄우는 링크) 억제 — 창 관리는 셸(WindowManager)만 한다.</summary>
    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        try
        {
            args.Handled = true; // 처리 완료 표지 = 새 창을 만들지 않는다
        }
        catch (Exception)
        {
            // 억제 실패 시 새 창은 기본 브라우저 몫이다 — 앱은 죽지 않는다.
        }
    }

    /// <summary>
    /// file:// 이외 항해 취소 — 링크 클릭으로 웹을 떠돌지 않게(사양 제안 채택). 같은 폴더
    /// 상대 링크는 file://로 풀리므로 허용된다(문서 바 상태(_shownPath)는 호출부가 원 파일
    /// 유지 — 항해를 상태에 반영하지 않는 것으로 성립). about:blank는 Clear의 목적지라 예외.
    /// </summary>
    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        try
        {
            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri)
                && (uri.IsFile || uri.AbsoluteUri == "about:blank"))
                return;
            args.Cancel = true;
        }
        catch (Exception)
        {
            try
            {
                args.Cancel = true; // 판정 불능이면 막는 쪽이 안전 기본값이다
            }
            catch (Exception)
            {
                // Cancel조차 못 세우면 항해는 진행된다 — 스크립트 off라 피해 면은 좁다.
            }
        }
    }

    /// <summary>항해 완료 — 대기 중 LoadAsync를 깨우고, 성공이면 현재 배율을 새 문서에 적용한다.</summary>
    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        bool ok;
        try
        {
            ok = args.IsSuccess;
        }
        catch (Exception)
        {
            ok = false;
        }
        _pendingNav?.TrySetResult(ok); // 외부 URL 취소 등 늦은 완료는 TCS가 이미 닫혀 무해(no-op)
        if (ok) ApplyZoomScript();
    }

    /// <summary>
    /// CSS zoom 주입(클래스 주석 "줌" 절 — ZoomFactor 부재의 대체). 문서 루트(html 요소)에
    /// 걸어 body 스타일과 충돌하지 않게 한다. fire-and-forget — 실패해도 100% 표시일 뿐
    /// 열람은 계속된다(항해 완료마다 재적용 기회가 온다).
    /// </summary>
    private async void ApplyZoomScript()
    {
        if (_webView is not { } view || _closed) return;
        try
        {
            var zoom = _zoomScale.ToString(CultureInfo.InvariantCulture);
            await view.ExecuteScriptAsync(
                $"document.documentElement.style.zoom='{zoom}'");
        }
        catch (Exception)
        {
            // 초기화 직후 경합·페이지 파괴 시점의 주입 실패 — 다음 적용 기회에 따라잡는다.
        }
    }
}
