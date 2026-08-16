# A94 탐색기 동작 대조표 (윈도우 탐색기 × KOTU)

> **A94 완료(v0.153.0) — 이 표는 영구 기록이다.** 6차로 항목이 끝났고 REQUIREMENTS에서는 결번이다.
> 남아 있는 ❌/🔶는 미완이 아니라 **2026-08-16 스코프 확정(안 하기로 한 것)**이다 — 사유는 각 행
> 비고에도 병기했다:
> - **OS 셸 확장 컨텍스트 메뉴**(탐색기 우클릭 항목 전체 이식) — COM 셸 확장 호스팅이 필요한 대형
>   작업. 필요해지면 **별도 항목으로 새로 등재**한다.
> - **Alt 드래그 = 바로가기 만들기** — 위와 같은 COM(IShellLink) 대형. 별도 등재 후보.
> - **드래그 중 항목 수 배지** — WinUI DragUI 제약(커스텀 비주얼 조립이 제한적) 대비 가치가 낮다.
> - **UAC 승격 뒤 자동 재시도** — 승격 헬퍼 프로세스가 필요하다. "Restart as admin" 제안으로 대체.
> - **별도 진행률 대화상자** — 기존 안내 문구 채널의 라이브 갱신("Copying 3 of 12...")으로 대체.

**이 표가 A94 진행의 단일 원본이었다.** 배치가 나갈 때마다 여기부터 갱신했다.
대상 표면 = 좌 도크(FileListOverlay: 폴더 트리 + ExplorerPane 리스트) · 중앙 썸네일 그리드
(ThumbnailExplorer — S1·S4 인스턴스 공통). 공용 조작 로직 = `src/KOTU.App/ExplorerFileOps.cs`.

표기: ✅ = 구현 / 🔶 = 부분·의도적 차이(비고 참조) / ❌ = 안 함(스코프 아웃 확정).
(진행 중에 쓰던 ▫ = 후속·미정은 6차에서 전부 ❌ 또는 ✅로 확정돼 표에서 사라졌다.)
(1차 v0.124.0 · 2차 v0.125.0 · 3차 v0.150.0 · 4차 v0.151.0 · 5차 v0.152.0 · 6차 v0.153.0 —
"2차 예정" 열은 2차 출시로 소진돼 정리했다. "후속·미정" 열은 6차에서 전부 스코프 확정으로 닫혔다.)

