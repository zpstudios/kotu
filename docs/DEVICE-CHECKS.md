# KOTU 실기기 확인 — 현재 경계와 필요한 다음 검증

현재 제품 버전과 선택된 작업은 [HANDOVER](HANDOVER.md)를 따른다. 이 파일은 아직 결론을 넓힐 수 없는 검증 범위의 정본이며 날짜별 시험일지가 아니다. 아래 미확인은 통과·결함 확정을 뜻하지 않는다. 사용자가 보류한 녹음·녹화/빠른 녹음은 재개 승인 전 실행하지 않는다. 실제 앱 화면 확인·조작 전에는 운영 규칙의 사전 질문을 지킨다.

선택된 변경과 관련된 검증만 묶어서 수행한다. 외형 변경은 실제 화면을 확인하고, 저장·IME·녹음/녹화·네이티브 재생·장치 수명은 해당 동작의 실제 검증 필요성을 따로 판단한다. 정적 검사·합성 provider·격리 probe를 정상 앱·실제 입력·물리 장치 성공으로 바꾸지 않는다. 공식 자산/설치본의 해시 대조는 계속 수행하되 긴 해시 목록을 이 문서에 반복하지 않는다.

과거 상세는 [실기기 시나리오·관측 색인](history/device-checks-index.md)의 주제/버전/고정 Git 줄 위치에서 찾는다. v0.161.0 이하와 A170은 사용자 결정에 따른 [미확인 종결](history/device-checks-closed-unverified.md)이며 통과가 아니다. 이후 오래된 미확인도 임의로 종결하지 않는다. 과거 `artifacts/...` 주소는 당시 근거 위치이며 현재 저장소에 파일이 있다고 가정하지 않는다.

<a id="editor"></a>
## 문서·편집기

