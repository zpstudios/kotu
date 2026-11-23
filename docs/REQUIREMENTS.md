# KOTU 요구사항 — 열린 작업과 유효 결정

이 문서는 현재 열린 일·보류 조건·유효한 제품 결정의 정본이다. 현재 작업 선택과 다음 시작점은 [HANDOVER](HANDOVER.md), 기능 설계는 [문서 지도](README.md), 실제 관측·미검증 경계는 [DEVICE-CHECKS](DEVICE-CHECKS.md)를 따른다. 미선택 백로그를 자동으로 착수하지 않는다. A번호는 영구 ID이며 마지막 등록 번호는 **A429**이다.

완료 항목은 [A1–A417 결과 색인](history/requirements-completed-index.md)에 번호당 한 줄로 모았다. 그 색인의 고정 Git 원문·행 범위와 [과거 완료 상세](history/completed-requirements.md)에서 고유한 구현·재현·판단 근거를 회수한다. 날짜별 배포·시험 수치·해시 목록을 이 문서에 다시 누적하지 않는다. 기존 지침의 정본은 [CLAUDE.md](../CLAUDE.md)이며 이 파일에서 복제하지 않는다.

<a id="open-work"></a>
## 열린 작업과 보류

완료된 구현에 실기기 미확인만 남았으면 아래 개발 백로그로 되돌리지 않고 DEVICE-CHECKS에 남긴다. `부분`은 명시된 결과만 완료, `보류`는 다음 승인 없이 재개하지 않음, `재확인`은 현 구현과 대조 전 결함으로 단정하지 않음을 뜻한다.

