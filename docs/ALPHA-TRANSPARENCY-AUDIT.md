# 알파·투명도 코드 감사 — 2026-10-23

사용자 요청: 전체 코드에서 알파값에 영향을 주는 위치와 값을 정리한다. 조사 시작 소스는
`14d0c0a` / v0.400.0이며 A396 문구 제거와 A409 편집기 배경 보강을 별도로 검증한다.
알파값을 일괄 변경하라는 요청으로 해석하지 않았다.

## 범위와 읽는 법

- Git 추적 코드·설정·XAML·웹·SVG·패키징 스크립트 **351개 파일**을 검색했다.
  직접 Opacity/ARGB/Transparent뿐 아니라 상태 스타일, 테마 브러시, 애니메이션,
  이미지 채널·마스크·합성과 CSS color-mix/gradient도 대조했다.
- 빌드 산출물·외부 패키지 전체·과거 문서의 값은 제품 조정점으로 세지 않는다.
  단, 실제 설치된 WinUI1.8.260803003의 기본 TextBox 템플릿은 A409 원인 확인을 위해 읽었다.
  사용자가 여는 이미지/PDF/HTML 자체의 투명도와 바이너리 자산 내부의 모든 픽셀은 수치 전수조사 대상이 아니다.
- `Opacity=0`은 완전 투명, `1`은 불투명이다. XAML `#AARRGGBB`의 앞 두 자리가 알파다.
  `FromArgb(A,R,G,B)`의 첫 값과 Python RGBA 튜플의 마지막 값이 알파이며 범위는 0~255다.
  **GDI `FromArgb(R,G,B)` 세 인자에는 알파가 없고 불투명**이다.
- 브러시 알파 × 요소 Opacity × 조상 Opacity가 겹친다. 예: 80% 안내판을 1→0으로 페이드하면
  배경은 80%→0%, 글자는 100%→0%가 된다. 글자를 보존하려면 배경 브러시/별도 배경판을 바꿔야 한다.
- 아래 경로는 저장소 기준이다. 세부 행·코드 목록은 함께 저장한
  [ALPHA-TRANSPARENCY-LOCATIONS.csv](ALPHA-TRANSPARENCY-LOCATIONS.csv)를 참조한다.
  CSV는 감사 후보 전체를 남겨 주석/호출/시험값도 포함하며, 아래 표가 실제 의미를 분류한 결과다.

## 1. 문서 편집기와 렌더러

| 위치 | 현재 값 | 영향 |
|---|---|---|
| `src/KOTU.Module.Document/EditorDecor.cs` GuideOpacity | **0.14** | 본문 줄 가이드 |
| 같은 파일 MarkerOpacity | **0.25** | 문단·EOF 마커 |
| 같은 파일 GutterOpacity | **0.4** | 줄 번호 |
| 같은 파일 진단판 | **#B0000000, 69.02%** | 성능 진단을 켰을 때만 검은 배경판 |
| `DocumentView.xaml` EditorBox | A409: **다크 Transparent / 라이트 White / 고대비 SystemColorButtonFaceColor** | 본문 내부의 Normal/PointerOver/Focused/Disabled 네 배경을 같은 값으로 유지 |
| `DocumentView.xaml` RootGrid | **Transparent** | 휠·빈 영역 히트 테스트를 성립시킨다. `null`과 의미가 다르다 |
| `DocumentView.xaml` Placeholder | **0.6** | 빈 문서 안내 |
| 같은 파일 CappedNoticeText, ViewModeText, 변경 수, PageInfoText | **0.8** | 제한 안내·편집 모드·변경 수·페이지 정보 글자 |
| `OfficeTextView.xaml` Notice / RootGrid | **0.75 / Transparent** | Office 읽기 안내 / 히트 테스트 |
| `MarkdownRenderer.cs` 표 테두리 | **0x80=50.20%** | 회색(128,128,128) 선 |
| 같은 파일 표 머리·코드 블록 배경 | **0x22=13.33%** | 회색 면 |
| 같은 파일 인용선·가로 구분선 | **0x60=37.65%** | 회색 선 |
| 같은 파일 인라인 코드 강조 | **0x2E=18.04%** | TextHighlighter 배경 |
| 같은 파일 인쇄용 검정 브러시 | **0xFF=100%** | 인쇄용 글자. 화면 투명도 조정점과 구분 |
| `HtmlPane.cs` WebView2 기본 배경 | **Transparent** | HTML이 배경을 그리지 않는 영역. 문서 HTML/CSS의 자체 색상은 별개 |
| `DocumentView.xaml` SelectionHighlightColorWhenNotFocused | **TextControlSelectionHighlightColor 테마 브러시** | 활성 선택과 같은 강조색. A409 보강으로 검색 상태에 따른 투명 브러시 복원을 없애 일반 선택도 유지한다 |