| 범위 | 확인된 경계 | 남은 확인과 착수 조건 | 정본/원문 |
|---|---|---|---|
| A426 문서 도구 모음 | 사용자가 v0.432.1 독립 실행본에서 좁은 창과 넓은 창의 너비를 각각 고정한 채 PDF·HWP를 열고 Esc로 탐색기에 복귀했다. 네 경우 모두 양쪽 화면에 공통으로 보이는 하단 버튼의 위치가 유지됐다. | HWP 페이지 보기에는 인쇄 버튼이 없어 화면 맞춤·맞춤 메뉴·전체 화면을 비교했다. | [A426](REQUIREMENTS.md#a426) |
| A401 로컬 편집기 | 2026-11-18 CC0028 설치형 v0.427.0 Monaco 다크에서 910,045자(UTF-16)·10,003줄 문서에 Enter 12회와 `kotu`를 개별 입력했다. 저장본 전체가 기존 CRLF를 유지한 예상 내용과 일치했다. 자동 휠로 끝에서 9,875행까지 올린 뒤 끝으로 돌아왔다. 이전 사용자 ‘입력이 밀리지 않음’ 확인은 당시 지정 문서·환경에 한정. | 자동화에서 한/영 키를 지원하지 않았고 대체 키로는 `gks`가 입력됐다. 실제 한국어 IME 조합, 물리 키 연속 입력·휠 체감과 입력부터 화면 반영까지의 지연, 고대비·극단 장문/긴 줄은 미검수. | [v412·v426 검수](features/editor/local-text-editor-A401.md), [성능 조사](features/editor/document-performance-investigation-A401.md) |
| A401 네이티브 대체 경로 | 과거 near1M/긴 줄256Ki Enter10·Undo·혼합 문자·줌/검색/모드 전환 관측. | 당시 held-key·고빈도 휠·초기 긴 줄 지연 미검증, 혼합 한글 wrap 글리프 잘림 원인 미확정. 현재 Monaco 주 경로와 구분하여 관련 경로를 바꿀 때 확인. | [과거 관측 L167](history/device-checks-index.md#l167), [visual line/search](features/editor/visual-line-search-A401.md) |
| A142→A283→A291→A307 | A291에서 글자가 있는 마지막 줄의 trailing 배치를 채택했고 A307(v294)에서 빈 마지막 줄을 수정했다. 위 설치형 Monaco의 문서 배율 100%·200%에서 10,002행에 ¶, 마지막 빈 10,003행에 ·EOF가 보였고 두 행의 번호와 수평 가이드가 맞았다. | 네이티브 편집기의 마지막 빈 줄 배치는 실화면 검수 잔여. | [판정/원문 L325](history/device-checks-index.md#l325), [과거 완료 상세](history/completed-requirements.md) A307 |
| A115 커서 | v0.429.1의 배포용 편집기 파일을 Chromium에서 렌더링했다. 라이트·다크 100%에서 줄 처음·중간·끝의 녹색 중심(2×19px)과 사방 검은 테두리(1px), 포커스 해제 시 숨김을 확인했다. 고대비·강제 색상에서는 사용자 지정 스타일이 적용되지 않았다. | 이 PC 정식 WinUI 설치본에서의 가독성은 웹 업데이트 후 확인할 수 있다. Standalone 네이티브 편집기는 기존 범위다. | [A115 판정과 근거](REQUIREMENTS.md#a115) |
| A396·A409 선택/검색 | 정상 다크 UI에서 편집·보기 선택, drag/Shift/Ctrl+A, 읽기 전용 보호·포커스/검색 관측. Viewing 선택은 후속 v407 결과가 기준. | 실제 Clipboard 복사, 라이트/고대비 픽셀·터치·다른 OS DPI, 좁은 폭·상한/접근 거부 조합은 별도. 초기 Viewing 실패 관측은 v407 후속과 구분. | [A409](features/editor/editor-background-A409.md), [v407 관측 L81](history/device-checks-index.md#l81) |
| A340·A341·PDF·인쇄 | 편집/보기 상태 분리, 마크다운 표·인쇄 분할, PDF Contain/여백/상단 고정 개선은 구현. | 실제 인쇄 제출·프린터별 결과·모든 테마/DPI 검증으로 확대하지 않음. 관련 변경 시 PDF 크기/바 가림/가장자리와 텍스트 인쇄를 분리 확인. | [PDF A381](features/editor/pdf-viewport-fit-2026-10-02-A381.md), [과거 시나리오](history/device-checks-index.md) |

<a id="image-cloud"></a>
## 이미지·클라우드·탐색기

| 범위 | 확인된 경계 | 남은 확인과 착수 조건 | 정본/원문 |
|---|---|---|---|
| A112 뒤로 가기 | v0.432.2 독립 실행 검수본에서 PDF를 열고 앱의 뒤로 가기 동작을 호출했다. 양쪽 닫힘·왼쪽만·오른쪽만·양쪽 열림 네 배치 모두 탐색기에 복귀한 뒤 실제 패널 너비가 유지됐다. Esc 동작은 네 배치 모두 양쪽을 열었다. 임시 탐색기에서 뒤로 가면 오른쪽 패널만 열어 둔 원래 PDF로 돌아왔다. | 물리 버튼·키 입력의 전달은 미검증. 사용자의 Mac Windows App 원격 세션에서는 Windows 파일 탐색기에서도 마우스 뒤로 버튼에 반응이 없었다. | [A112](REQUIREMENTS.md#a112) |
| A398 EXIF·회전·APNG | JPEG/TIFF 방향, PSD 일부 방향, 회전/배치/복사/인쇄 미리보기 관측. PNG eXIf/TIFF 썸네일/APNG 후속은 v426에서 보강. | 다른 codec·원제보 원본·실제 클라우드 cache/provider. Ctrl/Shift+휠, 자유 resize·mid-drag unload 등 당시 도구로 못 한 조합을 통과로 세지 않음. 실제 인쇄 제출·미설치 WhatsApp 붙여넣기 미검증. | [EXIF](features/image/exif-orientation-A398.md), [회전 배치](features/image/rotated-layout-A398.md), [과거 L133](history/device-checks-index.md#l133) |
| A399 OneDrive | 요청 직전 보호·감시 병합·재사용·조건부 재시도 구현 및 실제 WinUI 합성 provider 시나리오. | 실제 재현 파일/상태 미확보. 온라인 전용/로컬/동기화중 별 제공자 지연과 캐시 회복, hydration 부재, 취소 뒤 영구 잔류/장시간 S4·다중 창을 합성 성공으로 대체하지 않음. | [OneDrive 조사](features/explorer/onedrive-thumbnail-investigation-A399.md), [과거 L260](history/device-checks-index.md#l260) |
| A399·후보40 원본 비접근 | ReturnOnlyIfCached·placeholder 보호 코드 조사와 실제 원본 비접근 관측은 다르다. | 상세 줄·텍스트 미리보기·재생 전 속성 경로를 필요할 때 감사. 자동 다운로드 알림 제보와 디스크 증가 증거는 철회된 사실이며 재현 근거로 사용 금지. | [후보40](REQUIREMENTS.md#candidates), [요청 직전 보호](features/explorer/request-time-cloud-guard-A399.md) |
| A400·A410 이미지 복사 | 이미지 복사 경로·메뉴 구현, A398 정상 앱의 PNG/DIBV5/DIB 일부 복사 관측. | 대상 앱별 실제 붙여넣기와 clipboard 소유권·실패 복구는 해당 대상이 있을 때 검증. | [이미지 복사](features/image/image-copy-A400-A410.md), [과거 L229](history/device-checks-index.md#l229) |
| A385·A416·A417 탐색 | 썸네일 품질·선택/체크·상위 탐색·패널·트리 후속 구현/배포. | 좁은 영역·텍스트 확대·다른 DPI/모니터·테마·빠른 가상화 재사용/클라우드 조합의 옛 미확인 중 현재 기능 정본이 이미 다룬 범위를 먼저 대조. | [A385](features/explorer/thumbnail-quality-A385.md), [A417](features/explorer/explorer-refresh-A417.md) |
| A347 순서 | A346에서 좌 리스트/중앙 순서를 같은 목록으로 통합. | 현재 버전에서 어긋남이 재현되면 폴더·정렬·필터·화면을 특정하여 새 문제로 등록. 재현 없음을 통과 전수검사로 바꾸지 않음. | [A347](history/requirements-completed-index.md#a347) |
| A380 종료 | 진단 보강 v375 완료. | 원인 불명 종료가 재현되면 정확한 전환 순서와 로그/덤프를 함께 회수. A352의 다른 종료 원인 수리와 합쳐 종결하지 않음. | [열린 요구 A380](REQUIREMENTS.md#a380) |

<a id="recording"></a>
## 녹음·녹화 — 실제 동작 검증 보류

2026-11-10/14 지시에 따라 실제 녹음·녹화 검증과 A406/A407 빠른 녹음/트레이 개발은 보류한다. 저장 오류·흑색 프레임·Alt+Enter 후속 v426 배포 완료는 보류 전체의 해제를 뜻하지 않는다. 아래 기존 관측은 보존하고, 재개 시 선택된 묶음만 검사한다.

| 범위 | 확인된 경계 | 보류 중인 실제 검증 | 정본/원문 |
|---|---|---|---|
| A402 카메라 | 실제 카메라640×360 H.264 촬영 1회·다른 프레임을 확인. 약11초이며 목표5초 시험으로 세지 않음; audio0 샘플. | 카메라 재사용, 권한 거부/장치 분리/점유, 혼합 음원, 녹화 중 폴더 변경, 극단 형식·드라이버 정지. 이전 실패 원인은 미규명. | [카메라](features/recording/camera-sources-A402.md), [관측 L25](history/device-checks-index.md#l25) |
| A403 시작 3버튼 | 비촬영 정상 UI에서 선택·안내·Refresh·Settings·축소 폭 확인. Start/카메라 활성화는 0회. | 실제 녹음/화면/카메라 Start·Stop/Save/Discard, 활성 경합·실패 복구·첫 열거 밖 저장 소스. 빠른 녹음 admission은 별도 미통합. | [시작 조작](features/recording/record-start-controls-A403.md), [관측 L5](history/device-checks-index.md#l5) |
| A405 저장 폴더 | 공통 폴더 카드/Cancel·잘못된 파일 경로 오류, 두 창 폴더 전파와 재시작 기억 확인. | 활성 캡처 중 변경·실제 저장/부분 결과·실패 회수·카메라 연계. 기존 파일은 이동하지 않는 정책 유지. | [공통 저장 폴더](features/recording/common-recording-folder-A405.md), [관측 L43](history/device-checks-index.md#l43) |
| A388–A390·저장 후속 | 소스 목록·하단 조작·상태 표시 및 v426 저장/흑색 프레임 후속 구현. | 실제 장치 추가/제거·기본 변경·소스 소실 시 대체 금지, 시스템/마이크/무음, 저장/폐기/부분 회수·모듈 전환·트레이 전이. 과거 단위 검증을 실제 장치 성공으로 바꾸지 않음. | [저장/전체화면 후속](features/recording/recording-save-and-editor-fullscreen.md), [과거 L275](history/device-checks-index.md#l275) |
| A406·A407 | 설계·후보 상태. 정식 빠른 녹음/전용 트레이 구현은 없음. | 재개 승인 후 글로벌 admission·다중 창·닫기/Exit/Stop and save를 같은 세션 상태로 검증. 마지막 Record 음원 재사용은 확정, H/W 주기안은 미확정. | [빠른 녹음](features/recording/quick-recording-A406-A407.md) |

<a id="native-media"></a>
## 네이티브 재생·외형·오래된 확인

| 범위 | 확인된 경계 | 남은 확인 | 정본/원문 |
|---|---|---|---|
| A427 전체 UI 디자인 | 11개 실제 재실행·새 Settings, Light 전환·Dark 대표 새 창, 갤러리·미저장 문서·미디어·160행 선택 보존을 관측했다. Hardware 사양 재생성 수리 후 Render 세대 81개·글자 534건과 Record 부모·내부 글자 2,737쌍을 실제 모듈에서 읽었다. Archive 정렬, 메타 정보, 최대화·복원, 반투명 탐색기, 영상 탐색·음량 손잡이·간격 선택·휠을 확인했다. | 고대비는 캡처 제한으로 부분 확인, OS DPI는 원격 환경에서 변경 불가. 수동 창 크기·스크롤바 손잡이 드래그, 비활성 오디오 입력·물리 Browser Back은 미확인. run34 새 창/굵기 실패는 run36 비재현만으로 해결 판정하지 않았다. run36 Radio 실패는 native-only에서도 재현되며 전체 성공이 아니다. run38 글자 740개 일치와 준비·뷰포트·오프셋 7개 실패를 구분한다. 실제 화면·수동 관측을 원래 진단 판정의 통과로 바꾸지 않는다. v0.433.0 공식 배포·다운로드 검증과 사용자 업데이트 후 설치 1,177개 파일 대조를 완료했다. | [사양](features/shell/design-library-A427.md), `artifacts/a427-review/handoff.md`와 receipt17/run41·42 |
| A428 갤러리 선택 단순화 | v0.433.1 일반 검수본에서 11개 카드만 제공, Ink→Precision→Humanist→Ink 즉시 적용과 두 Settings의 Applied 표식 동기화, 마지막 카드까지 휠·하단 위치 유지·정상 종료를 확인했다. | 실제 3열 화면 기준. 키보드 활성화·다른 창 너비는 이번에 확인하지 않았다. A427의 기존 검수 한계는 유지한다. 공식 배포·다운로드 검증과 이전 설치 백업을 마쳤고, 해당 변경을 포함한 v0.433.2 사용자 업데이트 후 공식 설치 파일·설정 보존·실행 대조를 완료했다. | [선택·복원 사양](features/shell/design-library-A427.md#설정에서-선택), `artifacts/a428-review/visual-review.json` |
| A429 메뉴 배경 비침 | 수정 전 v0.433.1 Ink 시작 메뉴 틈으로 검수 이미지가 비치는 현상을 재현했다. v0.433.2 Folded paper는 같은 이미지 위에서 연속된 배경을 확인했고, 나머지 10개 디자인도 Settings 위에서 항목 간격·구분선·여백의 배경을 확인했다. Ink 일반 정렬 메뉴와 Esc 닫기, Solid·Isometric 툴팁 배경을 관측했다. 검수 디자인 Ink 복원·창 정상 종료 완료. | 이번 검수는 Light와 한 화면 크기 기준이며 Dark·고대비·DPI·다른 크기는 새로 검수하지 않았다. A427/A428의 기존 실패·미확인 경계는 유지한다. v0.433.2 공식 배포·공개 파일 검증과 이전 설치 백업 재검증을 마쳤다. 사용자 직접 업데이트 후 공식 설치 1,177개 파일·설정 37개 키 보존·재실행·기존 시작 오류 로그 동일성을 확인했다. 근거는 `artifacts/a429-review/installed-release-verification.json`이다. | [배경 소유권 사양](features/shell/design-library-A427.md#실제-요소-연결-원칙), `artifacts/a429-review/visual-review.json` |
| A412 영상 합성 | 정상 앱 재생/일시정지 중100/150/200 배율, EOF 지우기·native cap·fit·탐색/이어보기·독립 종료·두 영상 동시 변경 일부 확인. | Original 실시간 배율 변경·수동 modifier휠·가청 음성·첫 이어보기 프레임 시각·빠른 전이 전수. 격리 수명 probe와 실제 정상 앱 결과 분리. 모호한 검은 캡처는 통과 근거 제외. | [영상 합성](features/media/video-composition-A412.md), [관측 L117](history/device-checks-index.md#l117) |
| A408 맞춤 버튼 | 도형/상태색 정상·비활성·pressed·재활성·수명 검사와 일부 정상 다크 UI 관측. 최신 메뉴 v427. | 실제 라이트/고대비·다른 OS DPI·입력 조합은 최종 기능 정본의 수행 범위만 인정. 앱 배율과 OS DPI를 혼동하지 않음. | [원본 배율 아이콘](features/shell/original-icon-A408.md), [과거 L62](history/device-checks-index.md#l62) |
| A349–A354 재생 | A349 파일 이동/SMTC, A350 v343.2 정체성, A351 열린 표시와 A352 덤프 수리의 실제 확인 기록이 있음. | 이전 실패 버전343.0/.1, A352 진단 중간판을 현재 결과로 쓰지 않음. 전원 복구·네이티브 재생·장시간·가청 음성은 해당 증상/변경 시 실제 경계 재검토. | [버전별 관측 색인](history/device-checks-index.md) |
| H/W·OS 통합 | 옛50ms 허용 사용자 확인과 일부 UI 관측은 특정 환경 결과. | 비관리자 NVMe·IPolicyConfig·기본 앱 지정·장시간 센서 부하 등은 단위시험만으로 결론낼 수 없음. 후보26·29와 관련 변경 시 실제 장치/OS 조건을 정해 확인. | [후보](REQUIREMENTS.md#candidates), [과거 시나리오](history/device-checks-index.md) |
| v162 이후 미회수 시나리오 | 이전 검증 목록의 모든 주제/버전은 고정 원문 색인에 남아 있음. | 현재 변경/재현에 해당하는 것만 선택해 현 계약과 대조. 보존 이관을 통과·종결로 세지 않음. | [시나리오·원문 주소](history/device-checks-index.md) |

## 결과를 갱신할 때

검증한 환경·입력·정상 앱/격리 진단 구분·관측 결과와 남은 경계만 해당 행에 덮어쓴다. 실패를 후속 성공으로 덮어 숨기지 않고 기능 정본의 원인/수리로 연결한다. 결함이 확인되면 REQUIREMENTS의 기존 ID와 합칠 수 있는지 확인한다. 녹음/녹화의 실제 샘플, 저장/복구 보존본은 문서 정리 대상으로 삭제하지 않는다.
