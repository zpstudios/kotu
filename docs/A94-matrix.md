# A94 탐색기 동작 대조표 (윈도우 탐색기 × KOTU)

**이 표가 A94 진행의 단일 원본이다.** 배치가 나갈 때마다 여기부터 갱신할 것.
대상 표면 = 좌 도크(FileListOverlay: 폴더 트리 + ExplorerPane 리스트) · 중앙 썸네일 그리드
(ThumbnailExplorer — S1·S4 인스턴스 공통). 공용 조작 로직 = `src/KOTU.App/ExplorerFileOps.cs`.

표기: ✅ = 구현 / 🔶 = 부분·의도적 차이(비고 참조) / ▫ = 후속·미정 / ❌ = 안 함(사용자 확정 전).
(1차 v0.124.0 · 2차 v0.125.0 — "2차 예정" 열은 2차 출시로 소진돼 정리했다.)

| 동작 | 윈도우 탐색기 | KOTU (v0.125.0 현재) | 후속·미정 | 비고 |
| --- | --- | --- | --- | --- |
| 클릭 선택 / Ctrl+클릭 토글 / Shift+클릭 범위 | 지원 | ✅ Extended (리스트·썸네일 그리드) — 1차 | | 폴더 트리는 Single 유지(트리 다중선택은 탐색기도 없음) |
| Ctrl+A 전체 선택 | 지원 | ✅ 탐색기 표면 포커스에서만 — 1차 | | KeyDown 한정 — A34 핫키·텍스트 에디터로 새지 않음 |
| 더블클릭 = 폴더 진입 / 파일 열기 | 지원 | ✅ (현행 유지 — A93/A24) | | Enter도 동일(A86/A90). 다중 선택 일괄 열기는 **미지원**(첫/포커스 항목만 — 후속 검토) |
| 드래그 아웃 (앱 → OS 탐색기·타 앱·다른 KOTU 창) | 지원 | ✅ 선택 전부, 폴더 포함 (StorageItems) — 1차 | | 컨테이너 CanDrag+DragStarting 데퍼럴 방식. OS 탐색기 쪽이 이동을 택한 경우의 원본 삭제 주체는 실기기 확인 항목 |
| 드랍 인: 폴더 항목 위 = 그 폴더로 | 지원 | ✅ (리스트 폴더 항목·썸네일 폴더 타일) — 1차 | | |
| 드랍 인: 빈 영역·파일 항목 위 = 현재 폴더로 | 지원 | ✅ (패널·썸네일 빈 영역 공통) — 1차 | | 트리 영역 드랍도 현재 폴더로(트리 노드별 대상 지정은 미지원) |
| 드랍 동작: 같은 볼륨 = 이동 / 다른 볼륨 = 복사 | 지원 | 🔶 앱 내부 드래그(다른 KOTU 창 포함)만 볼륨 판정 — 1차 | | 외부(OS 탐색기 등) 소스는 원본 경로를 몰라 **기본 = 복사** — 수정자로만 이동 |
| Ctrl 홀드 = 복사 강제 / Shift 홀드 = 이동 강제 | 지원 | ✅ DragOver 커서 표기 포함 — 1차 | | Alt/링크 만들기(바로가기)는 ❌ 미지원 |
| 같은 폴더로 이동 = 무동작 | 지원 | ✅ 가드 (DragOver에서 커서부터 None) — 1차 | | Ctrl 강제 복사는 허용 — 같은 폴더 사본 "이름 (2)" |
| 폴더를 자기 자신·하위로 이동/복사 금지 | 지원(오류 창) | ✅ 가드 (경로 prefix, 대소문자 무시) — 1차 | | 커서 None + 조작 단계 이중 가드 |
| 이름 충돌 처리 (이동/복사) | 대화상자(대치/건너뛰기/이름 변경) | 🔶 **무조건 "이름 (2)" 자동 생성** (GenerateUniqueName식, 대화상자 없음 — 1차 결정) | ▫ 충돌 대화상자 | 탐색기의 "- 복사본" 표기와 접미사가 다름. **이름변경(F2)은 예외 — 자동 "(2)" 없이 거부**(아래 F2 행) |
| Ctrl+C 복사 / Ctrl+X 잘라내기 | 지원 | ✅ StorageItems + RequestedOperation(Copy/Move) — 1차 | | 탐색기 표면 포커스에서만 — 문서 에디터의 Ctrl+X와 충돌 없음 |
| Ctrl+V 붙여넣기 (대상 = 현재 폴더) | 지원 | ✅ 잘라내기 = 이동, 성공 시 클립보드 비움(1회성) — 1차 | | 붙여넣기 위치(폴더 항목 선택 시 그 폴더)는 미지원 — 항상 현재 폴더 |
| KOTU ↔ OS 탐색기 클립보드 상호운용 | 지원 | 🔶 탐색기→KOTU 붙여넣기 = 동작(StorageItems). KOTU→탐색기 = 복사 위주 — 1차 | | "Preferred DropEffect" 형식 미탑재 — 탐색기에 붙여넣으면 잘라내기도 복사로 떨어질 수 있음(실기기 확인 항목) |
| 잘라내기 원본 반투명 표시 | 지원 | ❌ 미지원 (1차 결정) | ▫ | |
| 조작 실패 표시 | 오류 대화상자 | 🔶 일시 안내 문구(A92류) — 첫 오류 메시지 + 건수 | | 항목별 실패 격리(하나 실패해도 나머지 계속). 2차의 삭제·이름변경·새 폴더도 같은 경로 |
| 조작 후 뷰 갱신 | 자동(감시) | 🔶 조작 직후 명시 재스캔 (FileSystemWatcher 없음) | ▫ 폴더 감시 | 갱신은 단일 원본(ExplorerPane) 경유 — ViewChanged로 썸네일까지. **이름변경 편집 중에는 재스캔 금지**(커밋/취소 후에만 — ExplorerRenameBox) |
| F2 이름 변경 | 지원(인라인 편집) | ✅ 인라인 편집 — **2차 v0.125.0** | | 다중 선택이어도 **첫 항목(SelectedItem)만**. 파일은 확장자 제외 부분 선택(탐색기 관례). Enter/포커스 상실 = 커밋, Esc = 취소. 충돌·빈 이름·잘못된 문자 = **커밋 안 함 + 안내 + 원복**(자동 "(2)" 없음 — 사용자 의도와 다른 결과 방지). 우클릭 메뉴 Rename도 같은 편집 진입 |
| 새 폴더 (Ctrl+Shift+N·우클릭) | 지원 | ✅ Ctrl+Shift+N — **2차 v0.125.0, 키만** | | "New folder", 충돌 = "New folder (2)"(1차 UniqueDestination 재사용). 생성 직후 그 항목 선택 + 자동 이름변경 진입(탐색기 관례). **빈 영역 우클릭 메뉴는 원래 없어 안 만들었다**(항목 메뉴만 있음) — 우클릭 새 폴더는 빈 영역 메뉴 신설과 묶어 후속 |
| Del = 휴지통 삭제 | 지원 | ✅ WinRT DeleteAsync(StorageDeleteOption.Default) — **2차 v0.125.0** | | **확인 대화상자 없음**(탐색기도 휴지통행은 기본 무확인). 선택 전부(파일·폴더), 항목별 실패 격리. 우클릭 메뉴 Delete = 그 항목이 선택에 포함돼 있으면 선택 전부, 아니면 그 항목만 |
| Shift+Del 영구 삭제 | 지원 | ❌ 안 함 — 후속 등재(사용자 확정) | ▫ 후속 등재 | Del 핸들러가 Shift가 눌려 있으면 **비켜 간다**(삼키지도 않음) — 휴지통 삭제로 오동작하지 않게 |
| 권한 상승(UAC) 필요한 조작 | 지원 | ❌ 안 함 — 후속 등재(사용자 확정) | ▫ 후속 등재 | 권한 부족 = 실패 안내로 떨어짐 |
| 우클릭 항목 컨텍스트 메뉴 | 셸 확장 전체 | 🔶 자체 메뉴 — 파일: Open in new instance(A24)+Rename+Delete / 폴더: Rename+Delete — **2차에서 확장** | ▫ OS 셸 메뉴 | 폴더 항목에 메뉴가 생긴 것도 2차부터(종전 파일 전용). 빈 영역 메뉴는 없음 |
| 드래그 중 항목 수 배지·고스트 이미지 | 지원 | 🔶 기본 컨테이너 드래그 비주얼만 | ▫ | |
| 진행률 표시(대량 복사) | 지원 | ❌ 미지원 — 완료 후 일괄 갱신 | ▫ | 워커 스레드라 UI는 블로킹 안 됨 |

