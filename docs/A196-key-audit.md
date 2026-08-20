# A196 감사표 — F11·F12·Enter·Esc 화면 × 상태 전수 (A196+A201+A202 공유 매트릭스, 2026-08-20)

> **성격**: A135 감사(docs/A135-audit.md) 관용구의 후속 — 코드 시뮬레이션 감사의 단일 원본.
> 세 항목이 같은 키 매트릭스를 공유해 한 배치로 감사·수리했다:
> **A196**(F11/F12 가용 범위 확대 — 게이트 완화) · **A201**(Enter 무동작 전수 감사) ·
> **A202**(Esc 말단에 콘텐츠 닫기 층 — 파일 인자 실행 후 Esc = 기본 화면).
> **코드 행 번호는 이 배치 수리 후(v0.204.0 예정) 기준** — 이후 어긋나면 심볼명으로 재확인
> (`HasPanelContext`·`IsPanelFallbackView`·`OnShellEscape`·`TryCloseContent`·`OnShellEnter`·
> `OnOverlaySideKey`·`ApplyOverlayStates`·`ExplorerListing.AllFiles`).

## 범례·판정 기준

| 표기 | 뜻 |
| --- | --- |
| ✓ | 성립 — 코드 시뮬레이션으로 확정(도달 → 선소비 → 게이트 → 실행 전 단계 통과) |
| ✗→✓ | 이번 배치에서 수리된 실버그/공백(표 A) |
| 사양 | 의도된 무동작/양보(A86-keymap·A90·A119 확정 예외 — 수리 대상 아님) |
| 실기기 | 코드만으로 판별 불가 — 런타임(포커스 행방·IME·OS/키보드) 몫. §6 |

판정 파이프라인(전 키 공통 — A135 §범례의 현행판):

1. **도달**: `RootLayout.AddHandler(KeyDownEvent, …, handledEventsToo:true)`(MainWindow.xaml.cs:239)
   → `OnRootKeyDown`(:1734). XAML 트리 안 어디에 포커스가 있든 셸이 받는다. 도달이 끊기는 경우는
   ① 포커스가 팝업 트리(대화상자·플라이아웃) 안 — 통상 기대 동작 ② 포커스 없음(null) — 실기기
   (A135 §4-① 포커스 고아. 이번 배치가 압축 뷰·미지원 안내의 자기 포커스 공백을 수리해 후보를 줄였다).
2. **선소비 양보**: 각 분배부의 `e.Handled` 존중 — 먼저 소비한 쪽이 이긴다(이름변경 편집 상자,
   탐색기 표면 Enter, A202부터 잘라내기 표시 해제).
3. **키별 게이트**:
   - F11/F12 = `OnOverlaySideKey`(:1813 — 원 기능 양보 :1815, S4 무동작 소비 :1817, 컨텍스트
     소비 :1826) → `OnOverlaySideDown`(:1834 — `HasPanelContext` 게이트 :1841).
   - Enter = 텍스트 입력 통과(:1783) → `OnShellEnter`(:1879 — 오토리피트·Handled·통과 표면 :1883).
     Alt+Enter = `OnShellAltEnter`(:1893 — 양보 없는 직행, 텍스트 판정보다 앞 :1773).
   - Esc = `OnShellEscape`(:2122 — 텍스트 입력 판정보다 앞 :1738) 체인 ①전체화면 ②S4 ③콘텐츠 닫기.
4. **실행**: 패널 = `ApplyOverlayStates`(:2309 단일 종착점) / 전체화면 = `ToggleFullScreen` /
   닫기 = `TryCloseContent`(:2217 — ShowModule 빈 컨텍스트 재사용).