| 동작 | 윈도우 탐색기 | KOTU (v0.153.0 최종) | 후속·미정 | 비고 |
| --- | --- | --- | --- | --- |
| 클릭 선택 / Ctrl+클릭 토글 / Shift+클릭 범위 | 지원 | ✅ Extended (리스트·썸네일 그리드) — 1차 | | 폴더 트리는 Single 유지(트리 다중선택은 탐색기도 없음) |
| Ctrl+A 전체 선택 | 지원 | ✅ 탐색기 표면 포커스에서만 — 1차 | | KeyDown 한정 — A34 핫키·텍스트 에디터로 새지 않음 |
| 더블클릭 = 폴더 진입 / 파일 열기 | 지원 | ✅ (현행 유지 — A93/A24) | | Enter도 동일(A86/A90) |
| 다중 선택 일괄 열기 (더블클릭·Enter) | 지원 | ✅ 선택된 **파일 전부** — **6차 v0.153.0** | | **첫 파일 = 기존 단일 열기 경로**(현재 창 재사용 규칙 A24·설정 존중, Shift 더블클릭이면 새 인스턴스) · **나머지 = 전부 새 인스턴스**(OpenFileInNewWindow). **폴더는 일괄 열기에서 제외** — 선택에 파일이 섞여 있으면 파일만 열고, 파일이 0개(폴더만 다중)면 종전 첫 항목 동작(폴더 진입). **상한 10개** — 초과 시 앞 10개만 열고 "Opened first 10 of n selected files"(기존 안내 채널). 잡은 항목이 선택 밖이면 그 항목만(드래그·삭제와 같은 규칙). 적용 진입점 = 리스트/그리드 Enter · 더블클릭 · 셸 Enter(S1) · 셸 Enter(S4) 전부 |
| 드래그 아웃 (앱 → OS 탐색기·타 앱·다른 KOTU 창) | 지원 | ✅ 선택 전부, 폴더 포함 (StorageItems) — 1차 | | 컨테이너 CanDrag+DragStarting 데퍼럴 방식. OS 탐색기 쪽이 이동을 택한 경우의 원본 삭제 주체는 실기기 확인 항목 |
| 드랍 인: 폴더 항목 위 = 그 폴더로 | 지원 | ✅ (리스트 폴더 항목·썸네일 폴더 타일) — 1차 | | |
| 드랍 인: 빈 영역·파일 항목 위 = 현재 폴더로 | 지원 | ✅ (패널·썸네일 빈 영역 공통) — 1차 | | 트리 영역 드랍도 현재 폴더로(트리 노드별 대상 지정은 미지원) |
| 드랍 동작: 같은 볼륨 = 이동 / 다른 볼륨 = 복사 | 지원 | 🔶 앱 내부 드래그(다른 KOTU 창 포함)만 볼륨 판정 — 1차 | | 외부(OS 탐색기 등) 소스는 원본 경로를 몰라 **기본 = 복사** — 수정자로만 이동 |
| Ctrl 홀드 = 복사 강제 / Shift 홀드 = 이동 강제 | 지원 | ✅ DragOver 커서 표기 포함 — 1차 | | Alt 홀드 = 바로가기(링크) 만들기는 ❌ — **2026-08-16 스코프 아웃**(IShellLink COM 대형. 필요해지면 별도 등재) |
| 같은 폴더로 이동 = 무동작 | 지원 | ✅ 가드 (DragOver에서 커서부터 None) — 1차 | | Ctrl 강제 복사는 허용 — 같은 폴더 사본 "이름 (2)" |
| 폴더를 자기 자신·하위로 이동/복사 금지 | 지원(오류 창) | ✅ 가드 (경로 prefix, 대소문자 무시) — 1차 | | 커서 None + 조작 단계 이중 가드 |
| 이름 충돌 처리 (이동/복사) | 대화상자(대치/건너뛰기/이름 변경) | ✅ **선택형 대화상자** "Replace or skip files" — **3차 v0.150.0** | | Replace = 파일 덮어쓰기 / 폴더 **병합**(내부 파일 충돌도 같은 정책 흐름) · Skip = 건너뛰기 · Keep both = 기존 "(2)" 규칙 재사용. 남은 충돌 2건 이상이면 "Do this for all remaining conflicts" 체크박스(3버튼 전부 적용, 이번 조작 한정). **Esc = 취소(남은 작업 중단, 수행분 유지)** + "n of m completed" 안내. 같은 폴더 강제 복사(자기 충돌)는 묻지 않고 "(2)" 사본(탐색기 동등). **예외(변경 금지): F2 = 거부+원복(아래 F2 행), 새 폴더 = "New folder (2)"** |
| Ctrl+C 복사 / Ctrl+X 잘라내기 | 지원 | ✅ StorageItems + RequestedOperation(Copy/Move) — 1차 | | 탐색기 표면 포커스에서만 — 문서 에디터의 Ctrl+X와 충돌 없음 |
| Ctrl+V 붙여넣기 (대상 = 현재 폴더) | 지원 | ✅ 잘라내기 = 이동, 성공 시 클립보드 비움(1회성) — 1차 | | 키(Ctrl+V)·빈 영역 메뉴 = 현재 폴더. **폴더 항목 우클릭 메뉴 Paste = 그 폴더**(6차 — 조작 로직은 처음부터 대상 폴더를 인자로 받아 넓힐 것이 없었다) |
| KOTU ↔ OS 탐색기 클립보드 상호운용 | 지원 | ✅ 양방향 잘라내기/복사 정합 — **6차 v0.153.0** | | **쓰기**: Ctrl+X = `"Preferred DropEffect"` DWORD **2**(DROPEFFECT_MOVE) · Ctrl+C = **5**(COPY\|LINK — 탐색기 관례)를 4바이트 리틀엔디언 스트림으로 함께 싣는다(RequestedOperation은 앱 내부용으로 그대로 유지 — 두 축을 같은 조작에서 정하므로 어긋날 수 없다). **읽기**: 붙여넣기 시 이 형식이 있으면 **그것이 우선**(이동 비트가 서 있으면 이동), 없거나 못 읽으면 종전 RequestedOperation 판정. 값 형이 런타임마다 다를 수 있어 IRandomAccessStream/IBuffer/byte[]를 형 검사로 받고 실패는 전부 종전 판정(원본이 남는 안전한 쪽). **이동 성립 시 원본 삭제 주체 = 우리**(기존 이동 로직 그대로) |
| 잘라내기 원본 반투명 표시 | 지원 | ✅ Opacity 0.5 (리스트·썸네일 공통) — **4차 v0.151.0** | | 상태 = 프로세스 전역 1벌 경로 집합(ExplorerFileOps) — 모든 창·두 표면이 같은 집합을 본다. 해제 = 붙여넣기 소진(클립보드 비우기와 **같은 조건**) · 새 Ctrl+C/X · Esc(표시만, 클립보드 유지) · 재스캔 뒤에도 경로가 남아 있으면 재적용. **한계: 다른 앱(OS 탐색기 등)이 클립보드를 바꿔도 우리 표시는 남는다** — `Clipboard.ContentChanged` 선례가 저장소에 없어 구독하지 않았다(Esc·새 복사·붙여넣기로 해제). 흐림은 항목 '콘텐츠'에만 적용(선택 강조는 또렷) |
| 조작 실패 표시 | 오류 대화상자 | 🔶 일시 안내 문구(A92류) — 첫 오류 메시지 + 건수 | | 항목별 실패 격리(하나 실패해도 나머지 계속). 2차의 삭제·이름변경·새 폴더도 같은 경로. 3차부터 진행("Copying n of m...")·취소("n of m completed") 문구도 같은 채널(취소 안내가 첫 오류보다 우선). **4차: 접근 거부가 1건 이상이면 문구 대신 대화상자**(위 권한 상승 행) — 보고 종착점은 `ExplorerFileOps.ReportAsync` 한 곳 |
| 조작 후 뷰 갱신 | 자동(감시) | ✅ 자동 감시(FileSystemWatcher) + 조작 직후 명시 재스캔 — **5차 v0.152.0** | | 감시 = ExplorerPane 현재 폴더(창별 인스턴스, 하위 미포함). 이벤트를 **디바운스 400ms**(마지막 기준 병합)로 모아 **전체 재스캔 1회** — 단일 원본(ExplorerPane) 경유라 ViewChanged로 썸네일까지(증분 반영 없음). **이름변경 편집 중에는 보류**(커밋/취소/실패 후 1회 소화 — ExplorerRenameBox 규칙 유지). 폴더가 사라지면 **가장 가까운 존재하는 상위로 이동**(탐색기 동등), 루트까지 없으면 감시 중지+빈 목록. 감시 생성 실패(네트워크 등) = 조용히 무감시(명시 재스캔만 — 종전 동작). 명시 재스캔과의 중복은 디바운스가 흡수(억제 플래그 없음) |
| F2 이름 변경 | 지원(인라인 편집) | ✅ 인라인 편집 — **2차 v0.125.0** | | 다중 선택이어도 **첫 항목(SelectedItem)만**. 파일은 확장자 제외 부분 선택(탐색기 관례). Enter/포커스 상실 = 커밋, Esc = 취소. 충돌·빈 이름·잘못된 문자 = **커밋 안 함 + 안내 + 원복**(자동 "(2)" 없음 — 사용자 의도와 다른 결과 방지). 우클릭 메뉴 Rename도 같은 편집 진입 |
| 새 폴더 (Ctrl+Shift+N·우클릭) | 지원 | ✅ 키 + **빈 영역 우클릭 메뉴** — 2차 v0.125.0(키) · **6차 v0.153.0(메뉴)** | | "New folder", 충돌 = "New folder (2)"(1차 UniqueDestination 재사용). 생성 직후 그 항목 선택 + 자동 이름변경 진입(탐색기 관례) — 메뉴 경로도 **같은 함수**를 부른다 |
| Del = 휴지통 삭제 | 지원 | ✅ WinRT DeleteAsync(StorageDeleteOption.Default) — **2차 v0.125.0** | | **확인 대화상자 없음**(탐색기도 휴지통행은 기본 무확인). 선택 전부(파일·폴더), 항목별 실패 격리. 우클릭 메뉴 Delete = 그 항목이 선택에 포함돼 있으면 선택 전부, 아니면 그 항목만 |
| Shift+Del 영구 삭제 | 지원 | ✅ WinRT DeleteAsync(StorageDeleteOption.PermanentDelete) + **확인 대화상자** — **4차 v0.151.0** | | 탐색기 동등 — **영구 삭제만** 확인창("Permanently delete this item?" / 복수는 "these n items?", 1건이면 이름 표기). Primary=Delete·Close=Cancel, **기본 버튼 = Cancel**(파괴 방지 — A113 ⓓ 원칙). 대상·실패 격리·재스캔은 Del과 같은 경로, Ctrl+Del은 종전대로 비켜 간다. **우클릭 메뉴에는 없다**(키만 — 탐색기도 메뉴는 Delete 하나) |
| 권한 상승(UAC) 필요한 조작 | 지원 | 🔶 접근 거부 구분 안내 + 관리자 재시작 제안 — **4차 v0.151.0** (승격 자동 재시도는 안 함) | ❌ 승격 후 자동 재시도 — **2026-08-16 스코프 아웃**(승격 헬퍼 프로세스 필요. Restart as admin 제안으로 대체) | 이동/복사/붙여넣기/삭제(휴지통·영구)/이름변경/새 폴더의 실패 중 `UnauthorizedAccessException`·HRESULT 0x80070005(E_ACCESSDENIED)·0x80070522(권한 없음)를 **구분 집계**(폴더 병합 내부의 거부도 그 최상위 1건). 1건 이상이면 완료 요약 대신 대화상자 "Access was denied for n item(s)..." — Primary = **Restart as admin**(하드웨어 뷰와 같은 runas 흐름 공유), 기본 버튼 = Cancel. **승격 뒤 자동 재시도 없음**(재시작 후 사용자가 다시 조작 — 사용자 확정 스코프) |
| 우클릭 항목 컨텍스트 메뉴 | 셸 확장 전체 | ✅ **자체 메뉴 완성** — **6차 v0.153.0** | ❌ OS 셸 확장 메뉴 | 순서(탐색기 관례 근사) = **파일**: Open in new instance(A24) → 구분선 → Cut·Copy → 구분선 → Rename·Delete / **폴더**: Cut·Copy·**Paste(대상 = 그 폴더)** → 구분선 → Rename·Delete. Cut·Copy 대상 = Delete·드래그와 같은 규칙(그 항목이 선택에 포함돼 있으면 선택 전부, 아니면 그 항목 하나)이고 조작은 Ctrl+C/X/V와 **같은 함수**를 부른다. Shift+Del(영구 삭제)은 메뉴에 없다(키만 — 탐색기 동등). **OS 셸 확장 메뉴는 2026-08-16 스코프 아웃**(COM 셸 확장 호스팅 대형 — 별도 등재 후보) |
| 우클릭 **빈 영역** 컨텍스트 메뉴 | 셸 확장 전체 | ✅ New folder / Paste / Refresh — **6차 v0.153.0** | ❌ OS 셸 확장 메뉴 | 두 표면 공통(리스트·썸네일 그리드). 셋 다 기존 경로 재사용 — 새 폴더는 Ctrl+Shift+N과 같은 함수(생성 후 이름 편집 진입 포함), Paste는 현재 폴더 대상, Refresh는 조작 후 재스캔과 같은 경로. **활성 판정은 메뉴가 열릴 때**: 아직 폴더가 정해지지 않았으면 셋 다 비활성, Paste는 클립보드에 파일 항목이 있을 때만(판정 실패 시 활성 — 눌러도 조용한 무동작). 항목 메뉴와의 **이중 발화는 ContextFlyout 규칙이 원천 차단**(가장 안쪽 ContextFlyout 하나만 뜬다 — 항목 위 우클릭은 여기까지 오지 않는다) |
| 드래그 중 항목 수 배지·고스트 이미지 | 지원 | 🔶 기본 컨테이너 드래그 비주얼만 | ❌ | **2026-08-16 스코프 아웃** — WinUI DragUI의 커스텀 비주얼 조립 제약 대비 가치가 낮다(기본 비주얼로 충분) |
| 진행률 표시(대량 복사) | 지원(진행 창·바) | 🔶 **안내 문구 라이브 갱신** "Copying 3 of 12..." — **3차 v0.150.0** | ❌ 별도 진행률 대화상자 — **2026-08-16 스코프 아웃**(문구 채널로 대체 확정) | 별도 진행 창·바 없이 기존 A92류 안내 채널 재사용(의도적 차이). 최상위 항목 기준(병합 내부는 폴더 1건), 항목 시작마다 갱신 + UI 마셜 100ms 스로틀(마지막 값은 강제 표시). 1~2개 조작은 생략(순간 완료). 완료·실패·취소 요약이 진행 문구를 같은 채널로 대체 |

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

