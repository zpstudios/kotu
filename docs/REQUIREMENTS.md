# KOTU 요구사항 — 실제 남은 일

개발·조사·미확정 결정과 착수 조건만 둔다. 완료 결과·후보의 영구 주소는 [결과 색인](history/requirements-completed-index.md), 구현 후 검수는 [DEVICE-CHECKS](DEVICE-CHECKS.md), 확정 사양은 [제품 계약](features/product-contracts.md)이 정본이다. 미선택 항목은 착수 승인이 아니다. 마지막 등록 번호는 **A438**, 마지막 후보 번호는 **44**이다.

<a id="open-work"></a>
## 열린 작업과 보류

| ID | 상태 | 남은 일 유형 | 남은 일·착수 조건 | 근거 |
|---|---|---|---|---|
| <a id="a438"></a>A438 | 진행 | 조사 | 최초 PDF 열기 뒤 Ctrl+F 무응답의 실제 초점·키 입력 경로는 아직 미확정이다. 초점·휠 보강 수정본에서 첫 열기·본문 클릭·All Readable 중립 호스트를 분리해 재관측하고, 무응답이 남으면 실제 초점과 라우팅 원인을 좁힌다. 화면 입력 도구의 접근 거부가 해소되면 재개한다. | [원요구·현재 계약](history/requirements-completed-index.md#a438) · [관측과 후속 검수](DEVICE-CHECKS.md#a438) |
| <a id="a19"></a>A19 | 미착수 | 결정 | H/W 랭킹 제출의 서버·전송·개인정보 범위와 제출 정책을 정한다. 장기 후보이며 착수는 별도 선택한다. | [A19](history/requirements-completed-index.md#a19) |
| <a id="a45"></a>A45 | 부분 | 개발 | 다른 형식의 페이지 보기와 차트·수식 호환성을 검토·구현한다. 한글 원본 정밀 비교는 검수에서 관리하며 원본 편집·저장·인쇄와 암호·DRM·배포용 HWP/HWP 3.x는 지원 범위 밖이다. | [지원 범위](features/editor/office-text-preview-A45.md) |
| <a id="a115"></a>A115 | 보류 | 개발 | 네이티브 TextBox 커서 색 변경은 API 제약으로 보류한다. 기본 편집기 커서의 설치본 가독성 확인은 검수 문서에서 관리한다. | [구현 결과](history/requirements-completed-index.md#a115) · [검수](DEVICE-CHECKS.md#editor) |
| <a id="a363"></a>A363 | 백로그 | 결정 | 광고 서버·분석 기능의 데이터 수집 범위, 서버·도구·일정과 착수 여부를 정한다. | [A363](history/requirements-completed-index.md#a363) |
| <a id="a380"></a>A380 | 조사 | 조사 | All Readable 전환 중 원인 불명 종료. 새 재현의 전환 순서·현재/회전 로그·WER/덤프로 원인을 좁힌다. 파일 경로·내용·예외 메시지를 진단 로그에 추가하지 않는다. 원인 스택/덤프는 아직 없다. | [제보·진단 경계](history/requirements-completed-index.md#a380) |
| <a id="a399"></a>A399 | 조사 | 조사 | 실제 재현 파일·다운로드 상태를 확보해 로컬/온라인 전용/동기화 중과 최초/재진입/Refresh를 분리 조사한다. 원본 강제 다운로드·무한 UI 재시도 금지. 원본 부재를 매 세션 반복 질문하지 않는다. | [조사](features/explorer/onedrive-thumbnail-investigation-A399.md) · [검수 경계](DEVICE-CHECKS.md#image-cloud) |
| <a id="a406"></a>A406 | 보류 | 개발 | 메뉴 약 3초 누름 빠른 녹음·H/W 상태 표시를 구현한다. 2026-11-10/14 보류 유지: 재개 승인 전 구현·실녹음 금지. H/W 수집 주기 유지안은 아직 확정할 결정이다. | [설계·정책](features/recording/quick-recording-A406-A407.md) |
| <a id="a407"></a>A407 | 보류 | 개발 | 빠른 녹음 중 닫기→트레이·전용 표시를 구현한다. A406과 함께 미구현·미배포이며 재개 승인 전 착수하지 않는다. | [설계·닫기 정책](features/recording/quick-recording-A406-A407.md) |

**사용자 판단 대기** — 셸 키 진단 스트립(`diag.shellKeyOverlay`) 제거는 별도 지시와 A309 실기기 확인을 기다린다. 기본 꺼짐을 유지한다. [판단 근거](https://github.com/zpstudios/kotu/blob/070e21af070996ba3d78fb440db9b4552f75a723/docs/HANDOVER.md#L1813-L1815)

<a id="candidates"></a>
## 미해결 후보

번호는 기존 1–44를 유지한다. 현 구현과 대조 전 결함·미구현으로 단정하지 않는다. 원문 행과 종료된 후보는 [후보 결과 색인](history/requirements-completed-index.md#candidate-results)에서 회수한다. 승격·종결 때 이 표의 행을 제거하고 같은 영구 색인 행을 갱신한다.

| 후보 | 상태 | 남은 일 유형 | 남은 일·판단 조건 |
|---|---|---|---|
| <a id="candidate-2"></a>2 | 재확인 | 조사 | 이미지 ←/→가 숨김 이미지에 도달하는지. A346의 표시 순서 통합과 먼저 대조. |
| <a id="candidate-3"></a>3 | 재확인 | 조사 | A108 안내 문구 세로 중앙 잔존·버튼/표면 동반 정렬. |
| <a id="candidate-6"></a>6 | 결정 대기 | 결정 | 영상·오디오·H/W 드라이브 줄. 사용자 선택 전 추가하지 않음. |
| <a id="candidate-7"></a>7 | 결정 대기 | 결정 | 페이지·탐색 조작의 버튼화. 현재 A349 등과 범위 재대조. |
| <a id="candidate-12"></a>12 | 대조 후 판단 | 조사 | 마크다운 렌더 트리 분할. A193 완료 범위와 남은 대형 입력을 대조. |
| <a id="candidate-13"></a>13 | 대조 후 판단 | 조사 | 썸네일·상세의 상한 병렬화/이미지 이웃 선읽기. A194·A200 등 후속 범위와 대조. |
| <a id="candidate-15"></a>15 | 부분 | 조사 | 창 모드 확대·커서 숨김의 남은 범위와 오디오 pause 부상 계약을 확인한다. |
| <a id="candidate-16"></a>16 | 후보 | 개발 | 리스트 전체 체크 해제 제스처. 현재 A416 선택/체크 계약과 대조. |
| <a id="candidate-17"></a>17 | 후보 | 조사 | 상세 줄의 비PDF 줄 수·7z/rar 압축률 표시 범위를 정한다. [정보 패널 계약](features/product-contracts.md)을 따른다. |
| <a id="candidate-18"></a>18 | 결정 대기 | 결정 | 우측 정보 패널 드롭을 모듈 기준 라우팅과 맞출지. |
| <a id="candidate-20"></a>20 | 대조 후 판단 | 조사 | 마크다운 렌더 줌·편집 중 실시간 미리보기·키보드 스크롤. A401 도입 범위와 분리. |
| <a id="candidate-21"></a>21 | 재확인 | 조사 | 회전 후 하단 바·정보 패널 해상도 축 스왑. A398 완료 범위와 대조. |
| <a id="candidate-22"></a>22 | 재확인 | 조사 | S4×전체화면 스냅샷 겹침. A305가 옛 스냅샷을 폐지했으므로 전제부터 확인. |
| <a id="candidate-26"></a>26 | 보류 | 조사 | 비관리자 NVMe 온도 조회는 추정. 실기기 성립을 먼저 확인한 뒤 Core 훅 검토. |
| <a id="candidate-29"></a>29 | 조건부 보류 | 결정 | 센서 승격 수집 서비스 상주. 전체 센서 무승격 수요가 생길 때 별도 등록. |
| <a id="candidate-30"></a>30 | UX 후보 | 조사 | 바 버튼 클릭 직후 Enter 재실행은 ButtonBase 정상 우선순위. 불편 재현 시 포커스 이동 검토. |
| <a id="candidate-33"></a>33 | 성능 후보 | 조사 | 정보 예열을 이미지 외 오디오·영상·문서·압축으로 확대할 이득과 썸네일 작업 예산을 실측. |
| <a id="candidate-34"></a>34 | 재현 필요 | 조사 | libvlc EQ 열거 실패 후 Off만 남는 경우 재시도. 정상 빈 배열 가능성도 구분. |
| <a id="candidate-35"></a>35 | 주석 감사 후보 | 조사 | ‘다음 EOF부터 정상’ 가정은 다음 사건이 보장되는지 확인. 동작 수리와 낡은 주석을 구분. |
| <a id="candidate-36"></a>36 | 실측 필요 | 조사 | 오디오/영상 재생 워커와 폴더 스캔·정보 조회 직렬 병목. 조회 분리 전에 체감/타이밍 측정. |
| <a id="candidate-37"></a>37 | 재확인 | 조사 | EnsurePlaylist 스캔 실패→null 이후 재시도 없는 경로. A330 목록 루프와 자동 다음곡을 구분. |
| <a id="candidate-40"></a>40 | 코드 감사·미확인 | 조사 | 클라우드 원본 비접근 계약이 상세 줄·중앙 텍스트 미리보기·재생 전 조회에 적용되는지 감사한다. 실제 상태 변화는 원본·제공자 조건을 확보해야 판정한다. [철회된 근거·미관측 경계](DEVICE-CHECKS.md#image-cloud) |
| <a id="candidate-41"></a>41 | 대형 설계 후보 | 조사 | 스트리밍 열거·경량 Entry로 첫 화면 지연 완화. 가상화 완료와 별도이며 크기/날짜 정렬의 전수 조회 비용을 먼저 설계. |

## 이전 주소 호환

분리 전 발급한 주소만 유지한다. 새 참조는 결과 색인의 A번호·후보 주소를 사용하며 완료 때 호환 링크를 추가하지 않는다.

<a id="current-decisions"></a>[현재 확정 사양·결정](features/product-contracts.md)

<a id="a112"></a>[A112](history/requirements-completed-index.md#a112)
<a id="a142"></a>[A142](history/requirements-completed-index.md#a142)
<a id="a281"></a>[A281](history/requirements-completed-index.md#a281)
<a id="a398"></a>[A398](history/requirements-completed-index.md#a398)
<a id="a401"></a>[A401](history/requirements-completed-index.md#a401)
<a id="a418"></a>[A418](history/requirements-completed-index.md#a418)
<a id="a419"></a>[A419](history/requirements-completed-index.md#a419)
<a id="a420"></a>[A420](history/requirements-completed-index.md#a420)
<a id="a421"></a>[A421](history/requirements-completed-index.md#a421)
<a id="a422"></a>[A422](history/requirements-completed-index.md#a422)
<a id="a423"></a>[A423](history/requirements-completed-index.md#a423)
<a id="a424"></a>[A424](history/requirements-completed-index.md#a424)
<a id="a425"></a>[A425](history/requirements-completed-index.md#a425)
<a id="a426"></a>[A426](history/requirements-completed-index.md#a426)
<a id="a427"></a>[A427](history/requirements-completed-index.md#a427)
<a id="a428"></a>[A428](history/requirements-completed-index.md#a428)
<a id="a429"></a>[A429](history/requirements-completed-index.md#a429)
<a id="a430"></a>[A430](history/requirements-completed-index.md#a430)
<a id="a431"></a>[A431](history/requirements-completed-index.md#a431)
<a id="a432"></a>[A432](history/requirements-completed-index.md#a432)
<a id="a433"></a>[A433](history/requirements-completed-index.md#a433)
<a id="a434"></a>[A434](history/requirements-completed-index.md#a434)
<a id="a435"></a>[A435](history/requirements-completed-index.md#a435)
<a id="a436"></a>[A436](history/requirements-completed-index.md#a436)