**게이트 정본(A196 수리 후)**: `HasPanelContext`(:1609) = 파일 열림 ∨ 빈 파일 모듈 ∨ 패널 제공 뷰
∨ **무제 문서(A189) ∨ 폴백 화면(`IsPanelFallbackView` :1600 = 설정·미지원 안내)**. false = **빈 셸뿐**.
4소비처(A119 통일)가 함께 움직인다: ① F11/F12 소비(:1826)·토글(:1841) ② 경계 버튼
(`UpdateEdgeButtons` :2468 · `OnRootPointerMoved` :2507) ③ 전이(`ApplyOverlayStates`의 fallback 축
:2317) ④ `CurrentShellState`(:127 — None = !HasPanelContext). S4 중 무동작 게이트는 별도 존치(사양).

## 표 1 — 화면 × F11/F12 (게이트 감사)

키 경로가 셸 한 곳이라 화면별 차이는 게이트 입력값(HasPanelContext 구성 요소)과 좌 리스트
필터·우 정보 내용뿐이다. "콘텐츠 열림"·"전체화면" 열은 A135 표 1 판정 그대로 유효(무변경).

| 화면 | 게이트(수리 전) | 게이트(수리 후) | 좌 리스트 / 우 정보 | 근거 |
| --- | --- | --- | --- | --- |
| 이미지·영상·오디오·압축·문서(텍스트/PDF)·AllReadable — 콘텐츠 열림 | ✓ 파일 열림 | ✓ | 파일 폴더 + 모듈 필터 / 파일 정보 | `_currentFilePath`(:1609) |
| 위 6종 — 빈 파일 모듈(S1) | ✓ 빈 모듈 | ✓ | 시작 폴더(A174) + 모듈 필터 / 플레이스홀더 | `IsEmptyFileModule`(:1582) |
| 문서 렌더 모드(A190) | ✓ (파일 열림 — 렌더는 하위 표시 모드일 뿐) | ✓ | 파일 폴더 + 문서 필터 / 파일 정보 | `_renderMode`는 셸 상태 밖(DocumentView.xaml.cs:654) |
| 문서 무제(A189) | **✗ 게이트 공백**(`_untitledContent` 미포함 — 등재문 확인) | **✗→✓ 편입** | 시작 폴더(A174) + **문서 모듈 필터**(빈 파일 모듈과 동일 취급 — 확정) / "No file open" | :1609 `_untitledContent` 항 + fallback 축(:2317) |
| H/W(정보 모듈) | ✓ 패널 제공 뷰(A119) | ✓ | 모듈 고유 패널(SidePanelHost) | `PanelProviderView`(:1592) |
| 설정 화면 | **✗ 게이트 제외**(A119 확정 예외였음) | **✗→✓ 편입**(확정) | 시작 폴더(A174) + **전체 파일 필터**(모듈 개념 부재 — `ExplorerListing.AllFiles`) / "No file open" | `IsPanelFallbackView`(:1600)·`ShowListOverlay`(:2564) |
| 미지원 파일 안내 | **✗ 게이트 제외** | **✗→✓ 편입**(확정 — 설정과 동일 취급) | 〃 | 〃 |
| 빈 셸(중앙에 아무 뷰도 없음 — 창 생성 직후 잠깐) | ✗ 제외 | 제외 유지(사양 — 띄울 표면이 없다) | — | :1600 `ModuleHost.Content is not null` |
| S4('오픈 파일' 탐색) 중 | 사양(무동작 소비 — Q5) | 존치(사양 — keymap 명기) | — | :1817·:1836 |

## 표 2 — 상태·포커스 × F11/F12 (화면 공통)

| 상태/포커스 | 판정 | 근거 |
| --- | --- | --- |
| 콘텐츠 열림·전체화면 | ✓ (A153 — 게이트·토글이 모드 무관) | A135 표 1 무변경 |
| 패널 안 포커스(리스트·트리·정보) | ✓ (F11/F12에 Handled 거는 코드 전수 0건 — A158 뒤 유지 재확인: 셸 상수 2줄뿐) | 이번 grep 재검(:1683-1684) |
| 에디터 편집 중 | ✓ (문자 비생성 — 텍스트 입력 판정보다 앞 :1758) | A118 확정 |
| 이름변경 편집 중 | ✓ (편집 상자는 Enter·Esc만 Handled — ExplorerRenameBox.cs:116-128) | 〃 |
| 대화상자·플라이아웃 열림 | 사양(팝업 트리 — 셸에 키가 안 온다) | A135 표 2 |
| S4 | 사양(무동작 소비) | keymap Q5 |
| 포커스 없음/고아 | 실기기 (§6-①) | A135 §4-① + 2차 방어 존치 |

