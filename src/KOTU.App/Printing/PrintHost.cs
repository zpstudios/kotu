using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Printing;
using Windows.Graphics.Printing;
using KOTU.Core.Contracts;

namespace KOTU.App.Printing;

/// <summary>
/// 창당 1개 인쇄 호스트 (A211 배치 1, v0.220.0 — 사양 = docs/A211-print-research.md §3, 부록 B 78).
/// WinRT 인쇄 축(A축): <c>PrintManagerInterop.GetForWindow/ShowPrintUIForWindowAsync</c>(OS 표준
/// 인쇄 대화상자 + 내장 미리보기) + <c>Microsoft.UI.Xaml.Printing.PrintDocument</c>(페이지 공급).
/// 페이지 내용은 활성 모듈 뷰의 <see cref="IPrintPageProvider"/>(배치 2~5에서 구현)가 댄다.
///
/// ⚠️ 저장소 인쇄 API 선례 0 — 이 파일이 선례 0 API의 유일한 집결지다. CI가 여기서 깨지면
/// 최소 복구 = ① 이 파일 삭제 ② MainWindow.xaml.cs의 "A211 배치 1" 표식 4곳 제거
/// (RegisterShortcuts의 Ctrl+P 1줄 · 인쇄 절(필드/속성/RequestPrint) · ShowModule의
/// PrintRequested 블록 · 주석 원복). Core 계약(IPrintPageProvider)은 BCL 전용이라 남아도 안전하다.
///
/// 수명(창 단위 1회 등록 — 조사 §1-ⓐ "해제 없이 재등록하면 예외"의 회피책):
/// - 생성 = 첫 인쇄 요청 시(MainWindow가 지연 생성) — 시작 경로에서 인쇄 API를 건드리지 않는다.
/// - PrintManager 취득·PrintTaskRequested 구독·PrintDocument 이벤트 3종 배선은 창당 **1회**
///   (_registered 가드 — GetForWindow를 다시 불러도 재구독하지 않는다).
/// - 해제는 창 Closed에서 전수(PrintTaskRequested·Paginate/GetPreviewPage/AddPages·Closed 자신).
///   PrintTask.Completed는 태스크당 구독·완료 시 즉시 해제한다.
///
/// 스레드(조사 §1-ⓐ 문서 확인): PrintManager 이벤트(PrintTaskRequested·PrintTask.Completed)는
/// **UI 스레드 밖에서 올 수 있다** — 핸들러는 UI 스레드 선캡처 값(_printDocSource·_jobName)만
/// 만지고, 상태 정리·다이얼로그는 DispatcherQueue.TryEnqueue로 마샬한다(공식 예제 패턴).
/// 특히 PrintDocument는 DependencyObject라 비UI 스레드에서 DocumentSource를 읽으면 죽는다 —
/// 등록 시점(UI 스레드)에 IPrintDocumentSource를 캡처해 두는 것이 공식 예제의 핵심 관용구다.
/// 반대로 PrintDocument의 세 이벤트는 XAML 스레드로 온다(페이지 UIElement를 만드는 자리).
///
/// 지원 방어 2중(조사 §1-ⓐ KB5023773 축): ① PrintManager.IsSupported() 선확인
/// ② 그래도 실호출(등록·ShowPrintUI)이 던질 수 있다(구형 Win10 = KB 미적용 19042 미만 등) —
/// 전 경로 try/catch로 흡수하고 영어 안내 다이얼로그 하나로 닫는다(앱 다운 0).
/// </summary>
internal sealed class PrintHost : IDisposable
{
    /// <summary>지원 불가·표시 실패 안내(부록 B 78 확정 문구 — UI 문자열은 영어만).</summary>
    private const string NotAvailableText = "Printing is not available on this version of Windows.";

    /// <summary>제출까지 간 인쇄 작업이 실패로 끝났을 때(PrintTaskCompletion.Failed — 공식 예제도 안내한다).</summary>
    private const string PrintFailedText = "Printing failed.";

    /// <summary>
    /// GetPageDescription 실패 시의 예비 규격 — US Letter(8.5×11인치)의 96DPI DIP.
    /// 여기까지 오면 인쇄 자체가 성한 상태가 아니지만, 안내 페이지는 그려져야 한다.
    /// </summary>
    private static readonly PrintPageSpec FallbackSpec = new(816, 1056, 0, 0, 816, 1056, 96, 96);

    private readonly Window _window;
    private readonly DispatcherQueue _dispatcher;
    private readonly IntPtr _hwnd; // GetForWindow/ShowPrintUIForWindowAsync 공용 — 창 수명 동안 불변

