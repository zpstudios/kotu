# 현재 제품 계약

여러 기능에 걸친 확정 계약의 정본이다. 새 개발·조사·미확정 결정은 [남은 요구사항](../REQUIREMENTS.md), 관측 한계와 검수 대기는 [검수 문서](../DEVICE-CHECKS.md)에 둔다. 특정 기능의 상세는 각 행의 후속 기능 문서를 따른다. 계약 변경 시 해당 행을 갱신하며 작업 이력을 덧붙이지 않는다.

아래는 여러 기능에서 함께 지켜야 하는 현재 제품 계약이다. 기존 B2–B111의 110건 원문 위치는 [결정 색인](../history/requirements-decisions-index.md)에 있다(B1은 정리 전 문서에도 없음). 옛 결정의 고유 내용은 삭제된 것이 아니며 관련 작업을 선택했을 때 해당 행을 조회한다. 후속 반전이 있는 키·배율·모드·폴더 정책은 최신 기능 정본을 따른다. 과거 단일 배치의 자동 진행 승인·모델 이름을 현재 전체 백로그 승인으로 확대하지 않는다.

| 주제 | 현재 적용할 계약 | 원래 결정/후속 정본 |
|---|---|---|
| 이름·개인정보 | 앱 이름 KOTU (King Of The Util), 신규 표시명 KOTU. 개인 실명·개인 메일을 제품에 표시하지 않는다. | [B9·B29](../history/requirements-decisions-index.md#b9) |
| 메뉴·모듈명 | 모듈명은 단수형/Archive. 메뉴·연결 순서와 키는 옛 표가 아닌 현 가이드가 정본. | [B12·B16](../history/requirements-decisions-index.md#b12), [문서 지도](../README.md) |
| 파일 연결 | exe 경로 변경 시 조용한 재등록; 대표 확장자로 판단, 실패는 다음 실행 재시도. Standalone 차단, UserChoice 직접 수정 금지. 모듈 묶음 토글은 A326의 확장자별 상태를 사용. | [B24–25](../history/requirements-decisions-index.md#b25), [B80](../history/requirements-decisions-index.md#b80) |
| 인스턴스·트레이 | 인스턴스당 트레이 아이콘 1개. H/W 선택 저장은 전역 1벌·마지막 조작 우선. 최소화와 닫기는 별개이며 빠른 녹음 닫기 정책은 A407 보류 범위. | [B18·B21·B42·B45](../history/requirements-decisions-index.md#b42) |
| 브랜드 | 제공된 원본 에셋 보존, 단계형 적용. 16px 조건부 재검토는 A281만 해당. | [B28·B41](../history/requirements-decisions-index.md#b41) |
| 문서 저장 | 경고만 하고 자동 강행하지 않으며 사용자에게 선택 기회를 준다. 저장 전 검출은 진행/취소, 저장 후 검출은 재시도/다른 이름 저장을 제안한다. 현재 저장 재검증 계약에 따라 원본과 편집을 보호한다. settings.json은 실제 형식 그대로 취급한다. | [B37](../history/requirements-decisions-index.md#b37), [B56](../history/requirements-decisions-index.md#b56) |
| 문서·미디어 맞춤 | Fit의 표시값/실제 배율을 일치시킨다. B92의 옛 PDF 1:1 기본값은 A338→A374의 Contain으로 대체됐으며 텍스트 기본값과 혼동하지 않는다. | [B57·B92](../history/requirements-decisions-index.md#b92), [A381](editor/pdf-viewport-fit-2026-10-02-A381.md), [A411](media/video-fit-A411.md) |
| 모드·S4·포커스 | S4는 모달 계층, 전체화면+S4의 첫 Esc는 S4 닫기. 모드 계단·뒤로 키와 null 포커스 방어를 함께 지킨다. 사이드바 기본 양쪽 열림/오버라이드 수명은 모듈 전환까지. S4 투명도는 화면 모드별 계약이며 전체 불투명화를 일괄 적용하지 않는다. | [B79–88](../history/requirements-decisions-index.md#b79), [B94](../history/requirements-decisions-index.md#b94) |
| 상태·선택 | 꺼짐은 형상, 밝기는 활성/비활성 축에 사용. 열린 파일 표시·선택·포커스를 구분한다(A351/A417). 상태 표시와 버튼 실행 동작을 혼동하지 않는다. | [B89·B95·B109](../history/requirements-decisions-index.md#b89), [A417](explorer/explorer-refresh-A417.md) |
| 트리·목록 | 트리 동기화는 실제 표시 폴더 기준. All Readable 좌 리스트는 전 모듈 합집합이며 재생 목록 필터로 바꾸지 않는다. | [B96](../history/requirements-decisions-index.md#b96), [B101](../history/requirements-decisions-index.md#b101) |
| 시작 위치 | 앱 재시작·새 창은 전역 마지막 폴더를 사용한다. 빈 모듈 전환에서는 위치 유지, 파일을 여는 전환은 그 파일의 폴더로 이동한다. 옛 모듈별 키를 임의 청소하지 않는다. | [B71](../history/requirements-decisions-index.md#b71) |
| 정보 패널 | 포맷 정의 속성은 값이 비어도 라벨을 남기고 선택/열림은 같은 빌더를 공유한다. 스트림 배열·GUID 전용·본문성·셸/서비스 합성·포맷 밖 값·GPS는 제외. 사람이 읽을 값도 오는 코덱 키는 남기고 GUID 값만 거른다. 포맷에 없는 키는 만들지 않으며 기존 계산값만 재사용하고 상한 초과 수치를 전체처럼 표시하지 않는다. 줄 수는 4MB 초과 시 빈칸으로 둔다. 타일/트레이의 빈 조각 생략은 별도 규칙이다. 조회는 워커·일괄→개별→라벨 폴백과 취소/경로 방어를 유지. | [B97–98 및 하위 8규칙](../history/requirements-decisions-index.md#b98) · [후보17 원문](../history/requirements-completed-index.md#candidate-17) |
| 가이드 | 영어 Markdown→영어 site→한국어 Markdown→한국어 site 순서. 한국어에서도 UI 문자열·버튼·키는 영어 원문, HTML 두 언어 앵커는 동일. README는 개요/링크를 맡는다. `.ko.` 규칙 유지. | [B90–91](../history/requirements-decisions-index.md#b91) |
| 재생 이동 | 영상/오디오 ←/→는 시간 이동. 이전/다음 파일은 버튼·Ctrl+←/→·PageUp/Down. 수동 이동은 자동 반복 예산을 소모하지 않고 끝에서는 해당 버튼 비활성. SMTC는 창당 1세션. | [B110](../history/requirements-decisions-index.md#b110), [A349](../history/requirements-completed-index.md#a349) |
| 오디오 EQ | 기존 프리셋·하단 배치를 유지하고 밴드별 Custom 조절·복원과 Off 왕복 보존을 제공한다. | [A437 조작·저장·수명](media/audio-equalizer-A437.md) |
| 반복 재생 | 루프 없음/목록/파일은 상호 배타·기본 루프 없음. 횟수 1은 한 번 더 재생을 뜻하며 목록 무한 고정이라는 옛 결정은 A255로 개정됐다. 숨김 파일 제외·후속 표시 순서 계약을 지킨다. | [B76](../history/requirements-decisions-index.md#b76), [A255](../history/requirements-completed-index.md#a255) |
| 인쇄 | OS 표준 대화상자와 해당 모듈 Ctrl+P/하단 버튼. 텍스트는 편집기 글꼴·인쇄 가능 영역 여백·머리글 없음·대형 블록 분할, 이미지는 Contain 고정. 해당 업데이트가 없는 구형 Win10은 안내만 하며 GDI 대체 경로를 만들지 않는다. | [B78](../history/requirements-decisions-index.md#b78) |
| H/W·장치 | 긴 그래프는 주기와 무관하게 최근 5분. H/W는 콘텐츠 열림/닫힘 색 규칙의 예외다. 오디오 입력 장치 선택은 Windows 기본 장치 변경이며 단순 앱 내부 선택으로 바꾸지 않는다. 비관리자 센서 회수와 승격 서비스 상주는 별개. | [B67–70](../history/requirements-decisions-index.md#b67), [B76](../history/requirements-decisions-index.md#b76), [후보 26·29](../REQUIREMENTS.md#candidates) |
| 디스플레이 배율 | 설정 창이 있는 모니터에 즉시 적용하고 실패 시 Windows 디스플레이 설정으로 안내한다. 모니터 선택 UI 신설은 원래 범위에 없다. | [B76](../history/requirements-decisions-index.md#b76), [배율 조사](shell/display-scale-research-A48.md) |
| 기록 저장 폴더 | 다른 기존 모드별 경로는 Documents/KOTU로 통일. 기존 파일은 이동하지 않는다. 이후 공통 폴더 선택은 모든 모드 공유. | [B111](../history/requirements-decisions-index.md#b111), [A405](recording/common-recording-folder-A405.md) |
| 빠른 녹음 | 마지막 Record 음원 구성을 재사용. 일반 닫기→녹음 계속/트레이; Stop and save; 명시 Exit→저장 후 종료. 이 정책 확정은 보류된 A406/A407 재개 승인이 아니다. H/W 실제 수집을 기존 주기로 유지한다는 안은 미확정. | [B111](../history/requirements-decisions-index.md#b111), [A406·A407](recording/quick-recording-A406-A407.md) |
| 클라우드 원본 | 썸네일·속성 표시를 위해 온라인 전용 원본을 열거나 강제 다운로드하지 않는다. 제공 지연/실패는 경로별 예산·취소·늦은 결과를 구분하고 캐시 실패를 영구 고정하지 않는다. | [후보 40](../REQUIREMENTS.md#candidates), [A399 요청 보호](explorer/request-time-cloud-guard-A399.md), [B103](../history/requirements-decisions-index.md#b103) |
| 성능 판단 | 증상과 제기의 전제를 분리하고 원인은 측정으로 좁힌다. UI의 1회성 동기 I/O도 피하며 워커 우선순위/취소/배압을 함께 본다. ‘값 변경’과 ‘선택지 추가’를 구분한다. | [B99–109](../history/requirements-decisions-index.md#b99), 운영 규칙은 [CLAUDE](../../CLAUDE.md) |