## 표 3 — 화면 × Enter (A201)

Enter 실행부(`OnShellEnter`)에는 HasPanelContext 게이트가 **없다** — 무동작 원인은 게이트가 아니라
①원 기능 선소비(사양) ②텍스트 입력 통과(사양) ③탐색기 표면 양보(사양) ④키 미도달(포커스)뿐이다.

| 화면 × 포커스 | 기대 | 실제 코드 경로 | 판정 |
| --- | --- | --- | --- |
| 이미지·영상·오디오·H/W·AllReadable — 뷰 포커스 | 전체화면 토글 | :1879 → ToggleFullScreen (모듈 Enter 액셀러레이터 전수 0건 — Space·화살표뿐) | ✓ |
| 압축 — 뷰 포커스 | 전체화면 토글 | 경로는 동일하나 **압축 뷰만 IsTabStop·Loaded 자기 포커스가 없어**(7뷰 중 유일) 뷰 교체 직후 포커스 표류 → 셸 KeyDown 미발화 가능 | **✗→✓ 수리**(표 A-4) |
| 문서 텍스트 — 에디터 편집 중 | 줄바꿈(원 기능) | :1783 텍스트 입력 통과 → RichEditBox/TextBox 몫 | ✓ 사양 |
| 문서 텍스트 — 에디터 밖(뷰) 포커스 | 전체화면 토글 | :1879 | ✓ |
| 문서 PDF | 전체화면 토글 | PdfPane 리스트 = SelectionMode None·IsItemClickEnabled False(PdfPane.xaml:16-17 — Enter 무소비) → :1879 | ✓ |
| 문서 무제 — 에디터 편집 중 | 줄바꿈(원 기능. 전체화면은 Alt+Enter) | :1783 통과 | ✓ 사양 |
| 문서 렌더 모드(A190) | 전체화면 토글(렌더는 편집 불가 — 순환 대상 확인) | EnterRenderMode가 뷰 루트로 포커스 이동(DocumentView.xaml.cs:698) → 텍스트 입력 아님 → :1879 | ✓ (확정 — 수리 불요) |
| 설정 화면 | **전체화면 토글(대상 맞음 — 오케스트레이터 확정)** | SettingsView = IsTabStop + Loaded 자기 포커스(SettingsView.xaml.cs:74) → :1879. Alt+Enter도 :1893 직행 | ✓ (이미 성립 — 수리 불요. 개별 컨트롤 포커스 시 Enter는 그 컨트롤 원 기능 = 사양) |
| 미지원 파일 안내 | 전체화면 토글 | 구 TextBlock은 Control이 아니라 포커스 불가(포커스 행방 = 런타임) → **포커스 가능 래퍼 + 자기 포커스로 수리** | **✗→✓ 수리**(표 A-5) |
| 탐색기 표면(S1 중앙·좌 리스트·트리·S4 그리드) — 선택 있음 | 선택 열기(원 기능) | 표면이 소비(ExplorerPane.xaml.cs:1383 · ThumbnailExplorer.xaml.cs:155) | ✓ 사양 |
| 〃 — 선택 없음 | 무동작(표면 양보 — 전체화면 토글도 없음) | 표면 비소비 + :1883 ShouldPassThrough 양보 | 사양(keymap A186 명기) |
| 이름변경 편집 중 | 커밋(원 기능) | ExplorerRenameBox.cs:118-121 Handled | ✓ 사양 |
| 하단 바 버튼 포커스 | 그 버튼 클릭(원 기능) | Button 기본 Enter 처리(Handled) | ✓ 사양 |
| 대화상자 열림 | 대화상자 기본 버튼 | 팝업 트리 — 셸 미도달 | 사양 |
| 전체화면 중 | 복귀 토글 | :1879 → RestoreFromFullScreen | ✓ |
| S4 — 그리드 밖(예: 필터 텍스트) 포커스 | 무동작(텍스트 입력 우선 — Esc만 통과) | :1783 | 사양 |