### A409 원인: 기본 상태 스타일이 Background를 덮어씀

`Background="Transparent"`만으로는 TextBox의 `PointerOver`, `Focused`, `Disabled` 상태를 고정하지 못한다.
패키지 `Microsoft.WinUI/Themes/generic.xaml`의 `DefaultTextBoxStyle`에서 `BorderElement.Background`에
각 `TextControlBackground*` ThemeResource를 적용한다. 기존 v0.401.0 검증 실행본에서도 검색 입력에
포커스를 둔 채 포인터만 본문으로 이동했을 때 검정→짙은 회색 변화가 재현됐다.

| WinUI 기본 키 | 다크 테마 | 라이트 테마 |
|---|---|---|
| TextControlBackgroundPointerOver → ControlFillColorSecondary | **#15FFFFFF (8.24% 흰색)** | **#80F9F9F9 (50.20%)** |
| TextControlBackgroundFocused → ControlFillColorInputActive | **#B31E1E1E (70.20%)** | **#FFFFFFFF (100%)** |
| TextControlBackgroundDisabled | ControlFillColorDisabledBrush | ControlFillColorDisabledBrush |

고대비는 별도의 SystemControlBackground 자원을 사용한다. A409는 EditorBox.Resources 안에만
Default/Light/HighContrast 사전을 둔다. 다크는 기존 비활성 검정 바탕을 유지하도록 Transparent,
라이트는 검은 글자가 투명 바탕에 묻히지 않도록 기존 focused와 같은 White로 통일한다.
고대비는 기존 TextBox의 ButtonText/ButtonFace 짝을 유지한다. WindowColor만 따로 지정하면
사용자 지정 고대비 팔레트에서 ButtonText와 대비가 깨질 수 있다. 다른 TextBox의 hover는 유지한다.

실제 다크 화면에서 수정 전후 포인터 진입을 대조했고, 격리된 실제 컨트롤/테마 자원 시험은
다크→라이트→다크의 네 상태12표본·93/93을 통과했다. 편집기 배경은 다크 #00FFFFFF,
라이트 #FFFFFFFF로 일정했고 FindBox는 기본 TextBox와 일치했다. 고대비는 정적 자원 감사만
완료했으며 실제 OS 고대비 실행 검증은 하지 않았다. 정본: `A409-EDITOR-BACKGROUND.md`.

같은 날 추가 제보된 검색 후 일반 선택 강조 누락은 별도 원인이었다. A396 검색 갱신이 비일치
선택의 비활성 강조색을 투명으로 복원하면서 실제 포커스가 있는 수동 선택도 사라졌다.
v401과 배경만 수정한 v402에서 재현했고, 상태별 대입을 제거한 뒤 드래그·Shift·Ctrl+A를 실제
다크 화면에서 확인했다. 선택색은 하드코딩하지 않고 같은 테마 키를 사용한다. 확장 테마/선택
계약 검증134/134 통과; 위치 CSV는 변경된 행을 재대조한562개 후보 행이다.

## 2. 탐색기·패널·알림

