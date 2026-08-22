using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;

namespace KOTU.Module.Document;

/// <summary>
/// PDF 맞춤 보기 모드(A49 — A30 규격 준용). Contain = 페이지가 뷰포트보다 크면 전부 보이게
/// 줄이고, 작으면 100%(원본 크기) = <b>축소만</b> — A83이 3모듈 공통으로 확정한 의미론이다
/// (구 이름 AutoFit에서 계산식·동작 변화 없음. zoom = min(1:1 배율, 가로 맞춤, 세로 맞춤)).
/// </summary>
public enum PdfFitMode { Contain, FitWidth, FitHeight, ActualSize }

/// <summary>
/// PDF 뷰어 패널(A16). OS 내장 Windows.Data.Pdf로 페이지를 비트맵 렌더한다 —
/// 외부 네이티브 의존성 없음(라이선스·배포 부담 없음), unpackaged 지원.
/// 페이지 크기는 열 때 전부 훑어 레이아웃을 확정하고, 실제 렌더는 ListView 가상화로
/// 화면에 들어온 페이지만 지연 수행한다(리사이클 시 비트맵 해제 — 메모리 상한 유지).
/// 렌더 폭은 모니터 배율(RasterizationScale)을 곱해 선명하게. 암호 PDF는 물어보고 재시도.
/// </summary>
public sealed partial class PdfPane : UserControl
{
    // ---- A121 키보드 스크롤 비율(뷰포트 높이 대비) — 체감 조정은 이 두 상수만 고친다 ----

    /// <summary>A121: ↑/↓ 한 번 = 뷰포트 높이의 이만큼. 읽던 줄을 잃지 않는 잔 스크롤 폭.</summary>
    private const double ArrowScrollRatio = 0.125;

    /// <summary>A121: PageUp/PageDown 한 번 = 뷰포트 높이의 이만큼. 10%를 남겨 읽던 줄이 겹치게 한다.</summary>
    private const double PageScrollRatio = 0.9;

    /// <summary>스크롤 기준 현재 페이지/전체 (1-base). 문서를 내리면 (0, 0).</summary>
    public event Action<int, int>? PageChanged;

    private PdfDocument? _doc;
    private int _loadSeq;                    // 늦은 렌더·이전 문서 결과 무시용
    private List<PageItem> _items = [];
    private double[] _pageOffsets = [];      // 페이지별 누적 세로 오프셋(줌 1 기준)
    private ScrollViewer? _scroll;           // ListView 내장 ScrollViewer (지연 탐색)
    private ScrollContentPresenter? _presenter; // Ctrl+휠 줌 가로채기 지점 (A98, 지연 탐색)

    private sealed class PageItem
    {
        public int Index;      // 0-base 페이지 번호
        public double Width;   // 표시 크기(DIP) — 레이아웃 고정용
        public double Height;
        public double NativeWidth; // 원본 페이지 폭(DIP) — 1:1 배율 계산용 (A49)
    }

    public PdfPane()
    {
        InitializeComponent();
        // A49: Fit이 적용된 동안은 뷰포트 크기 변화를 추종해 배율을 다시 계산한다
        // (비디오 v0.41.0의 Fit width/height 크기 추종과 같은 규칙).
        SizeChanged += (_, _) =>
        {
            if (_appliedFit is { } mode) ApplyFit(mode);
            // A188: 수동 줌 상태(Fit 추종 해제)에서도 창 크기가 바뀌면 콘텐츠 최소 폭을
            // 새 뷰포트로 따라잡아야 페이지가 계속 수평 중앙에 온다(배율은 그대로 둔다).
            else if (_scroll is not null) EnsureContentMinWidth(_scroll.ZoomFactor);
        };
    }

