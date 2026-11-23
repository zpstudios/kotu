# KOTU 현재 상태와 인수인계

## 현재 상태

- v0.433.2는 시작 메뉴의 항목 사이·구분선·여백으로 뒤 콘텐츠가 비치던 문제를 수정했다. 기존 메뉴 배치와 디자인별 버튼 모양은 유지한다. [A429](REQUIREMENTS.md#a429) · [공식 릴리스](https://github.com/zpstudios/kotu/releases/tag/v0.433.2).
- A429 제품 커밋 [`06b2db2`](https://github.com/zpstudios/kotu/commit/06b2db2472557a6decd54fafc8616da2987b2f17)의 [전체 빌드](https://github.com/zpstudios/kotu/actions/runs/45125165342)와 [공식 배포·설치 검사](https://github.com/zpstudios/kotu/actions/runs/45125165315)가 2026-11-23 통과했다. 공개 태그·최신 릴리스·설명·자산 8개·피드, 실제 Full·Delta 크기·SHA-1·SHA-256, Setup SHA-256, 글꼴·라이선스 29개와 진단 기능 제외를 검증했다. 근거는 `artifacts/a429-review/official-release/verification.json`과 `workflows.json`이다. Setup은 실행하지 않았다.
- v0.433.1은 Settings → Display에서 전체 UI 디자인 11종을 갤러리로만 제공하고 카드 선택 즉시 적용·Applied 표식을 표시한다. 제거한 별도 보기의 복원 기준은 v0.433.0 제품 커밋 `db101e7`이다. [A428](REQUIREMENTS.md#a428) · [선택·복원 사양](features/shell/design-library-A427.md#설정에서-선택) · [공식 릴리스](https://github.com/zpstudios/kotu/releases/tag/v0.433.1).
- A428 제품 커밋 [`7892fe2`](https://github.com/zpstudios/kotu/commit/7892fe29d5a8afb03bd7ca9ba81730df66de2ee3)의 [전체 빌드](https://github.com/zpstudios/kotu/actions/runs/45120772965)와 [공식 배포·설치 검사](https://github.com/zpstudios/kotu/actions/runs/45120772970)가 2026-11-23 통과했다. 공개 태그·설명·자산·피드, 실제 Full·Delta 크기·SHA-1·SHA-256, Setup SHA-256, 동봉 글꼴·라이선스 29개와 진단 기능 제외를 확인했다. 근거는 `artifacts/a428-review/official-release/verification.json`과 `workflows.json`이다. 실제 갤러리 검수와 한계는 [실기기 확인 목록](DEVICE-CHECKS.md#native-media)에 유지한다.
- A427 제품 커밋 [`db101e7`](https://github.com/zpstudios/kotu/commit/db101e765d12a35a77f954d2674b18bb912221cc)의 [전체 빌드](https://github.com/zpstudios/kotu/actions/runs/45116084495)와 [공식 배포·설치 검사](https://github.com/zpstudios/kotu/actions/runs/45116084502)가 2026-11-23 통과했다. 공개 태그·설명·자산·피드, 실제 Full·Delta 크기·SHA-1·SHA-256, Setup SHA-256, 동봉 글꼴·라이선스 29개와 진단 기능 제외를 확인했다. 근거는 `artifacts/a427-review/official-release/verification.json`과 `workflows.json`이다. 실제 화면 검수의 한계는 [실기기 확인 목록](DEVICE-CHECKS.md#native-media)에 유지한다.
- v0.432.2는 뒤로 가기로 파일을 닫을 때 좌우 패널 배치를 유지한다. [A112](REQUIREMENTS.md#a112) · [공식 릴리스](https://github.com/zpstudios/kotu/releases/tag/v0.432.2).
- A112 제품 커밋 [`d87dc7d`](https://github.com/zpstudios/kotu/commit/d87dc7db79a8674fdd2799874c0587523d269c63)의 [공식 빌드](https://github.com/zpstudios/kotu/actions/runs/44888855105)와 [배포·설치 검사](https://github.com/zpstudios/kotu/actions/runs/44888855037)가 2026-11-21 통과했다. 최신 정식 태그·제품 커밋·공개 설명·자산·업데이트 피드를 대조했고, Full·Delta 실제 다운로드의 크기·SHA-1·SHA-256도 일치했다. 로컬 근거는 `artifacts/a112-review/release-verification.txt`, 패널 검수는 [실기기 확인 목록](DEVICE-CHECKS.md#editor)과 `artifacts/a112-review/probe/result-2.txt`에 있다.
- v0.432.1은 문서를 열거나 Esc로 돌아올 때 공통 하단 버튼의 위치를 유지한다. [A426](REQUIREMENTS.md#a426) · [실기기 검수](DEVICE-CHECKS.md#editor) · [공식 릴리스](https://github.com/zpstudios/kotu/releases/tag/v0.432.1).
- v0.432.0은 조작부의 아이콘과 글자에 코투 기하체를 기본 적용하고 얇은 선 스타일도 제공한다. [A425](REQUIREMENTS.md#a425).
- v0.431.4는 PDF·HWP 용지 경계를 표시하고 문서·이미지·영상 맞춤 메뉴에 아이콘과 짝을 이루는 글꼴을 적용한다. [A423](REQUIREMENTS.md#a423) · [A424](REQUIREMENTS.md#a424).
- v0.431.3은 HWP 페이지 보기에 문서 공통 맞춤 선택을 적용하고 한컴 고지를 Settings → About으로 옮긴다. [A422](REQUIREMENTS.md#a422).
- v0.431.2는 HWP 페이지 본문을 클릭한 뒤에도 Esc로 탐색기에 돌아간다. [A421](REQUIREMENTS.md#a421).
- v0.431.1은 이미지·오디오·영상 정보 패널에서 작은 파일의 크기를 탐색기와 같은 단위로 표시한다. [버전별 변경](RELEASE-v0.431.1.md) · [A420](REQUIREMENTS.md#a420).
- v0.431.0은 HWP 5.x 문서의 페이지·표·그림을 오프라인에서 표시한다. [버전별 변경](RELEASE-v0.431.0.md) · [A45](REQUIREMENTS.md#a45).
- v0.430.0은 다른 프로그램에서 같은 경로에 저장한 이미지를 뷰어에 다시 표시한다. [버전별 변경](RELEASE-v0.430.0.md) · [A419](REQUIREMENTS.md#a419).
- v0.429.1의 기본 문서 편집기는 라이트·다크 테마에서 형광 녹색 입력 커서와 검은 테두리를 표시한다. 줄 맨 앞에서도 테두리 전체가 보이도록 조정했다. [공식 배포 및 제품 커밋](https://github.com/zpstudios/kotu/releases/tag/v0.429.1) · [커서 검수](DEVICE-CHECKS.md#editor).
- v0.428.0의 정식 설치 파일과 v0.427.0 기준 차등 업데이트가 2026-11-18 공개됐다. Windows에서 정식 v0.427.0과 KOTU Test 동시 실행, 테스트판 제거 후 정식 파일·설정·등록 보존을 검증했다. [공식 릴리스](https://github.com/zpstudios/kotu/releases/tag/v0.428.0) · [제품 커밋](https://github.com/zpstudios/kotu/commit/91ee169484942d625cf9e17cdc586f8f0e1c3d26) · [설치 검증](https://github.com/zpstudios/kotu/actions/runs/44325584622).
- 지정 PC는 사용자가 v0.433.0으로 업데이트했고, 2026-11-23 공식 패키지와 설치 파일 1,177개의 SHA-256이 일치했다. 기존 설정 36개 키의 누락은 없었고 `hardware.traySensors`, `update.lastCheckedAt`, `explorer.lastFolder` 값 변경과 `display.design` 추가를 읽었다. 업데이트 전부터 있던 시작 오류 로그는 변경되지 않았다. 근거는 `artifacts/a427-review/installed-release-verification.json`, 이전 v0.432.2 설치·설정 백업은 같은 폴더의 `pre-update-backup/`이다. 과거 v0.427.0 백업 기록 경로 `%USERPROFILE%/KOTU-Validation/20261116-official-v0.427.0/`는 이번 조회에서 없었고 기존 복구 보존 자료는 건드리지 않았다.
- 11월 17일 복구 문서 4개의 대조와 중복 원문 폐기를 마쳤다. 독립 복구 보존본은 보호 대상이다. [나중에 반영한 요구와 폐기 근거](history/recovered-notes-review-A417.md).
- 문서 정리로 지침과 현재 상태를 줄이고, 과거 기록과 기능 자료를 목적별로 찾을 수 있게 했다. 문서 수정 후 `pwsh -File eng/Test-Documentation.ps1`로 확인한다. [문서 지도](README.md)

## 열린 작업과 다음 시작점

| 항목 | 다음 작업을 선택할 때 확인할 것 |
|---|---|
| 메뉴 배경 비침 A429 | v0.433.2 공식 배포·다운로드 검증을 완료했다. Core 718개·일반 빌드·독립 검토와 Light의 11개 시작 메뉴·Ink 정렬 메뉴·대표 툴팁 관측을 마쳤다. 근거는 `artifacts/a429-review/visual-review.json`과 `build-verification.json`이다. 사용자에게 Settings → Updates에서 최신 v0.433.2 적용·재시작을 요청했다. 요청 직전 지정 PC는 v0.433.0이며 공식 창은 조작하지 않았다. 적용 후 같은 폴더의 `verify-installed-release.ps1`로 설치 대조를 끝낸다. [요구](REQUIREMENTS.md#a429) · [실기기 경계](DEVICE-CHECKS.md#native-media) |
| 디자인 선택 단순화 A428 | v0.433.1 공식 배포·다운로드 검증과 실제 갤러리 검수를 완료했다. A429의 v0.433.2에 포함되므로 최신판으로 함께 설치 대조한다. 별도 보기의 Git 복원 기준과 A427/A428의 기존 진단 실패·검수 한계는 유지한다. [실기기 목록](DEVICE-CHECKS.md#native-media) · `artifacts/a428-review/visual-review.json` |
| 웹 업데이트 실기기 검수 A418 | 기존 `artifacts/a428-review/pre-update-backup/`의 설치 백업 1,179개를 다시 검증했고, 변동하는 다운로드 캐시를 제외한 현재 설치 1,177개가 일치해 재사용한다. 기존 백업과 A427 복구 자료는 보존한다. 최신 설정 37개 키는 `artifacts/a429-review/pre-update-request-settings.json`, 적용 직전 대조와 시작 오류 로그 기준은 같은 폴더의 `pre-update-request.json`, `backup-coverage.json`, `pre-update-startup-log.json`에 있다. 확인 시각만 바뀌었으며 v0.433.1은 캐시에만 있고 설치되지 않았다. 사용자 v0.433.2 적용 후 공식 파일·설정·실행·오류 로그 대조가 남았다. [공식 절차](development/official-release-and-install.md) |
| 문서 편집기 커서 A115 | 정식판 웹 업데이트 후 실제 문서의 줄 처음·중간·끝에서 가독성을 확인할 수 있다. [검수 범위](DEVICE-CHECKS.md#editor) |
| 문서 편집 A401 | 원격 검수에서 저장·선택·편집·인쇄 취소 등을 확인했다. 직접 접속 시 실제 한국어 IME와 연속 입력·스크롤 체감, 다른 테마·배율 및 극단 조건이 남았다. [편집기 검수](features/editor/local-text-editor-A401.md) |
| 이미지 A398 | v0.426.0에서 PNG/TIFF 썸네일과 APNG 일부 경로를 검수했다. 사용자 원본·다른 코덱·클라우드 캐시 조건 등은 부분 반영으로 남았다. [이미지 방향](features/image/exif-orientation-A398.md) |
| 한글 문서 보기 A45 | 실제 업무 문서와 한글의 PDF 출력을 대조해 글꼴 대체, 복잡한 표와 그림의 위치, 쪽 나눔 차이를 확인한다. [지원 범위와 검수](features/editor/office-text-preview-A45.md) |
| 녹음·녹화 | 일반 녹화 저장과 검은 프레임 수정은 v0.426.0에 포함됐다. 원격 환경에서 미뤘던 실기기 검수 및 앱 내 재생의 검증 한계를 구분한다. 빠른 녹음·트레이 A406/A407은 보류다. [녹화 검수](features/recording/recording-save-and-editor-fullscreen.md) · [보류 범위](features/recording/quick-recording-A406-A407.md) |
| 나머지 요구·결정 | [요구사항](REQUIREMENTS.md)에서 선택 항목과 관련 결정만 확인한다. [실기기 확인 목록](DEVICE-CHECKS.md)은 해당 동작을 검수할 때 조회한다. |

새 작업은 사용자가 선택한 범위에서 시작한다. `master`의 현재 경로를 확인하고, 이전 기록에 등장하는 OneDrive·Temp 사본이나 당시 검수 창을 작업 정본으로 가정하지 않는다. 예전 판단·승인·원본 관측은 [당시 기록의 절별 색인](history/handover-index-through-2026-11-17.md)에서 찾는다.