| 위치 | 현재 값 | 영향 |
|---|---|---|
| `src/KOTU.App/MainWindow.xaml.cs` S4TranslucentOpacity | **0.65** | S4 중앙 배경·하단 덮개·떠 있는 좌우 패널 배경의 공통 조정점 |
| 같은 파일 ApplyOverlayStates | **S4 떠 있음:0.65, 일반:1.0** | 아래 SetBackdropOpacity 두 경로에 전달 |
| `Overlays/FileListOverlay.xaml.cs`, `ContentInfoOverlay.xaml.cs` SetBackdropOpacity | 전달값 | 배경판만 변경. 목록·정보 글자까지 흐려지지 않는다 |
| 두 오버레이 XAML + `Overlays/SidePanelHost.cs` 고정 상태 안내판 | **#CC202020 (80%)** | 3곳의 안내판 배경. 일관성 유지 시 함께 조정 |
| 위 세 구현 HintOpacity·페이드 | **1→0**, 다시 표시 시 **1** | 안내판과 글자가 함께 사라짐. XAML 초기 Opacity와 상수가 일치해야 한다 |
| `ExplorerFileOps.cs` CutOpacity | **0.5**, 일반 **1.0** | 잘라낸 항목을 반투명 표시 |
| `ExplorerEntryVm.cs` ContentOpacity → `ExplorerPane.xaml` 및 `Controls/ThumbnailExplorer.xaml` | 초기 **1.0**, 위 CutOpacity 반영 | 리스트·타일 공유. 두 표면을 각각 별도 값으로 바꾸지 않는다 |
| `ExplorerPane.xaml` 경로 요약 / 상세·빈 상태 | **0.75 / 0.6** | 보조 글자 |
| `ExplorerDialogs.cs`, `ExplorerConflictDialog.cs` | **0.6** | 대화상자 설명·상세 글자 |
| `Controls/ThumbnailExplorer.xaml` 작은 표식 / 빈 안내 / 상태 | **0.55 / 0.6 / 0.8** | 타일 보조 표시 |
| `Controls/ThumbnailExplorer.xaml.cs` 대기 배지 | **0.55** | 아직 로드되지 않은 썸네일 표시 |
| 같은 파일 진단 글자 / 진단판 | **0.9 / #B0000000 (69.02%)** | 썸네일 진단을 켰을 때만 |
| `Controls/DriveStrip.xaml.cs` 글자 / 구분선 | **0.6 / 0.5** | 드라이브 상태 스트립 |
| `Overlays/ContentInfoOverlay.xaml.cs` 보조 글자 | **0.65** | 정보 패널 부가 문구 |
| `MainWindow.xaml` 빈 상태 | **0.6** | 시작·빈 콘텐츠 안내 |
| 같은 파일 EscapeFocusSink | **0** | 1×1 포커스용 요소를 숨김. 기능상 투명이며 장식 조정 대상이 아님 |
| 같은 파일 DiagStrip / NavDiagStrip | **0.85 / 0.9** | 진단판 전체에 적용 |
| `MainWindow.xaml.cs` SPONSOR 판 / 글자 | **#59000000 (34.90%) / 0.9** | 시작 메뉴 후원 이미지의 식별 라벨 |

Transparent 배경을 사용하는 탐색기 Root·항목 표면, 오버레이 PanelBorder와 드라이브 브러시 폴백은
히트 테스트·부모 배경 노출용이다. 화면 전체 알파를 바꾸는 설정이 아니다. `App.xaml`의 옛 Acrylic
언급은 삭제 이력 주석이며 현재 실행되는 아크릴 구현이 아니다. 현재 추적 제품 코드에서
창 전체 알파를 설정하는 SetLayeredWindowAttributes/DwmSetWindowAttribute/Mica 호출은 발견되지 않았다.

## 3. 영상·오디오·이미지·녹화·하드웨어·설정

| 위치 | 현재 값 | 영향 |
|---|---|---|
| `VideoPlayerView.xaml` 재생 안내판 2곳 | **#CC202020 (80%)** | 재생 시작/재개 안내 |
| `AudioPlayerView.xaml` 안내판 2곳 | **#CC202020 (80%)** | 오디오 안내 |
| 같은 파일 좌/우 VuTrack | **#26FFFFFF (14.90%)** | 레벨 미터의 비어 있는 트랙 |
| `ImageViewerView.xaml` 빈 안내 | **0.6** | 이미지 없음 문구 |
| `AllReadableView.xaml` 빈 안내 | **0.6** | 통합 뷰어 문구 |
| `ArchiveView.xaml` 헤더·빈 안내 / 결과 경로 | **0.6 / 0.7** | 압축 목록 보조 글자 |
| `ArchiveView.xaml.cs` 상태 글자 | **0.8** | 상태 표시 |
| `RecordView.cs` 소스 분류 글자 | **0.65** | 녹화 소스 타일 |
| 같은 파일 선택 타일 배경 | **accent 브러시 Opacity0.22**, 비선택 Transparent | 선택 상태 배경. 테두리의 강조 브러시는 별개 |
| 같은 파일 카드·버튼 배경/테두리 | **알파255 (100%)**, 다크/라이트 RGB 별도 | 밝기 차이는 RGB 차이 |
| `HardwareView.xaml` 관리자 안내 / 코드의 상태 설명 | **0.65** | 하드웨어 안내 |
| `HardwareView.xaml.cs` 보조 글자·축·단위 | **0.55** | 그래프 보조 정보 |
| 같은 파일 그래프 면 | **56/255=21.96%**, RGB는 채널 accent | 그래프 아래 채움 |
| 같은 파일 graph.Root | 측정 전 **0.45**, 값 수신 후 **1.0** | 그래프 전체. 면 알파와 곱해진다 |
| `SensorChannels.cs` CPU/GPU/RAM/FAN/SSD 색 | **255** | 기본 채널 색은 불투명 |
| `SettingsView.xaml.cs` 설명 글자 | 주로 **0.7**, 일부 **0.6** | 개별 설정 설명. 개별 행은 CSV에 전부 기재 |
| 같은 파일 상태/현재 버전 | **0.8** | 업데이트·상태 문구 |
| 같은 파일 latest/lastChecked/nextCheck | 업데이트 가능 **0.7**, 불가 **0.4** | 세 TextBlock의 공통 dim 변수 |