## 3차 구현 메모 (v0.150.0)

- **충돌 대화상자 = `ExplorerConflictDialog`(신규 파일, 코드 생성 ContentDialog — XAML 불요)**.
  버튼 매핑: Primary=Replace / Secondary=Keep both / Close=Skip, Enter 기본 = Replace(탐색기 동등).
  **취소 = Esc**(3버튼 제약 — 제목 옆 X 없음): `ContentDialogResult.None`을 취소로 해석하되,
  Skip 버튼과의 구분은 `CloseButtonClick` 플래그(버튼 확증) + Esc `KeyDown`(handledEventsToo)
  플래그 이중 장치 — "버튼 확증 없는 None = 취소"라 어느 감지가 어긋나도 안전 쪽으로 떨어진다.
- **스레드 규약**: 조작은 워커 유지(Task.Run). 충돌을 만나면 조작 시작 시점에 캡처한
  `ExplorerFileOps.OpUi`(그 표면 창의 DispatcherQueue + XamlRoot + 안내 채널)로 마셜해
  대화상자를 띄우고, 워커는 `TaskCompletionSource`(RunContinuationsAsynchronously — 완료 후
  연속이 UI 스레드에 얹히지 않게)를 await. UI 스레드 동기 대기(.Wait()/.Result) 없음.
  **창(XamlRoot) 단위 SemaphoreSlim(ConditionalWeakTable)로 표시 직전 직렬화** — ContentDialog는
  창당 동시 1개(A113 실사례). 표시 실패(창 닫힘·TryEnqueue 거절·XamlRoot 부재)는 전부
  **그 시점부터 취소 흐름**(수행분 유지).