## 1차 구현 메모 (v0.124.0)

- 드래그 데이터는 `DragItemsStarting`이 아니라 **항목 컨테이너 CanDrag + DragStarting**으로 싣는다 —
  `DragItemsStarting`은 await가 안 되는데 StorageItems 수집이 비동기라, 데퍼럴이 있는 경로를 썼다
  (UI 스레드 WinRT 동기 대기는 교착 위험 — 저장소 관례).
- 폴더 이동/복사는 WinRT `StorageFolder`에 MoveAsync가 없어 **System.IO로 통일**(워커 스레드).
  `NameCollisionOption.GenerateUniqueName`과 같은 "이름 (2)" 규칙을 직접 구현했다.
- 소스 볼륨 판정용 원본 경로 목록은 `DataPackage.Properties`(문자열)로 실어 프로세스 경계
  (다른 KOTU 창)를 넘는다.

## 2차 구현 메모 (v0.125.0)

- 휴지통 삭제는 **WinRT `IStorageItem.DeleteAsync(StorageDeleteOption.Default)`** — Default가
  휴지통 경유다(저장소 선례: ImageViewerView.DeleteCurrentAsync). COM 인터롭(SHFileOperation류)은
  선례가 없어 쓰지 않았다.
- 인라인 이름변경(`ExplorerRenameBox`)은 이름 TextBlock을 Collapsed로 숨기고 **같은 패널·같은
  Grid 칸에 새 TextBox를 삽입**한다 — reparent가 아니라 Parent 함정이 없다. 이름 TextBlock 위치는
  표면별 생성 코드 기준(ExplorerPane 그리드/리스트·썸네일 타일 모두 콘텐츠 패널 Children[1] —
  인덱스 수동 동기).
- 편집 상자의 키가 새지 않는 3중 근거: Enter·Esc는 편집 상자가 Handled(셸 OnShellEnter/Escape는
  Handled·텍스트 입력이면 물러남) / 표면 KeyDown(handledEventsToo)은 `e.OriginalSource is TextBox`
  가드로 걸러냄 / A34 버튼 핫키·셸 단독 키·Shift+N은 HotkeySupport의 텍스트 입력 통과 규칙이 흘림.
- 새 폴더 후 편집 진입 타이밍: ExplorerPane은 `NavigateToAsync`(대기 가능형 분리)로 재스캔 완료를
  기다리고, 썸네일 그리드는 재스캔이 좌 리스트 경유 비동기라 `_pendingRenamePath` 예약 →
  다음 `ShowEntries`에서 진입한다(어느 쪽이든 **재스캔 완료 후 편집** — 순서가 규칙).
