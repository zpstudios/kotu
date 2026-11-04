# A416 — 중앙 탐색기 체크·상위 타일·배치 복원

## 상태와 범위

2026-11-04 사용자 요청 세 가지를 B1/B2/B3로 나눠 직렬 구현·메인 검수했다. 폴더 이동 후 Ctrl+F의 기존 포커스 수명 문제는 별도 B3a/B3b로 제한해 보강했다. 실제 UI와 최종 메인 검수는 통과했으며 v0.414.0 배포 준비 상태다. 커밋·원격 배포 결과는 완료 후 별도로 기록한다.

기준은 정식 v0.413.0 `f1d50479e395825974d917243dc8987437499cc4`다. 원본 OneDrive 저장소의 소스와 Git 객체가 읽기 오류를 내어 TEMP의 얕은 clean clone에서 작업했다. 원본 녹화 보류21개와 문서 변경, 사용자 실행 앱·설정은 수정하지 않았다. 실제 실행은 별도 IPC와 MemorySettings를 사용하는 기존 Standalone 빌드이며 설치·등록·업데이트는 실행하지 않았다.

## 구현

### B1 — 선택과 파일 작업 체크 분리

왼쪽 ExplorerPane의 기존 체크 집합을 단일 원본으로 유지한다. 중앙 VM은 체크를 복제 소유하지 않고 기존 집합을 읽고 변경을 요청한다. 중앙 표시 체크, 목록 체크, 문맥 메뉴·일괄 작업의 대상이 같은 규칙을 사용한다. 숨겨진 체크의 작업 대상 포함 여부도 기존 목록의 표시 범위 규칙을 따른다.

Space는 실제 포커스 타일을 우선해 한 번 토글한다. 반복 키 메시지는 소비만 하며 파일 열기·미디어 동작·이름 prefix 이동으로 흘리지 않는다. 검색·이름 변경·본문·IME 입력·팝업의 입력은 가로채지 않는다. 필터 해제와 VM 재사용 때도 체크 원본을 다시 반영한다.

### B2 — 첫 슬롯의 상위 폴더 타일

상위 타일은 ExplorerEntryVm이 아닌 별도 표시 항목이다. LeadingItemCollection이 기존 파일 컬렉션의 Add/Remove/Replace/Move/Reset을 한 칸 오프셋으로 반영한다. 실제 파일 순서·목록은 그대로이며 부모 항목에는 가짜 파일 경로가 없다.

단일 클릭/Enter는 기존 FolderActivated 경로로 한 단계 이동한다. pending 가드로 중복 이동을 막고 실패 후 ShowEntries도 다시 활성화한다. 루트에서는 비활성 상태다. Space·Ctrl+A·선택 메타데이터·이름 검색·복사/잘라내기/삭제·체크·드래그 대상에서 제외한다. 재활용 컨테이너와 부모 영역에 버블링되는 drop도 구분한다. 빈 폴더·0검색 결과에서도 첫 슬롯은 유지한다.

### B3 — 실제 표시 상태에 맞춘 공통 배치

재현 원인은 비동기 ShowListOverlay가 폴더 확인을 기다리는 동안 ApplyOverlayStates가 왼쪽 패널을 아직 닫힌 것으로 계산한 데 있었다. 오른쪽 25%는 예약됐지만 왼쪽 예약은 0%, 중앙은 75%/6열로 남았고 뒤늦게 열린 왼쪽 패널이 앞 두 타일을 덮었다. 최초의 전폭8열 추정은 실제 재현 후 이 진단으로 정정했다.

SidebarGeometry는 실제 표시된 좌우 패널 수와 S4 float 정책에서 폭·열 수를 계산한다. 공통 ApplySidebarGeometry는 IO·포커스·패널 Show/Hide 없이 폭·열 수·경계 버튼만 반영한다. 네 실제 패널 표면의 Visibility 변경을 한 번의 dispatcher 작업으로 모아 비동기 완료·실패/닫힘·제공자 교체도 반영한다. 늦은 탐색기 생성과 Loaded, 같은 열 수 재적용도 타일 크기 계산을 재시도한다.

일반 중앙은 패널 0/1/2개에 100/75/50% 및 8/6/4열이다. S4 float 콘텐츠는 기존 전폭 정책을 유지하고 위에 놓이는 탐색기만 실제 패널 폭을 예약한다. 단순 Esc 전용 보정은 추가하지 않았다.

### B3a/B3b — 폴더 전환의 입력 위치

일반 폴더와 새 상위 타일 모두 이동 뒤 포커스가 살아 있는 왼쪽 UpButton으로 바뀌어 Ctrl+F가 목록 소유자로 인정되지 않는 현상을 관찰했다. 기존 진단은 `Button#UpButton`, `inRoot=Y`, `popup=N`, `skip(alive)`였다. 전역 복구 타이머나 키 정책은 바꾸지 않았다.