## 4. 아이콘과 실제 이미지 픽셀

| 위치 | 값 / 처리 | 영향 |
|---|---|---|
| `BrandIcons.cs` 표식 / 외곽선 | **235 (92.16%) / 70 (27.45%)** | 실행 중 생성하는 브랜드 아이콘 |
| `InstanceIcon.cs` LabelPlate, `TrayStatusIcon.cs` Plate | **#E0202024 (87.84%)** | 창·트레이 아이콘의 글자 대비판 |
| 위 파일들의 Format32bppArgb | 투명 초기 비트맵 + 안티앨리어싱 | 도형 밖 투명도·가장자리 픽셀. 단일 UI Opacity가 아님 |
| `Branding.cs` 모듈 accent 8종 | **0xFF** | 불투명 브랜드 색 |
| `TitleBarTheming.cs` Rgb | **255** 고정 | hover 색 #2B1454 / 기본 #15072E 등은 RGB 밝기 변화이며 alpha 변화 아님 |
| `InstanceIcon.cs` Lighten, `TrayStatusIcon.cs` Lighten/IdleColor | RGB를 흰색 쪽으로 보간 | 불투명 색상. alpha를 낮춰 밝아지는 구현이 아님 |
| `HardwareView.xaml.cs` ToArgb + `ITrayStatusProvider` | A 채널을 상위8비트에 포장 | 전달 형식. `TrayStatusIcon.LineColor`는 alpha를 버리고 글자를 불투명으로 생성 |
| `Controls/ThumbnailRaster.cs` | **BitmapAlphaMode.Premultiplied**, BGRA8 | 썸네일의 RGB에 alpha가 미리 곱해져 있다 |
| `ThumbnailExplorer.xaml.cs`, `.Retry.cs` PixelBuffer 쓰기 | 위 BGRA를 복사 | 여기서 다시 alpha를 곱하면 가장자리가 어두워진다 |
| `ImageClipboardSnapshot.cs` PNG | **BitmapAlphaMode.Straight** 디코드/인코드 | 원본 투명도 보존 |
| 같은 파일 DIBV5 | **32비트 BGRA**, alpha mask **0xFF000000** | 원본 alpha 바이트를 그대로 복사 |
| 같은 파일 구형 DIB | **24비트 BGR**, 흰색 합성 | `(color*alpha + 255*(255-alpha)+127)/255`. alpha 채널을 모르는 앱용 |

이미지 복사 alpha를 임의로 0으로 만들거나 Straight/Premultiplied를 서로 바꾸면 빈 이미지·검은 테두리
문제가 생길 수 있다. WhatsApp 대응 경로는 UI 배경 투명도와 독립이며 이번 감사에서 변경하지 않았다.

## 5. 패키징 이미지와 웹사이트

- `packaging/gen_app_icon.py`, `gen_file_icons.py`: 초기 RGBA **(0,0,0,0)**,
  타일/주 글자 **255**, 외곽선 **70**, 발바닥 표식 **235**, 작은 글자 **210(82.35%)**.
  실행 중 `BrandIcons.cs`와 공통 표식 값이 연결된다.
- `packaging/gen_brand_assets.py`: wordmark/mascot/logo의 alpha 마스크 임계값 **150**,
  spinner **90**. 마스크는0/255, 2회 MaxFilter(3)으로 가장자리를 보존하고 허용 영역은 원본 alpha,
  그 밖은0으로 쓴다. 임계값은 최종 불투명도가 아니라 남길 픽셀 판정값이다.