## 표 4 — 화면 × Esc (A202 수리 후 체인)

셸 체인(:2122): **① 전체화면 복귀 → ② S4 복귀 → ③ 콘텐츠 닫기(신설 — 무제 포함, `TryCloseContent`
defaultSidebars:**true**) → 그 외 무동작·무소비**. 한 번의 Esc는 한 층만 움직인다.

| 화면 × 상태 | Esc 결과 | 근거 |
| --- | --- | --- |
| 전체화면(전 화면 공통) | 복귀 스냅샷으로 — 콘텐츠는 유지 | :2125 |
| S4 | 진입 전 상태 복귀 | :2131 |
| 콘텐츠 열림(이미지·영상·오디오·압축·문서·AllReadable 자식) — 창 모드 | **닫기 → 그 모듈 S1 + 기본 사이드바(A109)** — 파일 인자 시작(A81 무사이드바)에서도 아이콘 실행 기본 화면과 동일(합격선) | :2138 → :2217 |
| 영상/오디오 재생 중 — 창 모드 | **닫기**(재생 정지 = 뷰 Unloaded — 기존 '뒤로' ③ 경로와 동일). 사용자 문면의 일반화로 판단 = 맞음(한 층씩 규칙 — 더티 없는 즉시 닫기라 재열기 쉬움. 오케스트레이터 제안 채택) | 〃 |
| 문서 더티 | 닫기 가드(ConfirmDiscard) 경유 — 취소 = 무변경 | ShowModule(:1287) 안 A37 |
| 문서 에디터 포커스(캐럿) | 닫기 체인 도달(Esc는 텍스트 입력 판정보다 앞 — A90 확정 유지) — 더티면 가드 | :1738-1742 |
| 문서 무제 | 동일 규칙(닫기 — 더티면 가드, 문서 모듈 S1로) | :2217 `_untitledContent` |
| IME 조합 중 | 조합 취소(IME가 키를 먹는다 — 앱 미도달) | 실기기(§6-③) |
| 이름변경 편집 중 | 편집 취소만(표면 Handled — 셸 체인 미진입) | ExplorerRenameBox.cs:123-127 |
| 잘라내기 표시 있음 + 탐색기 표면 포커스 | 표시 해제만(**A202 개정: 지운 게 있을 때만 소비** — 다음 Esc가 다음 층) | ExplorerPane.xaml.cs:1433 · ThumbnailExplorer.xaml.cs:193 |
| 대화상자 열림 | 대화상자 몫(팝업 트리 — 셸 미도달. ContentDialog 자체 Esc 닫기) | A135 표 2 |
| S1·설정·미지원 안내·빈 셸 | 무동작·무소비(닫을 콘텐츠 없음 — 무간섭 원칙) | :2138 false 반환 |

## 표 A — 수리 목록 (버그 → 수리, 파일:라인)