- **병합 규칙**: 폴더 Replace = 대상을 지우지 않고 재귀 병합. 내부 "파일" 충돌 = 같은 정책 흐름
  (all 선택 존중, 없으면 그 파일에 대해 대화상자 — 남은 내부 충돌 수는 미지수라 체크박스 항상
  노출), 내부 "폴더" 충돌 = 묻지 않고 자동 병합(탐색기 동등). **이동 병합은 빈 원본 폴더만
  삭제(비재귀)** — Skip·실패·취소로 항목이 남으면 원본 유지. 폴더 자리의 동명 파일(종류 불일치)은
  항목 실패로 격리(탐색기도 오류).
- **카운트·진행은 최상위 항목 기준**(병합 내부는 폴더 1건). 취소 안내 "Move/Copy cancelled -
  n of m completed"(n = 완료 최상위 항목)가 첫 오류 안내보다 우선. 취소된 잘라내기는 클립보드를
  비우지 않는다(남은 항목 재붙여넣기 가능). 성공 완주는 종전대로 조용 — 마지막 진행 문구가
  기존 2.5초 타이머로 자연 소멸하고 뷰 갱신이 피드백이다.
- 체크박스 노출 판정: 최상위 = 현재 1 + 남은 항목의 대상 존재 검사(2에서 조기 종료) —
  폴더 충돌은 항상 노출(내부 미지수). all 선택은 이번 조작 한정(저장 안 함) — `TransferOp` 수명.

