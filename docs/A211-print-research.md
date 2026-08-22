# A211 조사 — v0.219.0 시점, 단일 원본

> 과제: 인쇄 기능(이미지·문서(텍스트/마크다운)·PDF 우선 — 사용자 범위 확정)의 실현 축 조사.
> WinUI 3(WASDK 1.8.*) unpackaged 데스크톱 앱(net8.0-windows10.0.19041.0, self-contained)에서
> 어떤 인쇄 API가 성립하는지, 저장소 현행 자산으로 어떤 최소 경로가 나오는지, 배치를 어떻게
> 쪼갤지. 조사 전용 — 코드 변경 없음. 확실하지 않은 판단은 "추정" 표기.
> 저장소 인쇄 관련 코드 선례 = **0건 확실**(grep `Print` — 일치는 REQUIREMENTS.md 등재문뿐).

## 1. WinUI 3 데스크톱 인쇄 API 실태

### 1-ⓐ Windows.Graphics.Printing + PrintManagerInterop — **성립 (현행 공식 경로)**

- **결론: 성립한다. 이것이 MS 공식 권장 경로다.** 공식 문서 "Print from your app"이
  `ms.service: windows-app-sdk`로 재작성돼 있고(2024-09 갱신), 예제가 전부 WinUI 3 데스크톱
  기준이다: `PrintManagerInterop.GetForWindow(hWnd)`로 등록, `PrintManagerInterop.ShowPrintUIForWindowAsync(hWnd)`로
  OS 인쇄 대화상자 표시. UWP의 `PrintManager.GetForCurrentView()`/`ShowPrintUIAsync()`는
  CoreWindow 개념이라 데스크톱에서 불가 — **HWND 인터롭이 필수**다.
  - 문서: https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/print-from-your-app
  - MS 확인(2021-08, WindowsAppSDK#1224): "GetForCurrentView is a CoreWindow concept not
    supported in WinUI3 desktop apps. You need to use the interop helpers."
    https://github.com/microsoft/WindowsAppSDK/issues/1224
- **C# 프로젝션 재료는 추가 패키지 없이 있다**: `Windows.Graphics.Printing.PrintManagerInterop`은
  TFM(`net6.0-windows10.0.17763.0` 이상, .NET 6 SDK+)이 제공하는 C# 인터롭 클래스 —
  KOTU의 TFM `net8.0-windows10.0.19041.0`(KOTU.App.csproj:4)에서 즉시 사용 가능. KOTU가 이미
  쓰는 `WinRT.Interop.InitializeWithWindow`(피커, DocumentView.xaml.cs:1669)와 같은 층이다.
  - 문서(인터롭 클래스 표에 IPrintManagerInterop → PrintManagerInterop 명시):
    https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-com-interop-csharp
- **버전 이력(언제부터 되는가)**:
  - WASDK 0.8~1.0-experimental(2021): 데스크톱에서 전멸 — `PrintManager` 직접 접근 =
    `0x80040155 Interface not registered`, 인터롭 경유도 `0x80070578 Invalid window handle`
    (unpackaged·packaged 공통). https://github.com/microsoft/microsoft-ui-xaml/issues/5831 (dup → #4419)
  - **WASDK 1.1.1부터 성립** — MS(krschau, 2022-06-15): GetForWindow+ShowPrintUIForWindowAsync
    스니펫에 대해 "This should work in WinAppSDK version 1.1.1 and downlevel. We will update
    documentation to reflect this."
    https://github.com/microsoft/microsoft-ui-xaml/issues/4419#issuecomment-1156715435
    (커뮤니티 실증은 1.1.0-preview1부터: castorix의 동작 gif·샘플, 2022-04)
  - **Windows 10은 OS 쪽 수리가 별도로 필요했다**: 한동안 Win11에서만 되고 Win10에서는
    `System.ArgumentException`이 났다(MS marb2000: "It should be working on Windows10 too...
    looks like there is a bug in Windows 10 ... preventing the IPrintManagerInterop solution
    from working. We're investigating servicing the fix downlevel"). 수리 = **KB5023773
    (2023-03-21 프리뷰, OS 빌드 19042.2788/19044.2788/19045.2788)** — "This update affects
    applications that use the Windows UI Library in the Windows App SDK (WinUI 3). It makes
    printing possible..." 이후 Win10 19045.2846에서 동작 확인 코멘트(2023-05-08).
    https://support.microsoft.com/en-us/topic/march-21-2023-kb5023773-os-builds-19042-2788-19044-2788-and-19045-2788-preview-5850ac11-dd43-4550-89ec-9e63353fef23
  - **KOTU 함의**: 현행 WASDK 1.8.*(KOTU.App.csproj:44)는 1.1.1을 한참 지난 버전이라 SDK 쪽
    조건은 충족. 남는 변수는 **구형 Win10 빌드**다 — KOTU의 TargetPlatformMinVersion은
    10.0.17763(1809, KOTU.App.csproj:5)인데 KB5023773은 19042+ 대상이라, **1809 LTSC 등
    19042 미만(또는 2023-03 이후 누적 업데이트 미적용) 빌드에서는 ShowPrintUIForWindowAsync가
    예외를 던진다고 봐야 한다(추정 — 해당 빌드 실기기 부재)**. 공식 예제 자체가
    `PrintManager.IsSupported()` 선확인 + try/catch(ContentDialog 안내)를 권고하므로 그대로
    복제하면 안전하게 닫힌다. `IsSupported`는 1607(14393)부터 있어 min OS에서 안전
    (https://learn.microsoft.com/en-us/uwp/api/windows.graphics.printing.printmanager 의 버전 표).
- **unpackaged 성립 여부**: 성립 **추정(강)**. 근거 — ① 인터롭 API의 존재 목적 자체가 HWND
  창(비 UWP) 지원(MS asklar, #5831: "These interop APIs typically exist to allow usage with
  HWND windowing, so it is expected to work in an unpackaged WinUI 3 scenario" — 당시 실패를
  버그로 취급) ② `Windows.Graphics.Printing`은 OS 내장 WinRT이고 MS의 "패키지 정체성이
  필요한 기능" 목록(알림·패키지 확장·활성화 정보)에 인쇄가 없다
  (https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/modernize-packaged-apps)
  ③ 1.1.1+ "works and downlevel" 발언과 이후 문서·커뮤니티 확인에 패키징 단서가 없다.
  다만 "unpackaged + self-contained에서 됐다"는 명시 실증 문헌은 못 찾았다 — **실기기 확인
  포인트 1순위**로 지정한다(§3-4).
- **수명 규칙(문서 명시)**: 인쇄 가능한 화면마다 등록하고 떠날 때 해제해야 하며, 해제 없이
  재등록하면 예외가 난다("If you have a multiple-page app and don't disconnect printing, an
  exception is thrown"). `PrintTaskRequested` 핸들러는 UI 스레드 밖에서 올 수 있어 공식
  예제가 UI 갱신을 `DispatcherQueue.TryEnqueue`로 넘긴다 — KOTU는 창(MainWindow)당 1회
  등록·모듈 전환과 무관한 셸 소유 서비스로 두면 이 함정 자체가 사라진다(§3).

### 1-ⓑ Microsoft.UI.Xaml.Printing.PrintDocument — **존재·지원, XAML 요소 인쇄 성립**

- **결론: WASDK API 표면에 정식으로 있고(1.8 문서 뷰 존재), ⓐ와 한 몸으로 쓰는 페이지 공급
  객체다.** `PrintDocument.DocumentSource`를 `PrintTaskSourceRequestedArgs.SetSource()`에
  꽂고, 3개 이벤트로 페이지를 공급한다: `Paginate`(페이지 구성 + `SetPreviewPageCount`) →
  `GetPreviewPage`(프리뷰 n페이지 요청 시 `SetPreviewPage(n, UIElement)`) →
  `AddPages`(인쇄 확정 시 `AddPage(UIElement)` 반복 + `AddPagesComplete()`).
  - API: https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.printing.printdocument
    (뷰 셀렉터에 windows-app-sdk-1.2 ~ 2.0 존재 — 1.8 포함)
  - **페이지 = 임의 XAML UIElement**다. 공식 예제가 코드로 만든 StackPanel(+Image·TextBlock)을
    시각 트리에 붙이지 않고 그대로 페이지로 쓴다 — "XAML 시각 트리 인쇄"는 화면 요소를 직접
    꽂는 방식이 아니라 **인쇄 전용 요소를 새로 조립**하는 방식이 정석이고, 이는 KOTU의
    "요소 부모 1개 제약 — 사용처마다 새 인스턴스"(v0.174.1 교훈, CLAUDE.md §3) 방침과 정확히
    일치한다. 페이지 크기는 `PrintTaskOptions.GetPageDescription()`의 `PageSize`(96DPI DIP)·
    `ImageableRect`(인쇄 가능 영역)로 잡는다.
  - "초기 WASDK 미지원 이력"의 실체: 클래스는 처음부터 있었지만 ⓐ의 PrintManager 계열이
    데스크톱에서 죽어 있어(위 0x80040155/0x80070578) **파이프 전체가 2022년(1.1.1)까지 무용**
    이었다 — 별도의 PrintDocument 전용 결함 이력은 못 찾았다.
  - 실무 실증: microsoft-ui-xaml#4419에 동작 스크린샷·샘플(2022-04, castorix — WASDK
    1.1.0-preview1/.NET 6)과 Win10 22H2 동작 확인(2023-05)이 있다. 같은 스레드의 MS 조언
    (MikeHillberg): 페이지가 많으면 `AddPage`를 한 장씩 넘기고 넘긴 요소 참조는 버려도 된다
    — 대용량 PDF 메모리 관리의 근거. https://github.com/microsoft/microsoft-ui-xaml/issues/4419
  - 남은 잡음: 특정 환경 실패 보고가 드물게 있다(예: 도메인 가입 PC에서만 예외 — #4419
    2023-07 코멘트, 원인 불명). 예외 캐치 + 안내 다이얼로그(공식 패턴)로 흡수한다.
  - 프리뷰 화면(모던 인쇄 대화상자의 미리보기)은 OS가 그린다 — 자체 프리뷰 UI를 만들 필요
    없음. 페이지 범위·매수·양면 등 표준 옵션도 대화상자 몫이고, 앱 커스텀 옵션이 필요하면
    `PrintTaskOptionDetails`가 있다(이번 범위에선 불요 추정):
    https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/customize-the-print-preview-ui

### 1-ⓒ 대안 축 — 성립 조건·리스크 (전부 채택 안 함, 이유 포함)

- **Windows.Data.Pdf (렌더만)**: 인쇄 API가 아니다. 인쇄 파이프에 잇는 법 = 페이지를
  `RenderToStreamAsync`로 비트맵 렌더 → `BitmapImage` → `Image` 요소 → ⓑ의 `AddPage`.
  KOTU가 PdfPane에서 이미 쓰는 렌더 관용구(PdfPane.xaml.cs:180-193) 그대로이고, MS Q&A의
  MSFT 재현 코드도 동일 패턴(UWP)이다. 즉 ⓒ가 아니라 **ⓐ+ⓑ 위의 PDF 페이지 공급원**으로
  편입된다. 품질은 렌더 폭이 결정 — 화면용(뷰포트 폭×배율)이 아니라 **인쇄 DPI 기준**
  (예: PageSize인치 × 300)으로 새로 렌더해야 한다.
- **Win2D / Direct2D 인쇄**: WinUI 3용 Win2D(`Microsoft.Graphics.Win2D`)에
  `Microsoft.Graphics.Canvas.Printing.CanvasPrintDocument`가 존재한다(SetSource에 ⓑ 대신
  꽂는 대체 소스; 페이지를 Direct2D 드로잉으로 그린다).
  https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Printing_CanvasPrintDocument.htm
  성립하지만 — 신규 패키지 의존 + 텍스트·레이아웃을 전부 드로잉 명령으로 재작성(마크다운
  렌더 재사용 불가). 3종 요구에 과잉이라 채택 안 함(벡터 정밀도가 필요해지면 후일 낙수).
- **System.Drawing.Printing (GDI, WinForms 계열)**: net8.0-windows에서 사용 가능 —
  `PrintDocument`(System.Drawing.Printing)는 KOTU가 **이미 참조 중**인 System.Drawing.Common
  패키지에 들어 있다(KOTU.App.csproj:52, 트레이 아이콘 A18이 GDI+ 사용 —
  TrayStatusIcon.cs:139-). Windows 전용 제약은 KOTU에 무의미.
  https://learn.microsoft.com/en-us/dotnet/api/system.drawing.printing.printdocument /
  https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/6.0/system-drawing-common-windows-only
  결정적 결격: **인쇄 대화상자·미리보기가 없다** — PrintDialog/PrintPreviewDialog는
  System.Windows.Forms 소속이라 UseWindowsForms(프레임워크 통째 편입)가 필요하고, 없이 쓰면
  기본 프린터로의 무대화상자 인쇄 + 자작 프린터 선택 UI가 된다. 페이지네이션·여백·미리보기
  전부 수제. **주 축으로 부적격, 구형 Win10(ⓐ의 KB 미적용 빌드) 폴백 후보로만** 기록해 둔다
  (폴백을 실제 구현할지는 §4 질문 5).
- **OS 기본 앱 위임(ShellExecute "print" 동사 / mshtml)**: **KOTU에서는 구조적으로 부적격.**
  print 동사는 레지스트리 연결에 의존하는데(MS Q&A 답변(Castorix31): ""print" or "printto"
  verbs ... it depends on registry associations" —
  https://learn.microsoft.com/en-us/answers/questions/607144/printing-a-file-in-windows-app-sdk-(tested-printin ),
  KOTU 자신이 확장자 기본 앱을 **KOTU ProgId(UserChoice)로 지정하는 기능**을 갖고 있고
  (ExplorerIntegration.cs:261-283), 그 ProgId에는 `shell\open\command`**만** 등록한다
  (ExplorerIntegration.cs:392-393 — print 동사 없음). 즉 KOTU를 기본 앱으로 쓰는 사용자일수록
  print 동사 해석이 깨질 확률이 높다(동사 병합이 SystemFileAssociations 쪽에서 살려 주는
  파일형도 있을 수 있으나 형별·OS별로 갈린다 — 추정). 대화상자 없는 조용한 인쇄라 UX 통제도
  없다. mshtml(PrintHTML) 핵은 레거시 비지원 경로다. 채택 안 함.
  (낙수 등재 후보: 역으로 KOTU ProgId에 print 동사를 달아 "탐색기 우클릭 인쇄"를 KOTU로
  받는 것 — A축 구현 후에만 의미 있음.)

## 2. 저장소 현행 자산과의 접점

- **창 HWND**: ⓐ의 `GetForWindow(hWnd)`에 넣을 핸들 관용구가 이미 2형 있다 —
  ① 뷰(UserControl)에서: `Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId)`
  (DocumentView.xaml.cs:1675-1680 · ArchiveView.xaml.cs:646-650 · SettingsView.xaml.cs:1366-1370
  — A48 실사용은 SettingsView.xaml.cs:745·797) ② 창에서:
  `WinRT.Interop.WindowNative.GetWindowHandle(this)`(MainWindow.xaml.cs:471-472 등, 공식 인쇄
  예제와 동일 형). 인쇄 등록을 셸(창) 소유로 두면 ②, 모듈 뷰에서 직접 하면 ①.
- **PDF (PdfPane)**: 문서 핸들·렌더 전 과정이 재사용된다 — `PdfDocument.LoadFromFileAsync`
  (+암호 재시도, PdfPane.xaml.cs:138-151), 페이지 렌더 `page.RenderToStreamAsync(stream,
  new PdfPageRenderOptions { DestinationWidth = ... })` → `BitmapImage.SetSourceAsync`
  (PdfPane.xaml.cs:180-193). 최소 경로: PdfPane에 인쇄용 렌더 접근자(열린 `_doc` 재사용 —
  암호 PDF 재입력 불요, 페이지 번호·목표 폭을 받아 스트림 반환)를 추가하고, Paginate에서는
  페이지 수만 확정 + GetPreviewPage/AddPages에서 **요청 페이지만 지연 렌더**(PdfPane의
  ListView 가상화와 같은 철학, 대용량 메모리 = AddPage 한 장씩·참조 즉시 폐기 — §1-ⓑ MS 조언).
- **이미지 (ImageViewerView)**: 표시용 디코드 결과가 그대로 인쇄 소스다 — `ReadImageFile`이
  파일 bytes + WIC 메타(해상도·EXIF 회전)를 돌려주고(ImageViewerView.xaml.cs:584-609), 표시는
  bytes → `BitmapImage`. 회전은 `RotationTransform.Angle = (_exifRotation + _userRotation) % 360`
  (ApplyRotation, :790-794)이 단일 원본이고, 세로/가로 축 맞바꿈 판정은 `RotationSwapsAxes`
  (:801). 최소 경로: 인쇄 페이지 = 새 `Image`(+`RotateTransform` 같은 각도) 1장을
  ImageableRect 안에 contain 배치(맞춤 계산에 RotationSwapsAxes로 축 교환 반영). Magick 경로
  (psd)도 표시 시점엔 png bytes로 합류(:562-564)하므로 같은 파이프를 탄다.
- **문서 텍스트/마크다운 (DocumentView)**: 텍스트 원본 = `EditorBox`(TextBox,
  DocumentView.xaml:49-64). 마크다운 렌더는 `MarkdownParser`(워커) 모델 →
  `MarkdownRenderer.AppendRange(Panel, blocks, start, count)`가 **대상 패널에 블록당 요소를
  쌓는 구조**(MarkdownRenderer.cs:43-47)라, 대상 패널을 "인쇄 페이지 패널"로 바꿔 부르면
  재사용된다. 신규는 **페이지 팩킹**(요소를 Measure해서 페이지 높이 넘치면 다음 페이지) —
  텍스트도 같은 루프에 태울 수 있다(줄 단위 TextBlock 또는 문자 수 근사 + 측정 이진 탐색).
  마크다운 실현성 판정 = **가능**(블록 = 독립 UIElement라 팩킹 단위가 자연스럽다); 페이지보다
  큰 단일 블록(긴 코드 블록)의 분할 규칙만 결정 필요(§4 질문 3) — 불가 시 폴백은 등재문대로
  원문 텍스트 경로.
- **진입점·키**: Ctrl 조합 현황 — 앱에 남은 Ctrl 조합은 Ctrl+S 하나(MainWindow.xaml.cs:803-804,
  A86-keymap.md:117)라 **Ctrl+P는 비어 있다**. 관용구 = DocumentView의
  `<KeyboardAccelerator Key="S" Modifiers="Control">`(DocumentView.xaml:15-17). P 단독 키는
  Hardware 모듈이 사용 중(HardwareView.xaml.cs:1583)이나 Ctrl 조합과는 무충돌. 버튼은 모듈
  하단 바 + HotkeySupport 관용구가 그대로 쓰인다. 다중 창(Shift+N)은 창마다 ⓐ 등록이 필요
  — 셸 소유 서비스로 창 수명에 묶는다.

## 3. 권고안

### 3-1. 축 택일 비교표

| 축 | 구현 비용 | 리스크 | 인쇄 품질 | 페이지네이션 | 판정 |
|---|---|---|---|---|---|
| **A. WinRT 인쇄**(PrintManagerInterop + Microsoft.UI.Xaml.Printing.PrintDocument) | 중 — 공통 기반 1회 + 모듈별 페이지 공급 | 구형 Win10(19042.2788 미만) 예외(캐치로 흡수) · unpackaged 실증 문헌 부재(실기기 1순위) · 선례 0 API군 | OS 모던 대화상자 + 미리보기, XAML 텍스트는 벡터, PDF/이미지는 렌더 DPI가 결정 | PrintDocument 이벤트 모델이 담당(우리는 페이지 요소만 공급) | **채택** |
| B. Win2D CanvasPrintDocument | 대 — 신규 패키지 + 드로잉 전면 신규 | 의존성 추가 · 마크다운 렌더 재사용 불가 | 벡터·픽셀 통제 최상 | 자체(SetPageCount) | 기각(과잉) |
| C. System.Drawing.Printing(GDI) | 중 — 단 대화상자·미리보기 자작 또는 포기 | 모던 UX 부재 · WinForms 편입 압력 | 텍스트/이미지 무난, 마크다운 수제 재현 | 전부 수제 | 기각(폴백 후보로만 기록) |
| D. OS 위임(print 동사/mshtml) | 소 | KOTU 자신의 기본 앱 등록이 동사를 깨뜨림(§1-ⓒ) · UX 통제 0 | 대상 앱 나름 | 없음 | 기각 |

**핵심 판정: A축 단독 채택.** 셸에 창당 1개 "PrintHost"(등록/해제 수명 + PrintDocument 이벤트
배선 + IsSupported/예외 안내 다이얼로그)를 두고, 활성 모듈이 "페이지 공급자"(페이지 수 +
n페이지 요소 팩토리)를 꽂는 구조. 인쇄 대상 3모듈만 공급자를 구현하고, 미구현 모듈에서는
진입점을 숨기거나 비활성화한다.

### 3-2. 모듈 3종 권장 경로 (요약)

- **이미지**: 1페이지 — 새 Image + RotateTransform(총회전 재사용), ImageableRect 안 contain
  (RotationSwapsAxes로 축 교환). 비용 소.
- **PDF**: 페이지 수 = `_doc.PageCount`, 페이지 요소 = 인쇄 DPI로 지연 렌더한 비트맵 Image.
  OS 대화상자의 표준 페이지 범위 옵션 활성(PrintTask.Options) + AddPage 한 장씩. 비용 중.
- **문서**: 공용 "블록 팩킹 페이지네이터"(측정 기반) 위에 — 텍스트 = 원문을 줄/청크
  TextBlock으로, 마크다운(렌더 모드) = MarkdownRenderer 블록으로. 마크다운 조립 실패·비상
  경로는 원문 텍스트 폴백(EnterRenderMode의 폴백 계약과 동형). 비용 중~대(팩킹 신규).

### 3-3. 배치 분할 제안 (A11 선례 — 공통 기반 → 모듈별 직렬, 권장 모델 병기)

1. **[배치 1 · Fable] 공통 기반 PrintHost**: 인터롭 등록/해제 수명(창당 1회·다중 창), ⓑ 이벤트
   배선, IsSupported/try-catch 안내, Ctrl+P 액셀러레이터 + 하단 바 진입점 훅(뒤 배치가 얹힐
   훅 명시), 스모크용 단일 TextBlock 페이지. **선례 0 API 전부가 여기 몰린다** — CI 1순위.
2. **[배치 2 · Opus] 이미지 모듈**: 사양이 §3-2로 닫히는 기계적 구현(1페이지·회전·contain).
3. **[배치 3 · Fable] PDF 모듈**: 지연 렌더·페이지 범위·대용량 메모리 — 상태·수명 판단 필요.
4. **[배치 4 · Fable] 문서 텍스트**: 측정 기반 페이지네이터 확립(성능·경계 규칙 설계 포함).
5. **[배치 5 · Opus] 마크다운**: 배치 4의 팩킹 루프에 MarkdownRenderer 블록을 태우는 적용
   + 원문 폴백 배선(사양은 배치 4가 확정).

### 3-4. CI 1순위 위험 후보 / 실기기 확인 포인트

- **CI 1순위 위험 후보(선례 0 API 전부 — 배치 1 보고 의무)**:
  `Windows.Graphics.Printing.*`(PrintManagerInterop·PrintManager·PrintTask·PrintTaskOptions·
  PrintPageDescription), `Microsoft.UI.Xaml.Printing.*`(PrintDocument·이벤트 args·
  PreviewPageCountType), `IPrintDocumentSource`. 전부 문서 실재 확인됐고 TFM/WASDK 1.8 표면에
  있으나 저장소 선례 0 — 최소 복구법 = 해당 using·호출부를 조건 제거해도 앱이 서도록 배치 1을
  독립 파일 + 진입점 한 곳으로 구성(등재문 규칙대로 보고서에 복구 diff 요지 첨부).
- **실기기 확인 포인트(인쇄는 CI 검증 불가 — Microsoft Print to PDF 가상 프린터로 1차)**:
  ① unpackaged+self-contained에서 ShowPrintUIForWindowAsync 대화상자 표시(§1-ⓐ 추정의 실증)
  ② 미리보기에 페이지가 실제로 그려지는가(빈 프리뷰 부류) ③ 인쇄물 품질 — PDF/이미지 렌더
  DPI 체감, 텍스트 폰트·여백 ④ 다중 창 각각 인쇄 ⑤ 프린터 0대·인쇄 스풀러 중지 상태의 예외
  안내 ⑥ (가능하면) 19042.2788 미만 Win10에서 안내 다이얼로그로 안전하게 닫히는가
  ⑦ 암호 PDF 인쇄(열린 문서 재사용 — 재입력 없어야 함) ⑧ 대용량 PDF(수백 페이지) 메모리.

## 4. 확인 질문 문안 (부록 B 등재용)

1. **[A211] 인쇄 대화상자** — OS 표준(모던 인쇄 UI + 내장 미리보기) 고정으로 확정하는가?
   (자체 프리뷰 화면·자체 옵션 UI는 만들지 않는 전제. 커스텀 옵션이 필요해지면
   PrintTaskOptionDetails로 후일 확장.)
2. **[A211] Ctrl+P 키 배정** — Ctrl+P(3모듈 한정, DocumentView Ctrl+S 액셀러레이터 관용구)로
   확정하는가? 하단 바 인쇄 버튼을 병행 배치하는가(A86-keymap.md에 Ctrl+P 행 추가 필요)?
3. **[A211] 텍스트 인쇄 기본값** — 폰트(제안: 에디터와 같은 기본 글꼴·14px 상당 → 프린터
   DIP 그대로) / 여백(제안: ImageableRect + 소여백 고정, 사용자 옵션 없음) / 머리글·바닥글
   (제안: 없음 — 페이지 번호도 생략)? 마크다운의 페이지보다 큰 코드 블록은 잘라 넘기는
   분할을 허용하는가(제안: 허용 — 줄 단위 분할)?
4. **[A211] 이미지 인쇄 배치** — 1장 = 페이지 contain(여백 안 최대) 고정 제안. 원본 크기
   (DPI 반영)나 페이지 채움 같은 배치 옵션이 필요한가(제안: 불요 — 낙수)?
5. **[A211] 구형 Win10 폴백** — KB5023773 미적용 빌드(1809 LTSC 등)에서 ⓐ가 예외일 때
   안내 다이얼로그로 종료(제안)인가, GDI(System.Drawing.Printing) 무대화상자 폴백까지
   구현하는가(비권장 — §1-ⓒ)?

## 부록: 등재문·과제 전제와 코드의 어긋남

- 과제문 "GetHwnd — A48 실사용 :1189 부근"은 낡은 줄 번호 — 현행 A48 사용부는
  SettingsView.xaml.cs:745·797, GetHwnd 정의는 :1366-1370(내용 어긋남 없음, 위치만 이동).
- 등재문 "이미지 = 회전 반영 — A191 `RotationSwapsAxes` 재사용": RotationSwapsAxes는 축
  교환 **판정 bool**이고, 회전 각 자체의 단일 원본은 `(_exifRotation + _userRotation) % 360`
  (ApplyRotation, ImageViewerView.xaml.cs:792)이다. 인쇄에는 둘 다(각도 = 페이지 요소 변환,
  bool = 맞춤 축) 재사용한다 — 용어만 보정.
- A211 등재 위치가 REQUIREMENTS.md "## 4. 영상 모듈" 절 안이다(:1074-1090) — 내용은 3모듈
  횡단이라 절 배치가 어긋남(재배치는 A번호 규칙과 무관한 본문 정리 사항).