| # | 항목 | 버그/공백 | 수리 | 지점 |
| --- | --- | --- | --- | --- |
| 1 | A196 | 게이트 false 3화면(무제 문서·설정·미지원 안내)에서 F11/F12·경계 버튼 무동작 | `HasPanelContext`에 `_untitledContent`·`IsPanelFallbackView` 편입 + `ApplyOverlayStates` fallback 축 + `ShowListOverlay` 모듈 없음 분기(전체 파일 필터) + `CurrentShellState` None = !HasPanelContext(4소비처 일관) | MainWindow.xaml.cs:1600·1609·2317·2564-2569·127 |
| 2 | A196 | 전체 파일 필터 개념 부재(확장자 목록 = Contains 판정뿐) | `ExplorerListing.AllFiles`(["*"]) 신설 + `MatchesExtension` 와일드카드 해석. A7 필터 메뉴는 "*" 토글 미생성 | ExplorerListing.cs:32·36-38 / ExplorerPane.xaml.cs:376(continue) |
| 3 | A202 | Esc 체인에 콘텐츠 닫기 층 없음(전체화면·S4 복귀뿐) | 체인 말단에 `TryCloseContent(defaultSidebars:true)` — '뒤로' ③ 실행부를 공용 추출 | MainWindow.xaml.cs:2122-2139·2217 |
| 4 | A201 | 압축 뷰만 IsTabStop·Loaded 자기 포커스 부재(7뷰 중 유일 — A135 표 1의 "전 모듈 뷰 IsTabStop" 서술이 실은 압축에 거짓) → 뷰 교체 직후 셸 키(Enter·Esc·F11/F12) 표류 가능 | 다른 6뷰와 같은 관용구 적용 | ArchiveView.xaml:9 · ArchiveView.xaml.cs:145 |
| 5 | A201/A196 | 미지원 안내 = 맨 TextBlock(Control 아님 — 포커스 불가·고아 복구 대상도 아님) | 포커스 가능 ContentControl 래퍼 + Loaded 자기 포커스(설정 화면과 같은 관용구) — A135 2차 방어(`as Control` 재포커스)의 대상도 된다 | MainWindow.xaml.cs:1197-1210 |
| 6 | A202 부수 | 잘라내기 Esc가 무조건 비소비 → 신설 닫기 층과 겹치면 한 Esc에 두 층(표시 해제 + 닫힘) | `ClearCutMarks` bool 반환 + **지웠을 때만 소비**(한 층씩 규칙) — S4에서도 표시 해제와 S4 복귀가 두 번의 Esc로 갈라진다(A94 4차의 "비소비" 개정) | ExplorerFileOps.cs:179 / ExplorerPane.xaml.cs:1433 / ThumbnailExplorer.xaml.cs:193 |

수리하지 않은 것: 문서 렌더 모드 Enter(이미 성립 — 표 3), 설정 화면 Enter(이미 성립),
탐색기 표면 무선택 Enter 무동작(사양), S4 중 F11/F12 무동작(사양 존치), 빈 셸(컨텍스트 밖 유지).

## 표 B — Esc 소비자 전수 · 체인 순서 증빙

버블링 순서(안쪽 → RootLayout)와 `e.Handled` 존중이 순서를 만든다. 전수 = 저장소 Esc 처리 grep
(`VirtualKey.Escape`) 6곳 + 팝업 트리 층.

| 순위 | 소비자 | 층 | 소비 조건 | 증빙 |
| --- | --- | --- | --- | --- |
| 0 | 대화상자(ContentDialog — A37 가드·충돌·영구 삭제 확인)·플라이아웃/메뉴 | 팝업 트리 | 항상(셸에 키 자체가 안 온다) | ExplorerConflictDialog.cs:138은 관찰용, 소비는 대화상자 기본 |
| 1 | 이름변경 편집 상자 | 표면(TextBox 버블링 — RootLayout보다 앞) | 항상 Handled(취소) | ExplorerRenameBox.cs:123-127 |
| 2 | 잘라내기 표시 해제(A94 → A202 개정) | 표면(handledEventsToo지만 Handled는 여기서 세움) | **지운 표시가 있을 때만** | ExplorerPane.xaml.cs:1433 · ThumbnailExplorer.xaml.cs:193 |
| 3 | 셸 ① 전체화면 복귀 | OnShellEscape(:2125 — Handled·오토리피트 존중 :2124) | 전체화면일 때 | RestoreFromFullScreen |
| 4 | 셸 ② S4 복귀 | :2131 | S4일 때 | ExitOpenFileBrowsing(restore:true) |
| 5 | 셸 ③ 콘텐츠 닫기(A202 신설) | :2138 | 파일 콘텐츠 또는 무제(A189)가 열려 있을 때 — 가드는 ShowModule의 A37 | TryCloseContent(:2217, defaultSidebars:true) |
| 6 | (없음) | — | 위 전부 해당 없음 = 무소비로 흘림(무간섭) | :2138 false |