## 4차 구현 메모 (v0.151.0)

- **영구 삭제는 2차 삭제 경로의 인자 하나 차이**: `DeleteToRecycleAsync`/`DeletePermanentlyAsync`가
  같은 `DeleteAsync(paths, option)`을 부르고 옵션만 Default ↔ PermanentDelete다(같은 WinRT enum —
  새 API 표면이 아니다). 확인 대화상자는 **호출부**(표면)가 조작 전에 받는다 — 조작 로직은
  "이미 확인된 것"만 실행한다. 취소하면 재스캔도 하지 않는다.
- **대화상자 게이트를 공용화**: 3차의 창(XamlRoot) 단위 세마포어를 `ExplorerDialogs.GateFor`로
  올리고 충돌 대화상자도 이걸 쓴다 — 종류가 다른 대화상자(충돌 · 영구 삭제 확인 · 접근 거부)가
  한 창에서 겹쳐도 차례로 뜬다(ContentDialog 창당 동시 1개 — A113). 마셜·취소 폴백 규약도
  3차와 동일한 형태(`ShowSerializedAsync` 제네릭 파이프)로 재사용했다.
- **잘라내기 표시**: 상태는 `ExplorerFileOps`의 **프로세스 전역 HashSet 1벌**(경로, 대소문자 무시).
  변경 통지는 **신규 정적 이벤트 하나**(`CutMarksChanged`)뿐이고, 표면은 Loaded 구독 / Unloaded
  해지로 붙는다(정적 이벤트가 닫힌 창의 컨트롤을 붙들지 않게 — 워커 정리와 같은 수명 규칙).
  통지를 받으면 **재스캔이 아니라 이미 그려 둔 항목의 Opacity만 다시 맞춘다** — Ctrl+X가 선택·
  스크롤을 날리지 않는다. 새로 그려지는 항목은 생성 코드(MakeGridItem·MakeListItem·MakeTile)가
  `ApplyCutMark`로 처음부터 반영하므로 폴더 이동·재스캔 뒤에도 표시가 유지된다(경로 기준 재적용).
  **Esc는 소비하지 않는다** — 셸 Esc(S4 복귀)가 탐색기 표면 포커스에서도 성립해야 하기 때문.
  Esc는 표시만 지우고 클립보드는 그대로 둔다(Ctrl+V로 다시 붙여넣을 수 있다 — 구현 시 결정).