    private PrintManager? _printMan;
    private PrintDocument? _printDoc;
    private IPrintDocumentSource? _printDocSource; // UI 스레드 선캡처 — PrintTaskRequested(비UI)가 쓴다
    private bool _registered; // 창당 1회 배선 가드 — 재등록(중복 구독) 금지
    private bool _disposed;

    // ---- 세션 상태(요청~PrintTask 완료) — 시작 시 UI 스레드에서 세팅, 비UI 핸들러는 읽기만 ----
    private IPrintPageProvider? _provider;
    private string _jobName = Branding.AppName;
    private PrintPageSpec? _spec;   // Paginate에서 확정(UI 스레드) — 페이지 생성이 재사용
    private int _pageCount = 1;
    private bool _sessionActive;    // 재진입 가드(오토리피트·대화상자 표시 중 재요청 = 무동작)

    internal PrintHost(Window window)
    {
        _window = window;
        _dispatcher = window.DispatcherQueue;
        // 창에서의 HWND 관용구 ②(조사 §2) — MainWindow.xaml.cs의 WindowNative.GetWindowHandle(this)
        // 사용례들과 동일. 공식 인쇄 예제도 같은 형이다.
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        // 수명 자기완결: 창이 닫히면 스스로 전수 해제한다(MainWindow에 해제 코드를 남기지 않는다).
        window.Closed += OnWindowClosed;
    }

    /// <summary>
    /// 인쇄 요청 1건 — Ctrl+P·모듈 하단 바 버튼(배치 2~5)이 모두 이 한 경로다. UI 스레드 전용.
    /// 세션 중(대화상자 표시 중 포함) 재요청은 무동작. 모든 실패는 안내 다이얼로그로 닫힌다(예외 전파 없음).
    /// </summary>
    internal async Task RequestPrintAsync(IPrintPageProvider provider)
    {
        if (_disposed || _sessionActive) return;
        _sessionActive = true;
        var shown = false;
        try
        {
            if (!TryEnsureRegistered())
            {
                await ShowPrintMessageAsync(NotAvailableText);
                return;
            }
            // 비UI 스레드 핸들러(PrintTaskRequested)가 쓸 값을 여기(UI 스레드)서 스냅샷한다 —
            // 뷰 속성을 비UI 스레드에서 읽지 않기 위한 규약(계약 문서와 한 쌍).
            _provider = provider;
            var name = SafeJobName(provider);
            _jobName = string.IsNullOrWhiteSpace(name) ? Branding.AppName : name;
            try
            {
                // OS 인쇄 대화상자 표시. 이 호출이 PrintTaskRequested → (사용자 진행 시)
                // Paginate/GetPreviewPage → AddPages → PrintTask.Completed 순서를 굴린다.
                // IsSupported()가 true여도 구형 Win10(KB5023773 미적용)은 여기서 던진다 — 방어 2중의 둘째.
                shown = await PrintManagerInterop.ShowPrintUIForWindowAsync(_hwnd);
            }
            catch
            {
                shown = false;
                await ShowPrintMessageAsync(NotAvailableText);
            }
        }
        finally
        {
            // 표시까지 갔으면 세션은 PrintTask.Completed(취소 포함)가 끝낸다.
            // 표시 실패(false·예외)는 완료 이벤트가 안 오므로 여기서 즉시 끝낸다.
            if (!shown) EndSession();
        }
    }

    /// <summary>
    /// 인쇄 배선 1회 수행(이미 됐으면 true 즉시). IsSupported 선검사와 실배선 예외를 함께 흡수한다 —
    /// 실패 시 부분 구독을 되감아 다음 시도가 처음부터 다시 가게 한다(중복 구독 없음).
    /// </summary>
    private bool TryEnsureRegistered()
    {
        if (_registered) return true;
        try
        {
            if (!PrintManager.IsSupported()) return false; // 1607+ 존재(min OS 17763 안전 — 조사 §1-ⓐ)
            _printMan = PrintManagerInterop.GetForWindow(_hwnd);
            _printMan.PrintTaskRequested += OnPrintTaskRequested;
            _printDoc = new PrintDocument(); // DependencyObject — UI 스레드에서만 생성 가능
            _printDocSource = _printDoc.DocumentSource; // 비UI 핸들러용 선캡처(공식 관용구)
            _printDoc.Paginate += OnPaginate;
            _printDoc.GetPreviewPage += OnGetPreviewPage;
            _printDoc.AddPages += OnAddPages;
            _registered = true;
            return true;
        }
        catch
        {
            // 부분 배선 롤백 — PrintTaskRequested만 붙고 PrintDocument 생성에서 죽었을 수 있다.
            try { if (_printMan is { } man) man.PrintTaskRequested -= OnPrintTaskRequested; }
            catch { /* 해제 실패는 무시 — 창 수명 객체라 창과 함께 사라진다 */ }
            _printMan = null;
            _printDoc = null;
            _printDocSource = null;
            return false;
        }
    }