| ID | 상태 | 현재 결과와 남은 범위 | 다음 시작점 |
|---|---|---|---|
| <a id="a430"></a>A430 | v0.433.3 공식 배포 완료, 사용자 설치 대조 대기 | 2026-11-23 사용자는 v0.433.2 업데이트 후 폴더명과 펼침·접힘 표시의 세로 줄 맞춤이 어긋난다고 보고했다. 첨부 화면에서는 표시가 이름보다 아래에 놓여 보이며, 스킨별 차이는 사용자 관측으로 확정하지 않았다. 트리 행 안에서 두 요소의 시각적 중심을 맞추고 펼침·접힘, 들여쓰기, 선택·키보드 조작과 디자인별 모양을 유지한다. | 원본은 `artifacts/a430-review/reference.png`. 수정본은 Light의 11개 디자인을 관측했다. [정렬 사양](features/explorer/tree-density-A384.md#a430-이름과-펼침-표시-정렬)과 [실기기 확인 범위](DEVICE-CHECKS.md#native-media)를 구분한다. |
| <a id="a429"></a>A429 | v0.433.2 공식 배포·설치 대조 완료 | 2026-11-23 사용자는 시작 메뉴 항목 사이가 투명해 뒤쪽 글자가 일부 보이고 지저분하게 느껴진다고 보고했다. 첨부 화면은 Folded paper 모양이며 다른 스킨은 아직 보지 않았다고 명시했다. 메뉴의 항목 간격·구분선·내부 여백을 연속된 배경으로 가려 가독성을 유지한다. 컨트롤 자신의 템플릿 루트만 배경 장식 대상으로 인정하도록 수정했다. | [디자인 적용 사양](features/shell/design-library-A427.md) · [실제 검수 범위](DEVICE-CHECKS.md#native-media). 사용자 원본은 `artifacts/a429-review/reference.png`. 항목 순서·간격·메뉴 밀착 위치와 고유 디자인은 유지한다. |
| <a id="a428"></a>A428 | v0.433.1 배포, v0.433.2 설치 대조 완료 | 2026-11-23 사용자는 디자인 선택을 갤러리 하나로 단순화하고 Single view·보기 전환·별도 큰 미리보기·현재 적용 디자인 보기·Open single view를 없애도록 요청했다. 카드 선택으로 적용하고 현재 적용 카드에 표식을 둔다. 제거 기능은 Git 이력에서 복원할 수 있게 보존하며, 앱의 선택 옵션으로 남기지 않는다. | [갤러리 선택과 복원 기준](features/shell/design-library-A427.md#설정에서-선택). 복원 기준은 v0.433.0 제품 커밋 `db101e7`이며 기존 소스를 별도 보관 사본으로 늘리지 않는다. |
| <a id="a427"></a>A427 | v0.433.0 공식 배포·설치 대조 완료 | 2026-11-21 승인한 11개 디자인을 전체 UI에 적용했다. v0.433.0의 Single/Gallery 두 보기는 A428 요청으로 갤러리 하나로 단순화한다. 사용자 업데이트 후 설치 파일 1,177개가 공식 패키지와 일치했고 기존 설정 키의 누락은 없었다. | [디자인 선택과 적용 사양](features/shell/design-library-A427.md) · [검수 범위](DEVICE-CHECKS.md#native-media). A425의 조작부 통일보다 넓은 범위다. |
| <a id="a112"></a>A112 | v0.432.2 | 마우스 뒤로 버튼이나 키보드 `Browser Back`으로 파일을 닫으면 탐색기가 닫기 직전의 좌우 패널 상태를 유지한다. v0.432.1에서 빈 탐색기로 전환하며 양쪽을 다시 열던 오류를 수정했다. | [검수 범위](DEVICE-CHECKS.md#image-cloud) · [뒤로 가기 사양](guides/user-guide.ko.md#36-뒤로-가기) · [변경 설명](RELEASE-v0.432.2.md) |
| <a id="a426"></a>A426 | v0.432.1 | 문서 탐색기와 열린 문서 사이를 전환할 때 양쪽에 보이는 하단 버튼은 활성 여부와 관계없이 같은 창 너비에서 자리를 유지한다. | [버전별 변경](RELEASE-v0.432.1.md) |
| <a id="a425"></a>A425 | v0.432.0 | 버튼·메뉴·쪽수·재생 시간과 아이콘의 획을 통일한다. 기본은 시안 ④ 코투 기하체, 시안 ① 얇은 선도 한 번에 전환한다. 영상·음악의 재생·일시정지·스피커·음소거를 포함한다. | [버전별 변경](RELEASE-v0.432.0.md) |
| <a id="a424"></a>A424 | v0.431.4 | 문서·이미지·영상의 맞춤 메뉴를 펼치면 네 항목의 글자와 아이콘이 같은 획 느낌으로 보인다. 마우스를 올렸을 때의 툴팁에는 시스템 글꼴을 사용한다. | [버전별 변경](RELEASE-v0.431.4.md) |
| <a id="a423"></a>A423 | v0.431.4 | PDF와 HWP 페이지 보기에서 흰 용지의 가장자리와 바깥 여백을 구분할 수 있어야 한다. | [버전별 변경](RELEASE-v0.431.4.md) |
| <a id="a422"></a>A422 | v0.431.3 | HWP의 맞춤 선택과 페이지 이동을 문서 공통 조작부로 통합하고, 한컴 고지는 설정의 About에서 확인한다. | [버전별 변경](RELEASE-v0.431.3.md) |
| <a id="a421"></a>A421 | v0.431.2 | HWP 페이지 본문을 클릭한 뒤에도 일반 창에서 Esc를 한 번 누르면 탐색기로 돌아온다. 마우스로 좌우 사이드바를 펼쳐도 같은 규칙을 적용한다. | [버전별 변경](RELEASE-v0.431.2.md) |
| <a id="a419"></a>A419 | v0.430.0 구현 | 이미지 뷰어에서 연 파일을 다른 프로그램으로 수정·저장하면 화면을 자동 갱신한다. 변경된 이웃 사진의 선읽기 캐시도 비운다. | Windows 임시 파일에서 덮어쓰기·이름 교체·연속 저장·폴더 전환을 확인했다. [버전별 변경](RELEASE-v0.430.0.md) |
| <a id="a418"></a>A418 | 구현·실기기 검수 대기 | 테스트 Setup을 정식판과 함께 설치해도 정식판에서 다음 GitHub 업데이트를 직접 받을 수 있어야 한다. | [패키지와 검수 절차](development/official-release-and-install.md) |
| <a id="a19"></a>A19 | 미착수 | H/W 랭킹 제출은 서버가 필요한 장기 후보다. 전송·서버·개인정보 범위 미정. | 서버와 제출 정책부터 정한다. [원문 위치](history/requirements-completed-index.md#a19) |
| <a id="a45"></a>A45 | 부분 · HWP 5.x 페이지 보기 | v0.431.0부터 HWP 5.x 페이지의 글·표·그림을 PC에서 읽는다. HWP·HWPX·ODT·ODS·ODP의 본문/표 셀 텍스트 보기도 제공한다. | 한글 원본과의 정밀 비교, 다른 형식의 페이지 보기, 차트·수식 호환성은 남아 있다. 원본 편집·저장·인쇄, 암호·DRM·배포용 HWP와 HWP 3.x는 지원 범위 밖이다. [구현·검수 범위](features/editor/office-text-preview-A45.md) |
| <a id="a115"></a>A115 | Monaco 구현 | 장식 ①~③은 v142에 구현했다. v0.429.0부터 기본 편집기의 라이트·다크 테마에서 입력 커서는 형광 녹색에 검은 테두리로 표시한다. 고대비에서는 기존 테마 색상을 사용한다. | 네이티브 TextBox의 커서 색 변경은 API 제약으로 보류했다. [커서 스타일](../src/KOTU.Module.Document/EditorSource/index.html), [실기기 검수](DEVICE-CHECKS.md#editor) |
| <a id="a142"></a>A142 | 네이티브 후속 검증 | ①②③⑤ v179 완료. ④ ¶/EOF 스크린샷은 2026-08-29 수신 후 A283→A291→A307으로 이관됐다. 설치형 v0.427.0 Monaco에서 마지막 빈 줄의 표식 배치를 확인했다. | 네이티브 편집기의 마지막 빈 줄 배치 실화면 검수 잔여. [편집기 검수](DEVICE-CHECKS.md#editor) |
| <a id="a281"></a>A281 | 조건부 보류 | 기본 브랜드 레벨 0에서는 노출되지 않는다. `branding.assetLevel`을 실제 1 이상으로 올릴 때 16px 중립 표식 가독성을 평가한다. 안 읽히면 16px만 KO/TU로 복귀하고 48px 이상은 유지. | 조건이 성립할 때만 착수. [원문 위치](history/requirements-completed-index.md#a281) |
| <a id="a363"></a>A363 | 백로그 | 광고 서버·분석 기능은 요청 등록만 완료. 데이터 수집 범위, 서버, 도구, 일정과 착수는 미정. | 별도 선택 시 정책·범위를 먼저 정한다. [원문 위치](history/requirements-completed-index.md#a363) |
| <a id="a380"></a>A380 | 원인 조사 | All Readable 파일 전환 중 프로세스 종료의 원인은 미확정. v375의 전이 진단 보강을 원인 수리로 세지 않는다. 2026-10-02 일시정지 MP3→F11→손상 ZIP 전환 제보가 기준이며 유효한 원인 스택/덤프는 없었다. | 새 재현 시 현재/회전 로그와 WER·덤프를 연결해 원인을 좁힌다. 파일 경로·내용·예외 메시지를 진단 로그에 추가하지 않는다. [원문 위치](history/requirements-completed-index.md#a380) |
| <a id="a398"></a>A398 | 부분 | EXIF·회전 배치 v404, PNG/TIFF 썸네일·APNG v426 완료. 다른 codec·사용자 원본·클라우드 제공자/캐시 상태와 일부 실제 입력 조합은 미확인. 합성 이미지 성공이 원제보의 원인 확정을 뜻하지 않는다. | [EXIF](features/image/exif-orientation-A398.md), [회전 배치](features/image/rotated-layout-A398.md), [검증 경계](DEVICE-CHECKS.md#image-cloud). 원본 확보와 제공자별 조건 분리. |
| <a id="a399"></a>A399 | 구현 완료·원인 미확정 | 요청 직전 보호 v392, 감시 병합 v393, 항목 재사용 v394, 조건부 재시도 v395 완료. 사용자는 제보 파일 형식/다운로드 상태를 기억하지 못했고 실제 재현 대상이 없다. | [OneDrive 조사](features/explorer/onedrive-thumbnail-investigation-A399.md), [검증 경계](DEVICE-CHECKS.md#image-cloud). 로컬 보유/온라인 전용/동기화 중, 최초 진입/재진입/Refresh를 분리한다. 원본 강제 다운로드·무한 UI 재시도 금지. |
| <a id="a401"></a>A401 | 부분·실기기 잔여 | 로컬 Monaco 편집기 v412 이후 사용자는 지정한 약 100만 자 문서에서 입력이 밀리지 않는다고 확인했다. 설치형 v0.427.0에서는 자동 입력 후 저장 정확성과 휠 왕복을 추가 확인했다. | 실제 한국어 IME·물리 입력과 스크롤 체감·입력 지연·고대비·극단 문서 조건이 남았다. [로컬 편집기](features/editor/local-text-editor-A401.md), [성능 조사](features/editor/document-performance-investigation-A401.md), [실기기 검수](DEVICE-CHECKS.md#editor) |
| <a id="a406"></a>A406 | 보류 | 메뉴 약 3초 누름 빠른 녹음·H/W 상태 표시. 미구현·미배포. B1a admission/B1b 후보 작업은 정식 통합 결과가 아니다. 2026-11-10/14 보류 지시 유지. | [빠른 녹음 설계](features/recording/quick-recording-A406-A407.md). 재개 승인 전 구현/실녹음 금지. 마지막 Record 음원 구성을 재사용한다. H/W 수집 주기 유지안은 아직 제안. |
| <a id="a407"></a>A407 | 보류 | 빠른 녹음 중 닫기→트레이·전용 표시. A406과 함께 미구현·미배포. | [같은 설계](features/recording/quick-recording-A406-A407.md). 확정 정책은 일반 닫기→녹음 계속/트레이, 트레이 Stop and save, 명시적 Exit→저장 후 종료. |

**사용자 판단 대기** — 셸 키 진단 스트립(`diag.shellKeyOverlay`) 제거는 사용자의 별도 지시와 A309 실기기 확인을 기다린다. 기본 꺼짐 설정을 유지한다. [당시 판단 근거](https://github.com/zpstudios/kotu/blob/070e21af070996ba3d78fb440db9b4552f75a723/docs/HANDOVER.md#L1813-L1815)

### 완료·흡수·종결에서 혼동하기 쉬운 항목

| ID | 현재 판정 | 상세/검증 |
|---|---|---|
| A4 | 자체 탐색기 권한 조사·OS 탐색기 임베드 재검토 폐기. 구체적 권한 재현 기록 없음. | [결과](history/requirements-completed-index.md#a4) |
| A170 | 2026-08-29 사용자 확정으로 미확인 종결. 통과나 원인 규명으로 바꾸지 않는다. | [과거 미확인 종결](history/device-checks-closed-unverified.md) |
| A331 | 좌 리스트를 현재 모듈만으로 거르는 변경은 v321에서 철회. All Readable은 읽을 수 있는 모든 형식 유지. | [결과](history/requirements-completed-index.md#a331) |
| A342·A345 | 진입 정지 계측/개선과 가상화 완료. 옛 500개 상한의 유지/복원 질문은 가상화로 소진. 스트리밍 열거 후보는 별도. | [가상화](features/explorer/virtualization-research-A345.md), 후보 41 |
| A347 | A346에 흡수. 좌·중앙 순서 어긋남이 현 버전에서 재현될 때만 새 문제를 등록한다. | [결과](history/requirements-completed-index.md#a347) |
| A352 | v345~349 완료. UI 스레드 StorageFile 동기 호출의 COM 펌프→XAML 재진입을 덤프로 확정하고 수리. 옛 ‘미반영’ 제목을 현재 상태로 읽지 않는다. | [결과](history/requirements-completed-index.md#a352) |
| A382 | 편집 중 Alt+Enter와 녹화 저장 후속은 v426 정식 배포 완료. 옛 v425 이후 ‘배포 전’ 체크포인트는 이력. | [통합 후속](features/recording/recording-save-and-editor-fullscreen.md) |
| A383 | 상위 첫 항목 요구는 후속 A416·A417에서 다뤘다. 예전 `..` 타일 모양 제안은 현재 아이콘 계약을 덮어쓰지 않는다. | [A416](features/explorer/explorer-selection-layout-A416.md), [A417](features/explorer/explorer-refresh-A417.md) |
| A402·A403·A405 | 카메라 소스·시작 3버튼·공통 저장 폴더 구현/배포 완료. 실제 장치 실패·활성 녹음/녹화 통합 검증은 별도이며 현재 보류. | [녹음·녹화 검증](DEVICE-CHECKS.md#recording) |
| A408·A409·A411·A412 | 구현/배포 완료. A408의 최신 맞춤 메뉴는 v427이다. 실제 클립보드·테마·네이티브 재생의 미실행 조합은 남긴다. | [실기기 확인](DEVICE-CHECKS.md) |
| A413–A417 | 후속 UI·탐색기 개선 완료. 복구 문서 4개의 대조도 완료했으며 같은 요구를 새 백로그로 등록하지 않는다. | [UI 후속](features/shell/ui-followups-A413-A415.md), [A417](features/explorer/explorer-refresh-A417.md), [복구 대조](history/recovered-notes-review-A417.md) |

<a id="candidates"></a>
## 부록 A-2 — 등재 후보

후보 번호 1–44는 출처를 찾기 위한 번호이며 A번호와 다르다. 후보 자체는 착수 승인이 아니다. 오래된 구현 사실은 현재 소스와 이미 완료된 A항목을 먼저 대조한다. 승격되면 해당 A번호와 결과 한 줄만 남기고 중복 과제를 만들지 않는다. **미확인 후보를 임의로 완료 처리하지 않는다.**

아래 원문 행은 [고정 원문 부록 A-2](https://github.com/zpstudios/kotu/blob/070e21af070996ba3d78fb440db9b4552f75a723/docs/REQUIREMENTS.md#L3662-L3897) 기준이며 고유한 재현·설계 근거를 그대로 회수할 수 있다.

| 후보 | 현재 분류 | 남은 범위 또는 소진 근거 | 원문 행 |
|---|---|---|---|
| 1 | 소진 A323 | 같은 폴더 재생성 제거·실제 재스캔의 표시 복원. | L3667–3674 |
| 2 | 재확인 | 이미지 ←/→가 숨김 이미지에 도달하는지. A346의 표시 순서 통합과 먼저 대조. | L3675 |
| 3 | 재확인 | A108 안내 문구 세로 중앙 잔존·버튼/표면 동반 정렬. | L3676 |
| 4 | 통합 후보 | ShowModule ContentOpened stale-view 가드. 후보 25·32와 같은 축. | L3677 |
| 5 | 재현 안 함 | 바 임계는 자체 폭 기준. A305→A312 파생 폭 변경은 반영됐고 상수 재계수 0건. | L3678–3685 |
| 6 | 결정 대기 | 영상·오디오·H/W 드라이브 줄. 사용자 선택 전 추가하지 않음. | L3686 |
| 7 | 결정 대기 | 페이지·탐색 조작의 버튼화. 현재 A349 등과 범위 재대조. | L3687 |
| 8 | 후보 | 오디오 EQ 개별 밴드 슬라이더. 기존 프리셋과 별도. | L3688 |
| 9 | 소진 A329 | 문서·압축 IContentInfoProvider와 열림/선택 공통 빌더. | L3689–3695 |
| 10 | 재현 안 함 | H/W 모드 2 빈 화면의 옛 경로는 A305 구조에 없음. 재현 시 새 등록. | L3696–3701 |
| 11 | 소진 A345 | 좌 리스트·타일 수동 전량 생성은 가상화로 대체. | L3703 |
| 12 | 대조 후 판단 | 마크다운 렌더 트리 분할. A193 완료 범위와 남은 대형 입력을 대조. | L3704 |
| 13 | 대조 후 판단 | 썸네일·상세의 상한 병렬화/이미지 이웃 선읽기. A194·A200 등 후속 범위와 대조. | L3705 |
| 14 | 소진 A195 | 컨텍스트 메뉴 레지스트리 등록 워커 이관. 설정 JSON 저장 후보 39와 다른 축. | L3706, L3840–3842 |
| 15 | 부분 | 전체화면 비영상 자동 숨김은 A311 완료. 창 모드 확대·커서 숨김 잔여, 오디오 pause 부상 계약 확인. | L3709–3717 |
| 16 | 후보 | 리스트 전체 체크 해제 제스처. 현재 A416 선택/체크 계약과 대조. | L3718 |
| 17 | 후보 | 상세 줄의 비PDF 줄 수·7z/rar 압축률. A329 정보 패널 완료와 구분. 줄 수는 4MB 초과 빈칸 원칙. | L3719–3726 |
| 18 | 결정 대기 | 우측 정보 패널 드롭을 모듈 기준 라우팅과 맞출지. | L3727 |
| 19 | 소진 A308·A319 | 가이드의 Explorer integration 정정. | L3728–3729 |
| 20 | 대조 후 판단 | 마크다운 렌더 줌·편집 중 실시간 미리보기·키보드 스크롤. A401 도입 범위와 분리. | L3730 |
| 21 | 재확인 | 회전 후 하단 바·정보 패널 해상도 축 스왑. A398 완료 범위와 대조. | L3731 |
| 22 | 재확인 | S4×전체화면 스냅샷 겹침. A305가 옛 스냅샷을 폐지했으므로 전제부터 확인. | L3734–3739 |
| 23 | [A419로 승격](#a419) | 외부에서 저장한 이미지 자동 갱신. | L3740–3741 |
| 24 | 소진 A308·A319 | 가이드 정렬 Type 누락 정정. | L3742–3743 |
| 25 | 통합 후보 | Unloaded 후 늦은 스캔 continuation. 후보 4·32와 함께 현 수명 구조 확인. | L3744–3745 |
| 26 | 보류 | 비관리자 NVMe 온도 조회는 추정. 실기기 성립을 먼저 확인한 뒤 Core 훅 검토. | L3746–3747 |
| 27 | 소진 A325 | 내부 탐색 후 창 제목·트레이 툴팁 갱신. | L3748–3752 |
| 28 | 소진 A349 | 수동 이전/다음 파일 키는 Ctrl+←/→·PageUp/Down으로 구현. | L3753, L3630 |
| 29 | 조건부 보류 | 센서 승격 수집 서비스 상주. 전체 센서 무승격 수요가 생길 때 별도 등록. | L3754 |
| 30 | UX 후보 | 바 버튼 클릭 직후 Enter 재실행은 ButtonBase 정상 우선순위. 불편 재현 시 포커스 이동 검토. | L3755–3757 |
| 31 | 소진 A309 | F11/F12 패널 닫힘 후 null 포커스 방어. | L3758–3762 |
| 32 | 통합 후보 | ContentOpened 늦은 발화가 다른 뷰의 기준 경로·제목을 바꾸는지. 후보 4·25와 함께 확인. | L3763–3767 |
| 33 | 성능 후보 | 정보 예열을 이미지 외 오디오·영상·문서·압축으로 확대할 이득과 썸네일 작업 예산을 실측. | L3768–3778 |
| 34 | 재현 필요 | libvlc EQ 열거 실패 후 Off만 남는 경우 재시도. 정상 빈 배열 가능성도 구분. | L3779–3787 |
| 35 | 주석 감사 후보 | ‘다음 EOF부터 정상’ 가정은 다음 사건이 보장되는지 확인. 동작 수리와 낡은 주석을 구분. | L3788–3803 |
| 36 | 실측 필요 | 오디오/영상 재생 워커와 폴더 스캔·정보 조회 직렬 병목. 조회 분리 전에 체감/타이밍 측정. | L3804–3813 |
| 37 | 재확인 | EnsurePlaylist 스캔 실패→null 이후 재시도 없는 경로. A330 목록 루프와 자동 다음곡을 구분. | L3814–3822 |
| 38 | <a id="a420"></a>A420 완료 | 이미지·오디오·영상 정보 패널의 크기 표기를 탐색기의 B/KB/MB/GB 기준으로 통일했다. [버전별 변경](RELEASE-v0.431.1.md) | L3823–3830 |
| 39 | 성능 후보 | 항해 시 ISettings.Save 동기 JSON 쓰기. UI I/O 금지 적용, 디바운스 시 마지막 위치 유실 고려. | L3831–3842 |
| 40 | 코드 감사·미확인 | 클라우드 전용 원본 비접근 계약의 상세 줄·중앙 텍스트 미리보기·재생 전 조회 적용 여부. 자동 다운로드 알림 사용자 제보는 허위로 철회됐고 디스크 증가도 다른 파일 반입으로 정정됨. 실제 hydration 관측 0건이며 구름→체크 상태 변화도 미확인. | L3843–3871 |
| 41 | 대형 설계 후보 | 스트리밍 열거·경량 Entry로 첫 화면 지연 완화. 가상화 완료와 별도이며 크기/날짜 정렬의 전수 조회 비용을 먼저 설계. | L3872–3879 |
| 42 | 소진 A345 | 재실체화된 컨테이너에 미리보기 완료 적용, v338. | L3880–3884 |
| 43 | 흡수 A349 | 자동 다음 파일 순서=탐색기 표시 순서. ←/→는 시간 이동이므로 대상이 아니다. | L3885–3891 |
| 44 | 소진 A355 | UI 스레드 StorageFile 잔여 4곳 워커 이관. | L3892–3894 |

<a id="current-decisions"></a>
## 부록 B — 현재 적용할 결정과 출처

아래는 현재 작업에서 오해하기 쉬운 유효 계약이다. 기존 B2–B111의 110건 원문 위치는 [결정 색인](history/requirements-decisions-index.md)에 있다(B1은 정리 전 문서에도 없음). 옛 결정의 고유 내용은 삭제된 것이 아니며 관련 작업을 선택했을 때 해당 행을 조회한다. 후속 반전이 있는 키·배율·모드·폴더 정책은 최신 기능 정본을 따른다. 과거 단일 배치의 자동 진행 승인·모델 이름을 현재 전체 백로그 승인으로 확대하지 않는다.

| 주제 | 현재 적용할 계약 | 원래 결정/후속 정본 |
|---|---|---|
| 이름·개인정보 | 앱 이름 KOTU (King Of The Util), 신규 표시명 KOTU. 개인 실명·개인 메일을 제품에 표시하지 않는다. | [B9·B29](history/requirements-decisions-index.md#b9) |
| 메뉴·모듈명 | 모듈명은 단수형/Archive. 메뉴·연결 순서와 키는 옛 표가 아닌 현 가이드가 정본. | [B12·B16](history/requirements-decisions-index.md#b12), [문서 지도](README.md) |
| 파일 연결 | exe 경로 변경 시 조용한 재등록; 대표 확장자로 판단, 실패는 다음 실행 재시도. Standalone 차단, UserChoice 직접 수정 금지. 모듈 묶음 토글은 A326의 확장자별 상태를 사용. | [B24–25](history/requirements-decisions-index.md#b25), [B80](history/requirements-decisions-index.md#b80) |
| 인스턴스·트레이 | 인스턴스당 트레이 아이콘 1개. H/W 선택 저장은 전역 1벌·마지막 조작 우선. 최소화와 닫기는 별개이며 빠른 녹음 닫기 정책은 A407 보류 범위. | [B18·B21·B42·B45](history/requirements-decisions-index.md#b42) |
| 브랜드 | 제공된 원본 에셋 보존, 단계형 적용. 16px 조건부 재검토는 A281만 해당. | [B28·B41](history/requirements-decisions-index.md#b41) |
| 문서 저장 | 경고만 하고 자동 강행하지 않으며 사용자에게 선택 기회를 준다. 저장 전 검출은 진행/취소, 저장 후 검출은 재시도/다른 이름 저장을 제안한다. 현재 저장 재검증 계약에 따라 원본과 편집을 보호한다. settings.json은 실제 형식 그대로 취급한다. | [B37](history/requirements-decisions-index.md#b37), [B56](history/requirements-decisions-index.md#b56) |
| 문서·미디어 맞춤 | Fit의 표시값/실제 배율을 일치시킨다. B92의 옛 PDF 1:1 기본값은 A338→A374의 Contain으로 대체됐으며 텍스트 기본값과 혼동하지 않는다. | [B57·B92](history/requirements-decisions-index.md#b92), [A381](features/editor/pdf-viewport-fit-2026-10-02-A381.md), [A411](features/media/video-fit-A411.md) |
| 모드·S4·포커스 | S4는 모달 계층, 전체화면+S4의 첫 Esc는 S4 닫기. 모드 계단·뒤로 키와 null 포커스 방어를 함께 지킨다. 사이드바 기본 양쪽 열림/오버라이드 수명은 모듈 전환까지. S4 투명도는 화면 모드별 계약이며 전체 불투명화를 일괄 적용하지 않는다. | [B79–88](history/requirements-decisions-index.md#b79), [B94](history/requirements-decisions-index.md#b94) |
| 상태·선택 | 꺼짐은 형상, 밝기는 활성/비활성 축에 사용. 열린 파일 표시·선택·포커스를 구분한다(A351/A417). 상태 표시와 버튼 실행 동작을 혼동하지 않는다. | [B89·B95·B109](history/requirements-decisions-index.md#b89), [A417](features/explorer/explorer-refresh-A417.md) |
| 트리·목록 | 트리 동기화는 실제 표시 폴더 기준. All Readable 좌 리스트는 전 모듈 합집합이며 재생 목록 필터로 바꾸지 않는다. | [B96](history/requirements-decisions-index.md#b96), [B101](history/requirements-decisions-index.md#b101) |
| 시작 위치 | 앱 재시작·새 창은 전역 마지막 폴더를 사용한다. 빈 모듈 전환에서는 위치 유지, 파일을 여는 전환은 그 파일의 폴더로 이동한다. 옛 모듈별 키를 임의 청소하지 않는다. | [B71](history/requirements-decisions-index.md#b71) |
| 정보 패널 | 포맷 정의 속성은 값이 비어도 라벨을 남기고 선택/열림은 같은 빌더를 공유한다. 스트림 배열·GUID 전용·본문성·셸/서비스 합성·포맷 밖 값·GPS는 제외. 사람이 읽을 값도 오는 코덱 키는 남기고 GUID 값만 거른다. 포맷에 없는 키는 만들지 않으며 기존 계산값만 재사용하고 상한 초과 수치를 전체처럼 표시하지 않는다. 타일/트레이의 빈 조각 생략은 별도 규칙이다. 조회는 워커·일괄→개별→라벨 폴백과 취소/경로 방어를 유지. | [B97–98 및 하위 8규칙](history/requirements-decisions-index.md#b98) |
| 가이드 | 영어 Markdown→영어 site→한국어 Markdown→한국어 site 순서. 한국어에서도 UI 문자열·버튼·키는 영어 원문, HTML 두 언어 앵커는 동일. README는 개요/링크를 맡는다. `.ko.` 규칙 유지. | [B90–91](history/requirements-decisions-index.md#b91) |
| 재생 이동 | 영상/오디오 ←/→는 시간 이동. 이전/다음 파일은 버튼·Ctrl+←/→·PageUp/Down. 수동 이동은 자동 반복 예산을 소모하지 않고 끝에서는 해당 버튼 비활성. SMTC는 창당 1세션. | [B110](history/requirements-decisions-index.md#b110), [A349](history/requirements-completed-index.md#a349) |
| 반복 재생 | 루프 없음/목록/파일은 상호 배타·기본 루프 없음. 횟수 1은 한 번 더 재생을 뜻하며 목록 무한 고정이라는 옛 결정은 A255로 개정됐다. 숨김 파일 제외·후속 표시 순서 계약을 지킨다. | [B76](history/requirements-decisions-index.md#b76), [A255](history/requirements-completed-index.md#a255) |
| 인쇄 | OS 표준 대화상자와 해당 모듈 Ctrl+P/하단 버튼. 텍스트는 편집기 글꼴·인쇄 가능 영역 여백·머리글 없음·대형 블록 분할, 이미지는 Contain 고정. 해당 업데이트가 없는 구형 Win10은 안내만 하며 GDI 대체 경로를 만들지 않는다. | [B78](history/requirements-decisions-index.md#b78) |
| H/W·장치 | 긴 그래프는 주기와 무관하게 최근 5분. H/W는 콘텐츠 열림/닫힘 색 규칙의 예외다. 오디오 입력 장치 선택은 Windows 기본 장치 변경이며 단순 앱 내부 선택으로 바꾸지 않는다. 비관리자 센서 회수와 승격 서비스 상주는 별개. | [B67–70](history/requirements-decisions-index.md#b67), [B76](history/requirements-decisions-index.md#b76), 후보 26·29 |
| 디스플레이 배율 | 설정 창이 있는 모니터에 즉시 적용하고 실패 시 Windows 디스플레이 설정으로 안내한다. 모니터 선택 UI 신설은 원래 범위에 없다. | [B76](history/requirements-decisions-index.md#b76), [배율 조사](features/shell/display-scale-research-A48.md) |
| 기록 저장 폴더 | 다른 기존 모드별 경로는 Documents/KOTU로 통일. 기존 파일은 이동하지 않는다. 이후 공통 폴더 선택은 모든 모드 공유. | [B111](history/requirements-decisions-index.md#b111), [A405](features/recording/common-recording-folder-A405.md) |
| 빠른 녹음 | 마지막 Record 음원 구성을 재사용. 일반 닫기→녹음 계속/트레이; Stop and save; 명시 Exit→저장 후 종료. 이 정책 확정은 보류된 A406/A407 재개 승인이 아니다. H/W 실제 수집을 기존 주기로 유지한다는 안은 미확정. | [B111](history/requirements-decisions-index.md#b111), [A406·A407](features/recording/quick-recording-A406-A407.md) |
| 클라우드 원본 | 썸네일·속성 표시를 위해 온라인 전용 원본을 열거나 강제 다운로드하지 않는다. 제공 지연/실패는 경로별 예산·취소·늦은 결과를 구분하고 캐시 실패를 영구 고정하지 않는다. | 후보 40, [A399 요청 보호](features/explorer/request-time-cloud-guard-A399.md), [B103](history/requirements-decisions-index.md#b103) |
| 성능 판단 | 증상과 제기의 전제를 분리하고 원인은 측정으로 좁힌다. UI의 1회성 동기 I/O도 피하며 워커 우선순위/취소/배압을 함께 본다. ‘값 변경’과 ‘선택지 추가’를 구분한다. | [B99–109](history/requirements-decisions-index.md#b99), 운영 규칙은 [CLAUDE](../CLAUDE.md) |

## 다음 시작점

현재 선택된 작업은 HANDOVER에서 확인한다. 이 목록을 갱신할 때는 결과/남은 조건을 덮어쓰고, 해당 기능 정본과 실기기 경계를 링크한다. A406 H/W 주기처럼 아직 제안인 정책은 관련 배치가 선택됐을 때 확정한다. A347의 재현 자료나 A399의 원본이 없다는 사실을 모든 세션의 반복 질문으로 만들지 않는다.