- **접근 거부 판정**: `UnauthorizedAccessException`(System.IO 경로) + HRESULT 0x80070005·0x80070522
  (WinRT DeleteAsync 등 — 타입만으로는 못 가른다). 집계는 `OpResult.Denied`로 Failed의 부분집합이며
  카운트는 3차와 같은 **최상위 항목 기준**(폴더 병합 내부의 거부는 그 폴더 1건). 보고 종착점은
  `ExplorerFileOps.ReportAsync(notice, denied, ui)` 하나 — 표면 4곳(리스트·타일·좌 패널 드랍·
  이름변경 상자)이 전부 이걸 부른다. 판정이 빗나가면 종전 안내 문구로만 떨어진다(안전한 쪽).
- **runas 흐름 공용화**: 하드웨어 뷰 `OnElevateClick`의 본문(A17/A124)을
  `KOTU.App.Integration.AdminRelaunch`로 **단계 변경 없이** 옮겼다. 하드웨어 모듈은 Core에만
  의존하므로(아키텍처 규칙) 진입은 `KOTU.Core.Integration.AdminRelaunchHook`(App이 시작 시 배선 —
  RestartSession과 같은 방식)을 거친다. 모듈 고유의 마지막 정리(SensorService.Shutdown)는
  `beforeExit` 콜백으로 넘겨 순서(runas 성공 → 정리 → Exit)를 그대로 보존했다.
  인스턴스 키는 리터럴 대신 `Branding.AppName + "-Main"`(Program.InstanceKey와 같은 조립식).

## 5차 구현 메모 (v0.152.0)

- **폴더 감시 = ExplorerPane 인스턴스별 FileSystemWatcher 1개**(창별 — 전역 공유 없음. 중앙
  썸네일 뷰는 감시를 따로 갖지 않고 기존 ViewChanged 경로로 함께 갱신된다 — 단일 원본 규칙).
  대상은 현재 폴더 한 단계(IncludeSubdirectories=false), NotifyFilter는 FileName·DirectoryName·
  LastWrite·Size 최소 구성 — LastAccess류를 빼 Changed 폭주와 "재스캔이 이벤트를 되먹이는"
  순환을 피한다. 폴더 전환·오류 재시작은 **dispose+재생성**(Path 교체 재사용보다 실패 상태가
  단순 — 반쯤 살아 있는 감시자가 남지 않는다). 생성·시작 실패(네트워크·접근 불가·제거 드라이브) =
  조용히 무감시(종전과 같은 명시 재스캔만).