    /// <summary>문서를 열어 페이지 목록을 구성한다. 실패(암호 취소 포함)면 false.</summary>
    public async Task<bool> LoadAsync(string path)
    {
        var seq = ++_loadSeq;
        PdfDocument doc;
        try
        {
            doc = await LoadDocumentAsync(path);
        }
        catch (OperationCanceledException)
        {
            return false; // 암호 입력 취소
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Failed to open PDF", ex.Message);
            return false;
        }
        if (seq != _loadSeq) return false; // 그새 다른 문서/Clear

        _doc = doc;

        // 표시 폭: 패널 폭에 맞추되 과대 렌더 방지 상한. 레이아웃 전이면 A4쯤으로.
        var width = ActualWidth > 100 ? Math.Min(ActualWidth - 48, 1100) : 900;
        var items = new List<PageItem>((int)doc.PageCount);
        var offsets = new double[doc.PageCount];
        double y = 0;
        for (var i = 0; i < doc.PageCount; i++)
        {
            using var page = doc.GetPage((uint)i); // Size만 읽는다 — 렌더는 지연
            var aspect = page.Size.Height / Math.Max(1, page.Size.Width);
            var item = new PageItem
            {
                Index = i,
                Width = width,
                Height = width * aspect,
                NativeWidth = page.Size.Width, // Windows.Data.Pdf 좌표 = 96DPI DIP (A49 1:1 기준)
            };
            items.Add(item);
            offsets[i] = y;
            y += item.Height + 16; // ItemTemplate 상하 마진 8+8
        }
        _items = items;
        _pageOffsets = offsets;
        PageList.ItemsSource = items;
        PageChanged?.Invoke(1, items.Count);
        HookScroll();
        // A49: 파일이 바뀌면 Contain으로 회귀(A30 규칙, 기억 안 함) — 이전 문서에서 쓰던
        // 줌 배율이 남지 않게 즉시 적용한다. 새 문서는 1페이지 머리에 앵커(이전 문서의
        // 스크롤 오프셋이 남아 엉뚱한 페이지로 가는 것 방지). 뷰포트가 0이면 SizeChanged가 이어받는다.
        PageList.UpdateLayout();
        ApplyFitAt(PdfFitMode.Contain, 0);
        return true;
    }

    /// <summary>문서를 내린다(텍스트 파일로 전환 등). 진행 중 렌더는 시퀀스로 무효화.</summary>
    public void Clear()
    {
        _loadSeq++;
        PageList.ItemsSource = null;
        _items = [];
        _pageOffsets = [];
        _doc = null;
        _appliedFit = null; // A49: 문서가 없으면 추종할 Fit도 없다
        if (_itemsPanel is not null) _itemsPanel.MinWidth = 0; // A188 보정 원복(다음 문서가 다시 계산)
        PageChanged?.Invoke(0, 0);
    }