    // ---------- PrintManager 계열 이벤트 — UI 스레드 보장 없음(선캡처 값만 사용) ----------

    /// <summary>
    /// 인쇄 대화상자가 문서 소스를 요구하는 시점. 여기서 PrintTask를 만들지 않으면 대화상자가
    /// 자체 오류 화면을 띄운다(우리 세션이 아닐 때는 만들지 않는 것이 옳은 방어).
    /// </summary>
    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        try
        {
            var source = _printDocSource;
            if (source is null || !_sessionActive) return;
            var task = args.Request.CreatePrintTask(_jobName, sourceArgs => sourceArgs.SetSource(source));
            task.Completed += OnPrintTaskCompleted;
        }
        catch
        {
            // 태스크 생성 실패 — 대화상자가 자체 오류 상태를 보인다. 여기서 예외가 새면
            // 비UI 스레드 이벤트 디스패치라 프로세스가 죽는다("다운 0" — 전 핸들러 공통 방어).
        }
    }

    /// <summary>
    /// 세션 종점 — 제출·취소·실패·중단 전부 여기로 온다(대화상자 취소 = Canceled).
    /// 상태 정리는 UI 스레드로 마샬한다(공식 예제의 DispatcherQueue 패턴).
    /// </summary>
    private void OnPrintTaskCompleted(PrintTask sender, PrintTaskCompletedEventArgs args)
    {
        try
        {
            sender.Completed -= OnPrintTaskCompleted; // 태스크당 1구독 — 즉시 해제
            var failed = args.Completion == PrintTaskCompletion.Failed;
            if (_dispatcher.TryEnqueue(() =>
                {
                    EndSession();
                    if (failed) _ = ShowPrintMessageAsync(PrintFailedText);
                }))
            {
                return;
            }
        }
        catch { /* 비UI 스레드 — 새면 프로세스가 죽는다. 아래 정리로만 마감 */ }
        EndSession(); // UI 스레드가 내려간 뒤(창 닫힘) 등 — 필드 정리만이라 비UI에서도 안전
    }

    // ---------- PrintDocument 이벤트 — XAML(UI) 스레드로 온다(페이지 요소를 만드는 자리) ----------

    /// <summary>페이지 수 확정. 규격(_spec)도 여기서 세션에 굳힌다 — 이후 페이지 생성이 재사용.</summary>
    private void OnPaginate(object sender, PaginateEventArgs e)
    {
        try
        {
            // 캐스트는 공식 예제 그대로("Print from your app" — 저장소 선례 0 API라 문서 형태를 복제).
            var options = (PrintTaskOptions)e.PrintTaskOptions;
            _spec = SpecFrom(options.GetPageDescription(0));
            // 0 이하 = 공급자 이상 — 안내 페이지 1장으로 대체한다(SetPreviewPageCount(0) 방지).
            _pageCount = Math.Max(1, _provider?.GetPrintPageCount(_spec) ?? 1);
        }
        catch
        {
            _spec = null;
            _pageCount = 1;
        }
        try { _printDoc?.SetPreviewPageCount(_pageCount, PreviewPageCountType.Final); }
        catch { /* 대화상자가 먼저 닫힌 늦은 도착 — 버린다(핸들러에서 새면 다운) */ }
    }

    /// <summary>
    /// 미리보기 n페이지 요청(1-base). async void = 이벤트 핸들러 관용 — 공급자가 비동기 렌더(PDF)를
    /// 끝낸 뒤 SetPreviewPage를 불러도 미리보기가 그 페이지를 기다린다(UWP PDF 인쇄 예제 패턴.
    /// 배치 1의 안내 페이지는 동기 완료라 핸들러 안에서 곧바로 꽂힌다).
    /// </summary>
    private async void OnGetPreviewPage(object sender, GetPreviewPageEventArgs e)
    {
        var doc = _printDoc;
        if (doc is null) return;
        var page = await BuildPageAsync(e.PageNumber);
        try { doc.SetPreviewPage(e.PageNumber, page); }
        catch { /* 대화상자가 먼저 닫힌 늦은 도착 — 버린다 */ }
    }

    /// <summary>
    /// 본인쇄 확정 — 전 페이지를 한 장씩 공급한다(참조는 넘기고 버린다 — 대용량 메모리 규칙,
    /// 조사 §1-ⓑ MS 조언). 페이지 범위 옵션(PrintTask.Options 활성 + e.PrintTaskOptions 해석)은
    /// 배치 3(PDF)이 이 자리에 얹는다. 어떤 실패에도 AddPagesComplete는 부른다(안 부르면 큐가 매달린다).
    /// </summary>
    private async void OnAddPages(object sender, AddPagesEventArgs e)
    {
        var doc = _printDoc;
        if (doc is null) return;
        try
        {
            var count = _pageCount;
            for (var i = 1; i <= count; i++)
                doc.AddPage(await BuildPageAsync(i));
        }
        catch { /* 남은 페이지 포기 — 아래 완료 통지로 파이프는 닫는다 */ }
        finally
        {
            try { doc.AddPagesComplete(); }
            catch { /* 이미 닫힌 세션 — 무시 */ }
        }
    }

    // ---------- 페이지 조립 ----------

    /// <summary>공급자 페이지를 만들고, null·예외·비UIElement면 안내 페이지로 대체한다(파이프 유지).</summary>
    private async Task<UIElement> BuildPageAsync(int pageNumber)
    {
        var spec = _spec ?? FallbackSpec;
        try
        {
            if (_provider is { } provider
                && await provider.CreatePrintPageAsync(pageNumber, spec) is UIElement page)
            {
                return page;
            }
        }
        catch { /* 공급자 실패 — 안내 페이지로 */ }
        return BuildFallbackPage(spec);
    }

    /// <summary>
    /// 안내(스모크) 페이지 — 조사 배치 1 정의의 "단일 TextBlock 페이지". 공급자가 페이지를 못 내줄 때
    /// 대체되고, 실기기에서 파이프(대화상자→미리보기→인쇄)가 도는지 이 한 장으로 검증할 수 있다.
    /// 색 명시(검정/흰) — 테마 브러시는 다크 테마에서 흰 글자로 풀려 종이에 안 보인다(계약 문서 규칙).
    /// </summary>
    private static UIElement BuildFallbackPage(PrintPageSpec spec)
    {
        var page = new Grid
        {
            Width = spec.PageWidth,
            Height = spec.PageHeight,
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
        };
        page.Children.Add(new TextBlock
        {
            Text = "No printable content.",
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        return page;
    }

    // ---------- 보조 ----------

    private static PrintPageSpec SpecFrom(PrintPageDescription desc) => new(
        desc.PageSize.Width, desc.PageSize.Height,
        desc.ImageableRect.X, desc.ImageableRect.Y,
        desc.ImageableRect.Width, desc.ImageableRect.Height,
        desc.DpiX, desc.DpiY);

    /// <summary>작업 이름 스냅샷 — 뷰 속성 조회 실패까지 흡수한다(이름 때문에 인쇄가 죽으면 안 된다).</summary>
    private static string? SafeJobName(IPrintPageProvider provider)
    {
        try { return provider.PrintJobName; }
        catch { return null; }
    }

    private void EndSession()
    {
        _sessionActive = false;
        _provider = null;
        _spec = null;
        _pageCount = 1;
    }

    /// <summary>
    /// 안내 다이얼로그 — 창당 동시 1개 규칙(A113)의 공용 게이트(ExplorerDialogs.GateFor) 경유라
    /// 다른 대화상자(미저장 가드 등)와 겹치지 않고 차례로 뜬다. 표시 실패는 조용히 포기
    /// (인쇄 실패 자체는 이미 확정 — 안내는 최선 노력).
    /// </summary>
    private async Task ShowPrintMessageAsync(string message)
    {
        try
        {
            var root = _window.Content?.XamlRoot;
            if (root is null) return;
            var gate = ExplorerDialogs.GateFor(root);
            await gate.WaitAsync();
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "Print",
                    Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = "OK",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = root,
                };
                await dialog.ShowAsync();
            }
            finally
            {
                gate.Release();
            }
        }
        catch { /* 창 내림 중 등 — 무시 */ }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args) => Dispose();

    /// <summary>창 닫힘 해제 전수 — PrintTaskRequested·PrintDocument 이벤트 3종·창 Closed 자신.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.Closed -= OnWindowClosed;
        try { if (_printMan is { } man) man.PrintTaskRequested -= OnPrintTaskRequested; }
        catch { /* OS 쪽이 먼저 무너진 경우 — 프로세스 종료 경로라 무시 */ }
        if (_printDoc is { } doc)
        {
            doc.Paginate -= OnPaginate;
            doc.GetPreviewPage -= OnGetPreviewPage;
            doc.AddPages -= OnAddPages;
        }
        _printMan = null;
        _printDoc = null;
        _printDocSource = null;
        EndSession();
    }
}