- **스레드·디바운스**: 감시 콜백은 스레드풀 → 감시 시작 시점(UI)에 캡처해 둔 DispatcherQueue로
  TryEnqueue 마셜 → UI의 DispatcherTimer 400ms를 Stop 후 Start로 되감는다(마지막 이벤트 기준
  병합 — DocumentView DirtyTimer 관용구). 만료 = **전체 재스캔 1회**(NavigateTo 재사용 — 정렬·
  필터·썸네일·ViewChanged가 그 경로에 이미 있다. 증분 반영 없음). 재스캔 재진입은 기존
  _loadSeq(늦은 결과 폐기)가 흡수하고, 우리 조작의 명시 재스캔과의 중복도 디바운스가 흡수한다
  (억제 플래그 없음 — 중복 재스캔 1회는 무해, 단순 우선).
- **이름변경 편집 중 보류**: ExplorerRenameBox가 활성 편집을 정적 집합으로 추적(IsEditing,
  창 여럿이면 겹칠 수 있어 집합)하고, 종료(커밋·취소·검증 실패 + 창 닫힘 안전망 = 상자 Unloaded)
  시 EditEnded를 1회 쏜다. 디바운스 만료가 편집 중이면 bool 보류 플래그만 세우고 EditEnded에서
  만료 처리를 재실행해 소화한다(큐 없음). 커밋 성공 직후는 onRenamed의 재스캔(NavigateTo →
  감시 재대상)이 보류 플래그를 먼저 걷어 가 이중 스캔이 없다. 구독은 표면 Loaded/Unloaded
  (CutMarksChanged와 같은 수명 규칙).