    /// <summary>첫 시도 실패는 암호 PDF로 보고 물어본 뒤 재시도. 취소는 OperationCanceled.</summary>
    private async Task<PdfDocument> LoadDocumentAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        try
        {
            return await PdfDocument.LoadFromFileAsync(file);
        }
        catch
        {
            var password = await PromptPasswordAsync()
                ?? throw new OperationCanceledException();
            return await PdfDocument.LoadFromFileAsync(file, password); // 또 실패 → 호출부 에러 표시
        }
    }

    // ---------- 지연 렌더 (ListView 가상화) ----------

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is not Border border) return;
        var image = (Image)border.Child;
        if (args.InRecycleQueue)
        {
            image.Source = null; // 화면 밖 페이지 비트맵 해제
            return;
        }

        // 렌더 전에도 페이지 크기로 자리를 잡아 스크롤 길이가 흔들리지 않게 한다
        var item = (PageItem)args.Item;
        image.Width = item.Width;
        image.Height = item.Height;
        if (args.Phase == 0) args.RegisterUpdateCallback(OnRenderPhase);
    }

    private async void OnRenderPhase(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (_doc is null || args.ItemContainer.ContentTemplateRoot is not Border border) return;
        var seq = _loadSeq;
        var item = (PageItem)args.Item;
        var image = (Image)border.Child;
        try
        {
            var scale = XamlRoot?.RasterizationScale ?? 1.0; // 모니터 배율만큼 크게 렌더 → 선명
            using var stream = new InMemoryRandomAccessStream();
            using (var page = _doc.GetPage((uint)item.Index))
            {
                await page.RenderToStreamAsync(stream,
                    new PdfPageRenderOptions { DestinationWidth = (uint)(item.Width * scale) });
            }
            // 문서가 바뀌었거나 컨테이너가 다른 페이지로 재활용됐으면 버린다
            if (seq != _loadSeq || !ReferenceEquals(args.ItemContainer.Content, item)) return;

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (seq != _loadSeq || !ReferenceEquals(args.ItemContainer.Content, item)) return;
            image.Source = bitmap;
        }
        catch
        {
            // 페이지 하나의 렌더 실패가 전체 문서 보기를 막으면 안 된다 — 빈 페이지로 둔다.
        }
    }

    // ---------- 인쇄 렌더 (A211 배치 3 — 소비자 = DocumentView.CreatePrintPageAsync) ----------

    /// <summary>
    /// 인쇄(A211 배치 3): 열린 문서의 전체 페이지 수 — 셸 Paginate가 이 값 하나로 즉답한다
    /// (렌더 0회: PageCount는 문서 메타 값이라 수백 페이지 PDF에서도 상수 시간이다). 문서가 없으면 0.
    /// LoadAsync 진행 중에는 직전 문서 값이 유지된다(_doc 교체는 성공 시점) — 그동안의 인쇄
    /// 대상도 직전 문서다(파일명 표시(_shownPath)가 같은 시점에 함께 바뀌므로 서로 어긋나지 않는다).
    /// </summary>
    public int PrintPageCount => _doc is { } doc ? (int)doc.PageCount : 0;

    /// <summary>
    /// 인쇄(A211 배치 3): 페이지 원본 크기(96DPI DIP — Windows.Data.Pdf 좌표, A49 1:1 기준과
    /// 같은 단위). pageNumber는 1-base(IPrintPageProvider 규약). LoadAsync가 레이아웃 확정 때
    /// 하듯 GetPage로 Size만 읽고 닫는다(렌더 없음). 문서 없음·범위 밖·조회 실패 = null.
    /// </summary>
    public (double Width, double Height)? GetPrintPageSize(int pageNumber)
    {
        if (_doc is not { } doc) return null;
        if (pageNumber < 1 || pageNumber > (int)doc.PageCount) return null;
        try
        {
            using var page = doc.GetPage((uint)(pageNumber - 1));
            var size = page.Size;
            return size.Width > 0 && size.Height > 0 ? (size.Width, size.Height) : null;
        }
        catch
        {
            return null; // 손상 페이지 — 호출부가 null을 안내 페이지로 처리한다(계약)
        }
    }

    /// <summary>
    /// 인쇄(A211 배치 3): pageNumber(1-base) 페이지를 pixelWidth 픽셀 폭으로 지연 렌더한 비트맵.
    /// 화면 경로(OnRenderPhase)와 같은 관용구다 — RenderToStreamAsync(DestinationWidth) →
    /// BitmapImage.SetSourceAsync. 열린 _doc 재사용이라 암호 PDF도 재입력 없이 찍힌다(조사 §2).
    /// 화면용 비트맵(ListView 컨테이너의 캐시)은 재사용하지 않는다 — 그쪽 폭은 화면 배율
    /// 기준이라 인쇄 해상도에 못 미친다(조사 §1-ⓒ). 호출 스레드 = UI(계약 규약 그대로 — 화면
    /// 렌더(OnRenderPhase)와 같다): RenderToStreamAsync는 WinRT 비동기 작업이라 await 동안 UI를
    /// 블로킹하지 않고, BitmapImage는 DependencyObject라 워커 스레드로 뺄 수도 없다.
    /// 렌더 사이 문서가 바뀌면(_loadSeq — 화면 렌더의 늦은 도착 폐기와 같은 시퀀스 가드) null.
    /// 실패도 null — 한 페이지 실패가 인쇄 파이프를 죽이면 안 된다(화면 쪽 빈 페이지 방침과 동형).
    /// </summary>
    public async Task<BitmapImage?> RenderPrintPageAsync(int pageNumber, uint pixelWidth)
    {
        if (_doc is not { } doc || pixelWidth == 0) return null;
        if (pageNumber < 1 || pageNumber > (int)doc.PageCount) return null;
        var seq = _loadSeq;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var page = doc.GetPage((uint)(pageNumber - 1)))
            {
                await page.RenderToStreamAsync(stream,
                    new PdfPageRenderOptions { DestinationWidth = pixelWidth });
            }
            if (seq != _loadSeq) return null; // 그새 다른 문서/Clear — 낡은 렌더 폐기

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return seq == _loadSeq ? bitmap : null;
        }
        catch
        {
            return null;
        }
    }

    // ---------- 현재 페이지 추적 ----------

    private void HookScroll()
    {
        _scroll ??= FindDescendant<ScrollViewer>(PageList);
        if (_scroll is null) return;
        _scroll.ViewChanged -= OnViewChanged; // 중복 구독 방지
        _scroll.ViewChanged += OnViewChanged;

        // A98: Ctrl+휠 줌. 내장 Ctrl+휠 줌은 ScrollViewer 자신이 처리하므로 그보다
        // 먼저(버블 경로상 앞서는 콘텐츠 프레젠터에서) 가로채 우리 수동 줌으로 대체한다
        // (Fit 추종 해제(A49)를 명시 호출해야 해서 내장 줌에 맡길 수 없다).
        // 페이지(콘텐츠) 위에서만 배선되므로 리스트/그리드 무동작 규칙도 함께 성립한다.
        if (_presenter is null && FindDescendant<ScrollContentPresenter>(_scroll) is { } presenter)
        {
            _presenter = presenter;
            presenter.PointerWheelChanged += OnPresenterWheel;
            // A148: 드래그 팬도 같은 요소에 — 페이지 위에서만 동작한다는 규칙을 배선 지점이 보장한다.
            // 여기는 이미지 모듈과 달리 눌림·이동이 ListViewItem 컨테이너를 먼저 지나므로
            // handledEventsToo가 필수다(VideoPlayerView.xaml.cs:235-240의 Slider와 같은 사정 —
            // 항목이 시각 상태 때문에 포인터를 소비하면 += 구독은 아예 호출되지 않는다).
            // 휠(A98)은 항목이 소비하지 않아 종전대로 +=를 유지한다.
            presenter.AddHandler(PointerPressedEvent,
                new PointerEventHandler(OnPresenterPointerPressed), handledEventsToo: true);
            presenter.AddHandler(PointerMovedEvent,
                new PointerEventHandler(OnPresenterPointerMoved), handledEventsToo: true);
            // 놓기와 캡처 상실은 같은 종료 처리로(VideoPlayerView.xaml.cs:237-240 선례).
            presenter.AddHandler(PointerReleasedEvent,
                new PointerEventHandler(OnPresenterPointerReleased), handledEventsToo: true);
            presenter.AddHandler(PointerCaptureLostEvent,
                new PointerEventHandler(OnPresenterPointerReleased), handledEventsToo: true);
        }
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_scroll is null || _items.Count == 0) return;

        // A49: 수동 줌(Ctrl+휠(A98)·핀치, A16)이 들어오면 Fit "적용 중" 상태를 해제한다(크기 추종 중단).
        // ApplyFit이 건 ChangeView는 _appliedZoom과 일치하므로 여기 걸리지 않는다.
        // A148 팬도 마찬가지다 — 오프셋만 옮기고 배율은 null(무변경)이라 ZoomFactor가 _appliedZoom
        // 그대로여서 이 비교에 걸리지 않는다. 그래서 팬 중 억제 플래그가 따로 필요 없다.
        if (_appliedFit is not null && Math.Abs(_scroll.ZoomFactor - _appliedZoom) > 0.01)
            _appliedFit = null;

        // A188: 내장 핀치 줌은 우리 코드(ApplyFitAt·ZoomAtPointer)를 거치지 않는다 — 제스처가
        // 끝난 최종 이벤트에서 최소 폭을 현재 배율로 따라잡는다. 중간 이벤트마다 레이아웃을
        // 흔들지 않게 최종만 본다(EditorDecor.cs의 IsIntermediate 구분과 같은 관용구). 팬(A148)의
        // ChangeView 폭주는 배율이 그대로라 EnsureContentMinWidth의 변화 없음 조기 반환에 걸린다.
        if (!e.IsIntermediate) EnsureContentMinWidth(_scroll.ZoomFactor);

        // A148: 팬은 매 프레임 ChangeView를 걸어 이 이벤트가 폭주한다 — 페이지 번호가 그대로면
        // 표시를 건드리지 않도록 소비처(DocumentView)가 같은 값 조기 반환으로 받는다.
        PageChanged?.Invoke(CurrentPageIndex() + 1, _items.Count);
    }

    /// <summary>뷰포트 세로 중앙이 걸친 페이지(0-base) = 현재 페이지. 오프셋은 줌 배율을 되돌려 비교.</summary>
    private int CurrentPageIndex()
    {
        if (_scroll is null || _items.Count == 0) return 0;
        var center = (_scroll.VerticalOffset + _scroll.ViewportHeight / 2)
                     / Math.Max(0.1, _scroll.ZoomFactor);
        var idx = Array.BinarySearch(_pageOffsets, center);
        if (idx < 0) idx = ~idx - 1;
        return Math.Clamp(idx, 0, _items.Count - 1);
    }

    // ---------- 맞춤 보기 (A49 — A30 규격 준용) ----------

    /// <summary>적용 중인 Fit 모드 — 크기 추종용. 수동 줌(Ctrl+휠(A98)·핀치)이 들어오면 null로 풀린다.</summary>
    private PdfFitMode? _appliedFit;

    /// <summary>ApplyFit이 마지막으로 건 배율 — 수동 줌과 자체 ChangeView를 구분하는 기준.</summary>
    private float _appliedZoom = 1f;

    /// <summary>
    /// 현재 페이지 기준으로 배율을 계산해 적용한다(A49).
    /// FitHeight = 현재 페이지 전체가 세로로 보이는 배율(페이지 스냅 스크롤은 없다 — 연속 스크롤 유지).
    /// Contain = 페이지가 뷰포트보다 크면 전부 보이게 줄이고, 작으면 100%(원본 크기 — 확대하지 않는다).
    /// 적용 후에는 뷰포트 크기 변화를 추종하고, 수동 줌이 들어오면 추종을 멈춘다.
    /// </summary>
    public void ApplyFit(PdfFitMode mode)
    {
        HookScroll();
        ApplyFitAt(mode, CurrentPageIndex());
    }

    /// <summary>idx 페이지를 기준 페이지로 Fit을 적용한다(새 문서는 0으로 고정 호출).</summary>
    private void ApplyFitAt(PdfFitMode mode, int idx)
    {
        _appliedFit = mode; // 뷰포트가 아직 0이어도 기억해 두면 SizeChanged 재적용이 이어받는다
        HookScroll();
        if (_scroll is null || _items.Count == 0) return;

        var viewportW = _scroll.ViewportWidth;
        var viewportH = _scroll.ViewportHeight;
        if (viewportW <= 0 || viewportH <= 0) return;

        idx = Math.Clamp(idx, 0, _items.Count - 1);
        var item = _items[idx];
        if (item.Width <= 0 || item.Height <= 0) return;

        // 페이지 실측: 가로 = 비트맵 폭 + 테두리 2, 세로 = 높이 + 테두리 2 + 상하 마진 16(전부 보이게)
        var fitWidth = viewportW / (item.Width + 2);
        var fitHeight = viewportH / (item.Height + 18);
        var actual = item.NativeWidth > 0 ? item.NativeWidth / item.Width : 1.0;
        var zoom = mode switch
        {
            PdfFitMode.FitWidth => fitWidth,
            PdfFitMode.FitHeight => fitHeight,
            PdfFitMode.ActualSize => actual,
            // Contain = 축소만(A83): 1:1 배율(actual)을 상한으로 둬 작은 페이지는 확대하지 않는다
            _ => Math.Min(actual, Math.Min(fitWidth, fitHeight)),
        };
        zoom = Math.Clamp(zoom, (double)_scroll.MinZoomFactor, (double)_scroll.MaxZoomFactor);

        // 세로: 페이지 전체가 보여야 하는 모드는 현재 페이지 머리로 스냅, 나머지는 보던 지점 유지.
        var top = mode is PdfFitMode.FitHeight or PdfFitMode.Contain
            ? _pageOffsets[idx] * zoom
            : _scroll.VerticalOffset / Math.Max(0.1, _scroll.ZoomFactor) * zoom;
        // A188: 배율을 걸기 전에 콘텐츠 최소 폭부터 새 배율로 맞추고 레이아웃을 확정한다 —
        // 축소 배율에서 콘텐츠 폭이 뷰포트 아래로 내려가면 ZoomMode ScrollViewer가 줌 콘텐츠를
        // 좌상단 기준으로 놓아 ItemContainerStyle의 Center가 무력화되기 때문(원인 상세는
        // EnsureContentMinWidth 주석). UpdateLayout으로 반영한 뒤의 ExtentWidth라야 아래
        // 중앙 계산(baseWidth)이 이번 배율의 실제 패널 폭을 읽는다(낡은 값 방지).
        EnsureContentMinWidth(zoom);
        PageList.UpdateLayout();
        // 가로: 확대로 콘텐츠가 뷰포트보다 넓어지면 중앙 정렬(페이지는 콘텐츠 가로 중앙에 있다).
        // 페이지가 뷰포트에 들어오는 배율이면 left = 0 — EnsureContentMinWidth가 채운 폭 안에서
        // Center 정렬이 페이지를 뷰포트 중앙에 놓는다.
        var baseWidth = _scroll.ExtentWidth / Math.Max(0.1, _scroll.ZoomFactor);
        var left = Math.Max(0, (baseWidth * zoom - viewportW) / 2);

        _appliedZoom = (float)zoom;
        _scroll.ChangeView(left, top, (float)zoom, disableAnimation: true);
    }

    // ---------- 수평 중앙 정렬 보정 (A188) ----------

    /// <summary>A188: 최소 폭을 거는 콘텐츠 패널(ItemsStackPanel) — 템플릿 적용 후 지연 확보.</summary>
    private Panel? _itemsPanel;

    /// <summary>
    /// 콘텐츠 패널의 MinWidth를 "뷰포트 폭 ÷ 배율"로 유지한다(A188). ZoomMode가 켜진
    /// ScrollViewer는 배율이 걸린 콘텐츠를 좌상단 앵커로 놓는다 — 축소 배율(Contain 등)로
    /// 콘텐츠 폭 × 배율이 뷰포트보다 좁아지면 스크롤 여지도 없어 되돌릴 방법이 없고,
    /// ItemContainerStyle의 HorizontalContentAlignment=Center는 (왼쪽으로 쏠린) 콘텐츠 폭
    /// 기준이라 페이지가 왼쪽에 붙는다. 패널 폭을 "배율을 곱하면 항상 뷰포트 이상"으로
    /// 유지하면 이 퇴화 자체가 생기지 않는다 — 페이지가 뷰포트에 들어오는 배율에서는 Center
    /// 정렬이 뷰포트 중앙과 일치하고, 넘치는 배율에서는 종전대로 스크롤 오프셋(ApplyFitAt의
    /// left·팬)이 담당한다. 뷰포트 폭 ÷ 배율은 배율 1에서 뷰포트 폭과 만나므로 페이지 실측
    /// 폭과의 max 전환이 연속적이고, 세로 좌표(_pageOffsets·A121 키 스크롤·A152 아래 여백
    /// 44)는 가로 최소 폭과 무관해 건드리지 않는다.
    /// </summary>
    private void EnsureContentMinWidth(double zoom)
    {
        if (_scroll is null || zoom <= 0) return;
        _itemsPanel ??= PageList.ItemsPanelRoot; // ThumbnailExplorer.xaml.cs의 ItemsPanelRoot과 같은 접근
        if (_itemsPanel is null) return;
        var viewportW = _scroll.ViewportWidth;
        if (viewportW <= 0) return;
        var min = viewportW / zoom;
        // 반 픽셀 이하 차이로는 레이아웃을 다시 돌리지 않는다(팬 중 ViewChanged 폭주 대비).
        if (Math.Abs(_itemsPanel.MinWidth - min) > 0.5) _itemsPanel.MinWidth = min;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    // ---------- Ctrl+휠 줌 (A98 — A84 Shift+휠 대체) ----------

    /// <summary>뷰어 콘텐츠(페이지) 위 수정자 휠(A98): Ctrl+휠 = 줌. 휠 단독은 스크롤(기본 처리) 유지.</summary>
    private void OnPresenterWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) return;
        e.Handled = true; // 내장 Ctrl+휠 줌보다 먼저 소비 — Fit 추종 해제가 있는 수동 줌으로 대체
        ZoomAtPointer(e);
    }

    /// <summary>포인터 위치를 고정점으로 노치당 10% 줌. 핀치와 같은 수동 줌이므로 Fit 추종을 해제한다(A49).</summary>
    private void ZoomAtPointer(PointerRoutedEventArgs e)
    {
        if (_scroll is null) return;
        var delta = e.GetCurrentPoint(_scroll).Properties.MouseWheelDelta;
        if (delta == 0) return;
        var oldZoom = _scroll.ZoomFactor;
        var newZoom = (float)Math.Clamp(oldZoom * Math.Pow(1.1, delta / 120.0),
            _scroll.MinZoomFactor, _scroll.MaxZoomFactor);
        if (Math.Abs(newZoom - oldZoom) < 0.0001f) return;

        _appliedFit = null; // OnViewChanged의 0.01 임계값에 기대지 않고 명시적으로 해제
        // A188: 축소로 페이지가 뷰포트에 들어오면 좌상단으로 붙지 않게 최소 폭이 배율을 따라간다.
        // 콘텐츠 폭이 뷰포트로 클램프되면 오프셋도 0으로 클램프돼 페이지가 수평 중앙에 남는다.
        EnsureContentMinWidth(newZoom);
        // 포인터 아래 콘텐츠 지점이 화면에서 움직이지 않게 오프셋을 배율 변화만큼 이동
        var pt = e.GetCurrentPoint(_scroll).Position;
        var ratio = newZoom / oldZoom;
        _scroll.ChangeView(
            (_scroll.HorizontalOffset + pt.X) * ratio - pt.X,
            (_scroll.VerticalOffset + pt.Y) * ratio - pt.Y,
            newZoom, disableAnimation: true);
    }

    // ---------- 드래그 투 스크롤 / 팬 (A148) ----------

    /// <summary>이만큼(px) 넘게 움직이기 전에는 클릭 취급 — 이미지 모듈과 같은 값·같은 의미.</summary>
    private const double PanThresholdPixels = 4;

    private bool _panTracking; // 좌버튼 눌림으로 캡처를 잡았다
    private bool _panActive;   // 임계를 넘겨 실제로 팬 중이다
    private double _panOriginX; // 눌림 시점 포인터 좌표(_scroll 기준)
    private double _panOriginY;
    private double _panStartHorizontal;
    private double _panStartVertical;

    /// <summary>
    /// 좌버튼 드래그로 페이지를 밀어서 본다(A148). 마우스 전용 — 터치·펜은 ScrollViewer 내장
    /// 패닝이 이미 처리하므로 뺏지 않는다. PageList는 SelectionMode=None·IsItemClickEnabled=False라
    /// 항목 선택과 충돌하지 않고, PDF는 비트맵 렌더라 텍스트 선택 드래그도 없다.
    /// 문서 모듈에는 DoubleTapped 소비자가 없어 이미지 모듈 같은 더블탭 억제 창은 두지 않는다.
    /// 커서 변경(grab/grabbing)을 하지 않는 이유는 이미지 모듈 쪽 주석과 같다(후속 등재 후보).
    /// </summary>
    private void OnPresenterPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_scroll is null || _presenter is null) return;
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse) return;
        // 왼쪽 눌림 "전이"만 태운다 — A112·A131과 같은 관용구.
        var point = e.GetCurrentPoint(_scroll);
        if (point.Properties.PointerUpdateKind
            != Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed) return;
        if (_scroll.ScrollableWidth <= 0 && _scroll.ScrollableHeight <= 0) return; // 밀 여지 없음
        if (!_presenter.CapturePointer(e.Pointer)) return;

        _panTracking = true;
        _panActive = false;
        _panOriginX = point.Position.X;
        _panOriginY = point.Position.Y;
        _panStartHorizontal = _scroll.HorizontalOffset;
        _panStartVertical = _scroll.VerticalOffset;
    }

    /// <summary>
    /// 콘텐츠가 손을 따라오게 오프셋을 반대로 민다. 배율은 null = 무변경이라 Fit 추종 상태(A49)를
    /// 건드리지 않는다 — OnViewChanged의 줌 비교(ZoomFactor vs _appliedZoom)에 걸리지 않기 때문으로,
    /// 키 스크롤(TryHandleNavKey)이 기대는 성질과 같다. 임계를 넘긴 뒤에만 Handled를 세운다.
    /// </summary>
    private void OnPresenterPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_panTracking || _scroll is null) return;
        var position = e.GetCurrentPoint(_scroll).Position;
        var dx = position.X - _panOriginX;
        var dy = position.Y - _panOriginY;
        if (!_panActive)
        {
            // 맨해튼 거리 — 이미지 모듈과 같은 판정식.
            if (Math.Abs(dx) + Math.Abs(dy) <= PanThresholdPixels) return;
            _panActive = true;
        }

        e.Handled = true;
        // 줌 경로와 같은 이유로 애니메이션을 끈다 — 손보다 늦게 따라오면 미끄러진다.
        _scroll.ChangeView(_panStartHorizontal - dx, _panStartVertical - dy, null,
            disableAnimation: true);
    }

    /// <summary>놓기·캡처 상실 공용 종료. Handled는 세우지 않는다(셸 경로 보존).</summary>
    private void OnPresenterPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_panTracking) return;
        _panTracking = false;
        _panActive = false;
        _panOriginX = 0;
        _panOriginY = 0;
        _panStartHorizontal = 0;
        _panStartVertical = 0;
        // 캡처가 이미 풀린 뒤(PointerCaptureLost)라면 무동작 — 두 경로를 나눌 필요가 없다.
        _presenter?.ReleasePointerCapture(e.Pointer);
    }

    // ---------- 키보드 스크롤 (A121) ----------

    /// <summary>
    /// PDF 모드 세로 스크롤 키를 처리한다(A121). 처리했으면 true — 호출부(DocumentView의 터널링
    /// PreviewKeyDown)는 true일 때만 Handled를 세운다. 게이트(PDF 모드·텍스트 입력·리스트 포커스·
    /// 수정키)는 전부 호출부가 본다 — 이 메서드는 "우리 키인가 + 스크롤할 대상이 있는가"만 판정한다.
    ///
    /// ↑/↓ = 뷰포트 높이 × <see cref="ArrowScrollRatio"/>, PageUp/PageDown = × <see cref="PageScrollRatio"/>,
    /// Home/End = 문서 처음/끝. ←/→는 배정하지 않는다(수평 스크롤·파일 넘김과 의미가 겹친다 — A121 확정).
    /// 수직 오프셋만 옮기고 배율(ZoomFactor)·가로 오프셋은 null = 무변경이라 Fit 추종 상태(A49)도
    /// 건드리지 않는다(OnViewChanged의 줌 비교에 걸리지 않는다).
    ///
    /// 경계(최상단에서 ↑ 등)에서도 true다: ScrollViewer가 범위를 클램프하므로 무의미한 호출이 무해하고,
    /// 여기서 false를 돌려주면 그 한 번만 ListView 내장 포커스 이동으로 새어 동작이 들쭉날쭉해진다.
    /// </summary>
    public bool TryHandleNavKey(VirtualKey key)
    {
        // 표시 직후처럼 아직 못 잡았으면 이때 한 번 더 시도한다(잡은 뒤에는 건너뛰므로 키마다
        // 비주얼 트리를 훑지 않는다). 그래도 못 잡으면 무처리 — 호출부가 원 기능에 양보한다.
        if (_scroll is null) HookScroll();
        if (_scroll is null || _items.Count == 0) return false; // 스크롤러 미확보 · PDF 미로드

        var viewport = _scroll.ViewportHeight;
        var y = key switch
        {
            VirtualKey.Up => _scroll.VerticalOffset - viewport * ArrowScrollRatio,
            VirtualKey.Down => _scroll.VerticalOffset + viewport * ArrowScrollRatio,
            VirtualKey.PageUp => _scroll.VerticalOffset - viewport * PageScrollRatio,
            VirtualKey.PageDown => _scroll.VerticalOffset + viewport * PageScrollRatio,
            VirtualKey.Home => 0.0,
            VirtualKey.End => _scroll.ScrollableHeight,
            _ => double.NaN, // 우리 키가 아니다
        };
        if (double.IsNaN(y)) return false;

        // disableAnimation: false(부드럽게) — 이 파일의 줌 경로(ApplyFitAt·ZoomAtPointer)가 쓰는
        // true와 의도가 다르다. 줌은 배율과 오프셋이 한 프레임에 같이 확정돼야 포인터 고정점이
        // 튀지 않아 애니메이션을 끄지만, 키 스크롤은 화면이 얼마나 어디로 움직였는지 눈이 따라가야
        // 해서 관성 이동이 맞다. 오토리피트(꾹 누르기)로 연타되면 진행 중 애니메이션의 현재
        // 오프셋에 다음 이동이 얹혀 연속 스크롤이 된다 — 이것이 A121이 의도한 홀드 동작이다.
        _scroll.ChangeView(null, y, null, disableAnimation: false);
        return true;
    }

    // ---------- 다이얼로그 ----------

    private async Task<string?> PromptPasswordAsync()
    {
        if (XamlRoot is null) return null;
        var box = new PasswordBox();
        var dialog = new ContentDialog
        {
            Title = "Password required",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "This PDF appears to be password-protected." },
                    box,
                },
            },
            PrimaryButtonText = "Open",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary && box.Password.Length > 0
            ? box.Password
            : null;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        if (XamlRoot is null) return;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