B3a는 현재 TileGrid 내부 포커스만 타일 비활성화/제거 직전에 동기 이관하는17줄이었으나 실제 입력 시험에서 효과가 없었다. 실제 WinUI 1.8.260803003의 `lib/net6.0-windows10.0.17763.0/Microsoft.WinUI/Themes/generic.xaml` 9007/9009행에서 GridView 기본 IsTabStop=False를 확인했다. 마지막 B3b 한 속성으로 TileGrid IsTabStop=True를 지정한 뒤 일반 폴더/상위 이동→Ctrl+F 실입력이 통과했다. footer/sidebar/popup 소유 시 가드는 무동작이며 비동기 완료 후 포커스를 되찾는 경로는 없다.

## 검증

| 범위 | 실제 확인 |
| --- | --- |
| B1 체크 | Space 켜기/끄기, 다른 항목 이동 후 체크 유지, 좌우 동기화, checkbox 두 클릭 시 파일 미열림, Ctrl+방향키의 실제 포커스, 검색 공백, 필터 숨김/복원 |
| B2 부모 | 첫 일반 슬롯·별도 header 없음, 빈 child→클릭/Enter 한 단계, Space 무동작, Ctrl+A 실제14항목만 선택, 0결과에도 부모, 일반 파일 체크 |
| B3 패널 | 초기 콘텐츠→Esc 양쪽4열, 오른쪽6/없음8/왼쪽6/양쪽4, 양쪽에서 파일 열기→Esc 두 번 반복 |
| B3 전환 | 최대화/복원, 빈 폴더/부모, 검색1/0결과와 필터 중 F11, Hardware→Document, Document→Image→Document |
| B3 S4 | windowed/fullscreen 두 보기 모드에서 탐색기4열·양쪽 예약, 콘텐츠 underlay 전폭 유지, S4 종료/콘텐츠 종료 복귀 |
| B3b 입력 | 일반 빈 child→Ctrl+F→probe 실제 값/0결과, 부모→fixture→Ctrl+F→space name 한 파일, Tab로 부모 focus, Space 무이동/Enter 한 단계, alpha+amber 체크 재확인 |

Core는 기존457개에서 컬렉션2개와 실제 production geometry 계산9개를 추가해468/468이다. 구조 검사20개와 정규 App 빌드 경고0/오류0, 최종 Standalone publish도 통과했다. 계산 시험은 실제 dispatcher를 대신하지 않으며 위 UI 전환으로 비동기 표시 결과를 확인했다. 최종 버전 표기 변경 뒤 전체 솔루션·배포 검사는 CI 결과와 구분해 기록한다.

메인은 B1 Space off/on을 직접 재확인했고 B2/B3의 원본 JPEG/raw와 최종 private 창을 독립 검수했다. 마지막 화면은 양쪽 패널 사이4열, 첫 parent, alpha/amber 두 체크가 중앙·왼쪽에 일치하며 검색은 비어 있었다. 사용자 설정 SHA·제품 레지스트리·fixture12개 파일·시험 소스376개는 UI 전후 일치했다.

### 증거와 한계

로컬 증거 루트는 `artifacts/a416/`다. 배치마다 preflight·소스 manifest·결과 JSON·공식 UI 반환 원본 JPEG/raw를 보존한다. B1 초기 ui00..08은 JSON만 보관됐고 이후 JPEG를 저장했다. B3a와 B3b 초기 저장 helper는 초기 state를 closure로 붙잡아 다른 화면을 저장했다. 해당 파일은 삭제하지 않고 **검증 근거에서 제외**했다. 실제 도구 화면 관찰과 저장 파일의 불일치였으며 UI 결과를 위조해 보완하지 않았다.

B3b는 state 명시 인자로 보관을 고쳐 `ui04-normal-search-corrected`, `ui05-parent-search-corrected`를 재검증했고 메인이 실제 값과 원본 JPEG를 대조했다. `ui06-parent-space`, `ui07-parent-enter`, `ui08-final-two-checks` 및 메인의 최종 live 관찰이 유효 증거다. B3 ui09/16/19는 메인이 내용까지 확인한 독립 근거다.

UIA focus/선택 값 일부가 낡아 parent/체크는 가시 focus 표시와 실제 화면 결과를 함께 사용했다. 물리적 키 홀드 반복·실제 IME·root drive disabled·드래그/실제 파일 작업은 UI 통과로 주장하지 않는다. 반복 가드·입력 경계·부모 제외는 소스 검수, 컬렉션/배치 정책은 단위 시험으로 확인했다. 실제 viewport 경계는 공식 이미지로 확인했으며 런타임 숫자 계측 harness는 추가하지 않았다.