- `packaging/gen_splash.py`: RGBA mascot을 mask로 불투명 RGB 배경에 합성한다. 최종 splash에는 alpha 채널이 없다.
- `site/index.html`, `guide.html`, `guide.ko.html`: nav 배경 **.72→.9**, 모바일 **.98**;
  primary 버튼 그림자 **.65→.8**; ghost 버튼 배경 **.035→.09**, 테두리 **.16**;
  로고 그림자 **.5**, 코드 배경 **.06**, 보조 카드 **.017**, 모바일 구분선 **.05** 등.
- index 추가: 별 레이어 **.75**, 별 자체 alpha **.24~.74**(사인파), 제목 그림자 **.22**,
  왕관 그림자 **.4**, pill **.13/.28**, 카드 hover 테두리 **.2**·그림자 **.9**,
  카드 색상 혼합 **11%/22%/34%**, 취소선 **.55**, 엔진 hover **.4**, 라벨 **.32**,
  스크린샷 hover **.35**, 다운로드 카드 **.42/.07**, 바탕 무늬 **.05/.014**,
  스크롤 등장 **0→1**. mask-image의 transparent/black 경계로 배경을 점진적으로 지운다.
- guide 두 언어 추가: 링크 테두리 **.35**, 키캡 테두리 **.15**, 표 머리 **.02**.
- `site/assets/skyline.svg`: 요소 opacity·그라데이션 stop-opacity·겹친 창문 불빛이 별도다.
  개별 요소 값/행은 CSV에 모두 보존한다. CSS `#RRGGBBAA`는 XAML `#AARRGGBB`와 순서가 다르므로 혼용하지 않는다.
- 위 웹/패키징 값은 Windows 문서 편집기 hover 원인이 아니다. 테스트 파일의 alpha 값은 시험 입력·기대값이다.

## 6. 암묵적 테마 값과 감사 한계

App.xaml의 XamlControlsResources를 통해 버튼·입력·리스트·슬라이더·메뉴의 기본 상태 스타일도 들어온다.
직접 숫자를 쓰지 않아도 `TextFillColorPrimaryBrush`, `LayerFillColorDefaultBrush`,
`CardBackgroundFillColorDefaultBrush`, `CardStrokeColorDefaultBrush`, `DividerStrokeColorDefaultBrush`,
`ControlStrokeColorDefaultBrush`, `SystemControlBackgroundChromeMediumLowBrush`,
`AccentFillColorDefaultBrush`, `AccentTextFillColorPrimaryBrush`, `SystemControlHighlightAccentBrush`,
`SystemFillColorCautionBrush` 등이 색과 alpha를 공급한다. 전체 직접 참조 위치도 CSV에 포함한다.

테마/OS 강조색/고대비/컨트롤 상태에 따라 값이 달라지므로, 라이브러리 전체를 앱의 고정 숫자 목록으로
오인하면 안 된다. A409처럼 문제가 생긴 컨트롤에 국소 재정의하는 것이 범위를 통제하기 쉽다.
이 문서는 정적 코드 감사와 명시한 실제 재현을 구분한다. 모든 컨트롤·테마·배율의 픽셀을 전수 측정했다는 뜻은 아니다.

PDF 화면/인쇄(`PdfPane.xaml.cs`)는 PdfPageRenderOptions의 DestinationWidth만 지정하고 배경/알파를
재정의하지 않는다. 이미지 뷰어의 BitmapImage도 디코더가 읽은 파일 투명도를 사용한다.
이런 콘텐츠 자체·외부 렌더러의 alpha는 위 고정 UI 숫자와 별도로 취급한다.

## 7. A401 Enter 지연 상태

**v0.400.0은 1차 개선이며 전체 해결은 아니다.** 첫 요청부터 고정50ms 안의 레이아웃/스크롤
장식 요청을 합쳐 중복 계산을 줄였다. 약1MB 문서 Enter10에서 장식21→11회, 누적1.293→0.712초;
긴 한 줄256Ki에서는1.739→0.921초였다. 한 패스의 native 문자 사각형 계산은 여전히 비싸다
(수정 후 위 두 조건 최대 약78/96ms). 이 수치는 키 입력→화면 표시 지연 측정값이 아니다.
초기 긴 줄 레이아웃·고빈도 연속 스크롤·개별 계산 비용은 남아 있다.
정본: [A401-INPUT-SCROLL-COALESCING.md](A401-INPUT-SCROLL-COALESCING.md).