모듈 내 Esc 소비는 **0건**(A151이 8벌 제거한 상태 유지 — XAML/액셀러레이터 전수 grep 재확인).
'뒤로'(XButton1·GoBack, `TryNavigateBack` :2188)는 같은 층 구조이나 ③이 defaultSidebars=**false**
(A112 명시 "좌/우 상태 닫기 직전 그대로 보존") — **Esc의 true와 의도된 차이**로 존치 판단(아래 보고).

## §5 — 확인 필요 (사양 모호 — 수리 보류)

1. **미지원 안내 화면의 Esc**: 파일 인자로 미지원 파일을 열면 콘텐츠가 아니라 안내 화면이라
   (`_currentFilePath` null·모듈 null) 닫기 층이 잡지 않는다 — Esc 무동작. A202 문면("콘텐츠 닫고")
   범위 밖인데, "파일 인자 실행 후 Esc = 기본 화면"의 취지로는 기본 모듈(H/W)로 보내는 확장도
   가능하다. 모듈 없는 화면의 닫기 목적지 사양이 필요해 보류.
2. **설정 화면의 Esc**: 직전 화면 복귀/닫기 개념이 없어 무동작 유지. 필요하면 별도 항목으로.
3. **'뒤로' ③의 defaultSidebars 통일 여부**: 유지(false) 판단 — A112가 "닫기 직전 그대로 보존"을
   명시 요구했고, A202 문면은 Esc에만 걸린다. 두 닫기 경로의 패널 결과가 다른 것이 의도된
   차이임을 keymap·가이드에 명기했다. 통일을 원하면 A112 개정으로.
4. **설정 화면 진입 시 사이드바 기본**: 모듈 전환(A109)과 달리 설정 진입은 defaultSidebars를
   적용하지 않는다(종전 유지 규칙 — 직전 상태 승계). 편입 후에도 그대로 두었다(등재문이 초기
   상태를 지정하지 않음). 설정도 A109처럼 "항상 양쪽"으로 시작해야 하면 한 줄 수리로 가능.

## §6 — 실기기 확인 포인트

① **포커스 고아 일반**(A135 §4-① 승계): 2차 방어(ApplyOverlayStates 말미 재포커스)는 존치.
   이번 수리로 압축 뷰·미지원 안내가 자기 포커스를 갖게 돼 "뷰 교체 직후 키 전멸" 후보가 줄었다 —
   실기기에서 A196 재보고("v0.201.0에서도 안 되는 상황") 증상이 남으면 재현 화면을 특정할 것
   (게이트 3화면 + 압축 포커스 공백 밖이면 재조사).
② **설정 화면 사이드바 레이아웃**(함정 3): 도크 25%가 서면 설정 콘텐츠(MaxWidth 680
   ScrollViewer)가 좁아진다 — 코드상 충돌은 없으나(스크롤로 흡수) 실표시 확인.
   전체 파일 필터 리스트(썸네일·상세 조각 포함)의 체감 성능도 실기기 몫.
③ **IME 조합 중 Esc**: 조합 취소가 IME에서 소비되어 앱에 안 오는 것이 기대 — 오는 기종/IME이면
   닫기 층이 움직일 수 있다(그 경우 원 기능 우선 예외 추가 필요).
④ **ContentControl 래퍼(미지원 안내)의 문구 중앙 정렬**: 스트레치 프레젠터 + TextBlock Center
   조합의 실렌더 확인(CI는 레이아웃을 못 본다).
⑤ **Esc 닫기 직후 포커스**: ShowModule 빈 컨텍스트 → 새 뷰 Loaded 자기 포커스(전 7뷰) 기대 —
   S1 썸네일 그리드로 바로 키 탐색이 되는지.
