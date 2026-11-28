# A445 — 탐색기 Details 열

2026-11-28 후보 17의 정적 조사 뒤 사용자가 기본 꺼짐 Details 열, 기존 행 높이·선택·스크롤 유지, 공식 배포와 이 PC 설치를 선택했다. [A445 원요구·진행](../../history/requirements-completed-index.md#a445)에서 상태를 관리한다. 아래 v0.440.0 대조는 구현 전 코드 조사이며 실제 화면 검수·성능 실측이 아니다. [선택 전 조사 원문](https://github.com/zpstudios/kotu/blob/6c1834f6cd112837f7726994bda6679c75dfbea8/docs/features/explorer/explorer-detail-info-candidate17.md)을 보존한다.

## 구현 전 v0.440.0 기준과 원문 전제

[후보 원문](https://github.com/zpstudios/kotu/blob/070e21af070996ba3d78fb440db9b4552f75a723/docs/REQUIREMENTS.md#L3719-L3726)은 **탐색기 상세 줄**의 비PDF 텍스트 줄 수와 7z/RAR 압축률을 가리킨다. A329의 우측 정보 패널 구현과는 별개다. 이후 [A417](explorer-refresh-A417.md)이 기본 탐색 화면을 바꿨으므로 옛 상세 줄만 고쳐서는 정상 화면에 기능이 추가되지 않는다.

| 표시 위치 | 현재 코드에서 확인한 사실 | 근거 |
|---|---|---|
| 정상 중앙 List | 크기·종류·만든 날짜·수정 날짜는 스캔 결과에서 가져온다. 좁은 폭 요약도 이 값들만 조합하며 별도 상세 조회를 하지 않는다. | [List 준비](https://github.com/zpstudios/kotu/blob/5a3b736c0bd804b2098260901c63ff1657c5e568/src/KOTU.App/Controls/ThumbnailExplorer.List.cs#L115-L146), [표시값과 높이](https://github.com/zpstudios/kotu/blob/5a3b736c0bd804b2098260901c63ff1657c5e568/src/KOTU.App/ExplorerEntryVm.cs#L88-L106) |
| 내부 레거시 목록 | 기본 꺼짐 스위치를 켰을 때만 `ExplorerPane`을 만든다. 여기에는 텍스트 인코딩·ZIP 압축률 조각이 있고 줄 수·7z/RAR 압축률은 없다. | [스위치 기본값](https://github.com/zpstudios/kotu/blob/5a3b736c0bd804b2098260901c63ff1657c5e568/src/KOTU.App/Overlays/FileListOverlay.xaml.cs#L132-L138), [생성 조건](https://github.com/zpstudios/kotu/blob/5a3b736c0bd804b2098260901c63ff1657c5e568/src/KOTU.App/Overlays/FileListOverlay.xaml.cs#L262-L270), [기존 상세 조각](https://github.com/zpstudios/kotu/blob/5a3b736c0bd804b2098260901c63ff1657c5e568/src/KOTU.App/ExplorerPane.xaml.cs#L1212-L1238) |
| 우측 문서 정보 | 일반 텍스트의 `Lines`·`Characters`는 이미 계산한다. 4MiB 초과는 값을 비운다. 대상은 TXT·MD·MARKDOWN·LOG·INI·HTML·HTM이며 Office 계열은 별도 경로다. | [현재 정보 패널 계약](../product-contracts.md), [계산](https://github.com/zpstudios/kotu/blob/5a3b736c0bd804b2098260901c63ff1657c5e568/src/KOTU.Module.Document/DocumentQuickInfo.cs#L251-L307) |
| 우측 압축 정보 | 선택만 했을 때는 ZIP 통계만 읽는다. 정상으로 연 뒤에는 기존 트리의 통계로 7z/RAR도 압축률을 표시한다. | [단일 빌더와 두 경로](https://github.com/zpstudios/kotu/blob/5a3b736c0bd804b2098260901c63ff1657c5e568/src/KOTU.Module.Archive/ArchiveQuickInfo.cs#L8-L29), [열린 트리 재사용](https://github.com/zpstudios/kotu/blob/5a3b736c0bd804b2098260901c63ff1657c5e568/src/KOTU.Module.Archive/ArchiveView.xaml.cs#L94-L140) |

현재 상세 조각 캐시는 레거시 페인 내부에 있다. 창의 `FileMetadataCache`도 존재 여부·파일 크기만 보관하므로 압축 통계를 정상 List에 전달하는 경로는 새로 설계해야 한다. `ArchiveView`의 정상 열기 성공에서 얻은 `root.Size`는 재사용할 수 있지만 목록에서 정보 패널 전체 행이나 트리를 다시 순회할 이유는 없다. [현재 캐시 값](../../../src/KOTU.Core/Content/FileMetadataCache.cs) · [정상 열기 완료](https://github.com/zpstudios/kotu/blob/5a3b736c0bd804b2098260901c63ff1657c5e568/src/KOTU.Module.Archive/ArchiveView.xaml.cs#L296-L341)

## 확정 표시 범위

현행 중앙 List의 `Columns` 메뉴에 **기본 꺼짐인 `Details` 열**을 추가한다. 넓은 폭에서는 기존 열 뒤 별도 열, 좁은 폭에서는 기존 요약 줄에 포함하고 말줄임한다. 기존 행 높이·선택·체크·스크롤 위치를 유지하며 값 도착으로 정렬하지 않는다. 숨긴 열은 표시를 위한 새 조회를 만들지 않는다. 이것은 A417의 스캔 결과만 표시하는 계약을 넓히는 새 기능이며, 레거시 상세 줄의 단순 수리로 분류하지 않는다.

| 대상 | 표시 | 조회·실패 조건 |
|---|---|---|
| 위 7종 일반 텍스트 | `120 lines` | 저장된 파일의 논리 줄 수다. 화면 줄바꿈·HTML/Markdown 렌더 결과·미저장 편집 줄 수와 구분한다. 4MiB 이하만 전체 계산하고 초과·실패·온라인 전용은 생략한다. 빈 파일은 1줄, CRLF는 한 개행, 마지막 개행 뒤 빈 줄도 센다. |
| ZIP | `42%` | 기존 중앙 디렉터리 계산을 재사용한다. 추가 파일 해제는 없다. |
| 7z·RAR | `42%` | 사용자가 정상으로 연 파일의 통계만 같은 창 안의 상한 있는 메모리 캐시로 전달한다. 아직 열지 않았으면 생략한다. 목록 표시를 위해 백엔드 열거·암호 입력을 시작하지 않는다. |

압축률은 **압축 파일 크기 ÷ 원본 합 × 100**의 반올림값이며 절감률이 아니다. 빈 압축·원본 합 0·실패는 생략하고 100% 초과도 그대로 허용한다. 캐시에는 암호·내부 파일 목록을 넣지 않으며 재시작하면 비운다. 닫은 뒤에도 같은 파일로 확인되는 동안 재사용하되 변경·삭제·이름 변경·Refresh·클라우드 상태 변경에는 폐기한다. 경로·크기·수정시각과 세대를 대조해 다른 파일이나 취소된 열기의 늦은 결과가 값을 되살리지 않게 해야 한다.

## 구현·검수 경계

- **비용과 인코딩**: 기존 레거시 인코딩은 앞 64KiB, 정보 패널 계산은 최대 4MiB를 읽는다. 기존 함수만 바꾸면 뒤쪽 비ASCII 때문에 인코딩 판정까지 달라질 수 있다. 공통 줄 계산은 재사용하되 레거시 표시를 부수적으로 바꾸지 않는다. 4MiB는 정보 계산의 정책상 상한이지 현재 모든 편집기의 읽기 상한이라는 주장은 아니다. [두 조회 경로](https://github.com/zpstudios/kotu/blob/5a3b736c0bd804b2098260901c63ff1657c5e568/src/KOTU.Module.Document/DocumentQuickInfo.cs#L277-L329)
- **작업 수명**: 보이는 행만 제한된 워커에서 조회하고 취소·중복 억제·파일 교체 검사를 둔다. ZIP을 포함한 모든 자동 원본 조회는 열거 당시 placeholder 판정과 워커의 최신 속성 확인(`CloudFileAccess`)을 통과해야 하며 속성 조회 실패도 읽기 허가로 삼지 않는다. 새 List 경로는 기존의 추가 I/O 없는 상태와 비교해 첫 표시·연속 스크롤·폴더 이동 비용을 측정한다. `SevenZipBackend.List`는 취소 토큰이 없는 동기 열거이므로 목록의 자동 조회로 사용하지 않는다. [백엔드 계약](../../../src/KOTU.Module.Archive/IArchiveBackend.cs)
- **검수**: UTF-8/BOM·UTF-16·CP949, 빈 파일·혼합 개행·4MiB 경계·읽는 중 교체, ZIP/7z/RAR의 정상 열기·암호 취소·빈 압축·100% 초과·교체 후 캐시 무효화, S1/S4·넓고 좁은 List·열 숨김·가상화 재사용을 확인한다. 자동시험과 정상 UI 관측을 구분한다.

구현은 실체화된 행 중 뷰포트와 겹치는 행만 최대 2개 워커로 조회하고 창별 256항목 캐시를 둔다. 숨김·Preview·재활용·Unloaded·탐색 전환에는 취소하며, VM 토큰·탐색/메타데이터 세대·캐시 revision·컨테이너 동일성을 통지 직전까지 대조한다. 같은 폴더로 정상 복귀하면 동일 파일의 7z/RAR 통계를 보존하고 명시적 Refresh·변경 감시/오류에는 무효화한다. 행 소속 대조는 참조 집합으로 수행하여 매 행마다 전체 목록을 검색하지 않는다. 이는 코드 검토로 확인한 수명 계약이며 실제 UI·성능 관측 범위는 [A445 검수](../../DEVICE-CHECKS.md#a445)와 구분한다.

새 표시 열·설정 추가이므로 직전 공식 v0.440.0에서 v0.441.0 minor로 배포한다. 구현·로컬 검수·독립 검토·공식 배포·설치 완료 여부는 [현재 상태](../../HANDOVER.md)와 [A445](../../history/requirements-completed-index.md#a445)에서 구분한다.