- **폴더 소실**: 디바운스 만료 시 현재 폴더가 없으면 가장 가까운 존재하는 상위로 NavigateTo
  (탐색기 동등 — Up과 같은 경로). 루트까지 없으면 현재 폴더 재스캔이 기존 실패 경로("Cannot
  read this folder" + 빈 목록 + ViewChanged 빈 통지)로 떨어지고 감시자도 재대상 실패로 꺼진다.
  Error 이벤트(버퍼 오버플로 포함) = 재생성 시도 + 디바운스 경유 재스캔 1회 — 폴더가 사라진
  경우도 이 흐름이 상위 이동으로 수렴한다.
- **수명**: Loaded에서 감시 (재)시작·정적 이벤트 구독, Unloaded에서 이벤트 전부 해제 + Dispose +
  디바운스 정지(FileSystemWatcher는 OS 콜백에 뿌리를 두어 Dispose 없이는 뷰 델리게이트째
  살아남는다 — 정적 이벤트와 같은 부류). 좌 도크 "닫힘"은 Visibility 변경이라 Unloaded가 아니다 —
  도크가 닫혀 있어도 감시가 살아 중앙 썸네일이 계속 갱신된다(의도). 마셜·Tick 핸들러는 전부
  살아 있음 검사로 시작해 Unloaded 뒤 잔여 발화가 죽은 뷰를 만지지 않는다.

## 6차 구현 메모 (v0.153.0) — A94 마지막 배치

- **빈 영역 메뉴의 이중 발화 차단은 새 판정이 아니라 프레임워크 규칙 그대로다**: 컨텍스트 요청은
  원본 요소에서 위로 버블링하며 **가장 안쪽의 `ContextFlyout` 하나만** 떠서 요청을 소비한다.
  그래서 항목 컨테이너에는 종전대로 항목 메뉴(2차)를 걸고, **그 바깥 표면**에만 빈 영역 메뉴를
  건다 — 히트 판정 코드를 따로 쓰지 않는다(OriginalSource 탐색 없음 = 어긋날 곳이 없다).
  거는 위치: `ExplorerPane`은 `IconGrid`·`ListPane`(표면마다 한 벌 — 새 폴더 편집 진입이 자기
  owner의 컨테이너를 찾아야 한다), `ThumbnailExplorer`는 **`LayoutRoot`**(배경이 있어 히트가
  보장되고, 그리드에서 올라온 요청도 반드시 여기를 지난다).
  ⚠️ 리스트/그리드의 **빈 영역이 히트 테스트되도록 XAML에 `Background="Transparent"`를 명시**했다
  (ExplorerPane.xaml) — 배경이 없으면 항목 아래 여백에서 요청 자체가 안 난다. 투명이라 화면은
  종전과 같다(좌 패널 아크릴이 그대로 비친다).
- **메뉴 항목 활성 판정은 여는 순간(`Opening`)에 한다** — 폴더 미정(항해 전)이면 New folder·
  Paste·Refresh 전부 비활성, Paste는 `Clipboard.GetContent().Contains(StorageItems)` 판정.
  **클립보드 접근 실패는 "활성"으로 둔다**(사양 확정) — 눌러도 기존 붙여넣기가 조용한 무동작으로
  떨어지므로, 잠깐 잠긴 클립보드 때문에 메뉴가 회색이 되는 쪽보다 낫다.
- **메뉴는 새 동작을 만들지 않는다** — 전부 기존 함수 호출이다: New folder =
  `CreateFolderThenRenameAsync`(2차의 Ctrl+Shift+N 본문. 썸네일 쪽은 이번에 인라인 본문을 같은
  이름의 메서드로 뽑았을 뿐 동작 무변경), Paste = `PasteIntoAsync`(= 기존
  `PasteFromClipboardAsync` + 재스캔 + `ReportAsync`), Refresh = 조작 후 재스캔 경로
  (`RefreshAfterFileOp` / `RefreshViaShell`), Cut·Copy = `CopyWithNoticeAsync`(= 기존
  `CopyToClipboardAsync`). **폴더 항목 Paste의 대상 인자화는 불필요했다** —
  `PasteFromClipboardAsync(targetFolder, ui)`가 1차부터 대상 폴더를 인자로 받는다.
- **일괄 열기는 표면이 주체**다: 상한만 `ExplorerFileOps.TakeBatchOpen`(10개 + 초과 안내 문구)에
  두고, 실제 열기는 **기존 이벤트 두 개**(`FileActivated` = 재사용 규칙 A24 · `FileActivatedNewWindow`
  = 항상 새 인스턴스)로만 나간다 — 탐색기 컨트롤이 창 규칙을 알지 않는 A93 구조를 그대로 지켰다.
  첫 파일만 `FileActivated`(Shift 더블클릭이면 첫 파일도 새 인스턴스), 나머지는 전부
  `FileActivatedNewWindow`. **루프를 동기로 도는 근거** = 창 생성·파일 열기가 단일 UI 스레드에서
  동기 완결이라는 A124 복원 루프의 전례(`WindowManager.TryRestoreSession`도 같은 형태의 foreach).
- **셸 Enter(S1·S4)도 같은 규칙**으로 옮겼다: 종전 "표면의 `SelectedFilePath` 하나를
  `OpenFileRouted`"를 **표면의 `OpenSelectedFiles()` 호출**로 바꿨다(반환 = 하나라도 열었는지 →
  false면 종전 폴백: S1 = 오버레이 일괄 토글, S4 = 폴더면 항해·아니면 복귀). 이로써 진입점 4곳
  (리스트/그리드 Enter · 더블클릭 · 셸 Enter S1 · 셸 Enter S4)이 한 규칙을 공유한다.
  `SelectedFilePath` 속성들은 "첫 선택 파일" 질의 API로 남겼다(A86/A90 서술의 원형 — 제거하지 않음).
- **Preferred DropEffect**: 쓰기는 `DataPackage.SetData("Preferred DropEffect", 스트림)` —
  값은 4바이트 리틀엔디언 DWORD이고 스트림 만들기는 `new MemoryStream(bytes).AsRandomAccessStream()`
  (ExplorerPane.LoadThumbnailsAsync의 `AsRandomAccessStream` 선례 재사용). MemoryStream을 닫지
  않는 것은 의도 — 클립보드가 나중에 읽고, 관리 메모리뿐이라 해제할 자원이 없다.
  읽기는 `GetDataAsync` 결과의 **형 검사 후 안전 판독**(byte[] / IRandomAccessStream /
  IBuffer 순)이며 전부 try/catch 안이다 — 형식 없음·판독 실패는 **전부 종전 판정
  (RequestedOperation) 유지**라 최악이 "복사로 떨어져 원본이 남는" 안전한 쪽이다.
  판정 우선순위는 **셸 형식 > RequestedOperation**(탐색기는 후자를 싣지 않는다).
