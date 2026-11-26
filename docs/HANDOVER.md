# KOTU 현재 상태와 인수인계

## 현재 상태

- **공식 제품·지정 PC**: v0.434.0, 제품 커밋 `60771408a914b9208bf04fc5ae8d19ab74a283d2`. [공식 릴리스](https://github.com/zpstudios/kotu/releases/tag/v0.434.0). 해당 HEAD의 build `45746697808`·release `45746697828` 성공. 로컬 전체 App 빌드·1,531개 시험과 공식 CI의 전체 native 빌드·시험·패키지 검증을 마쳤다. 공개 태그·latest·8자산 목록·피드와 내려받은 Full/Delta/Setup의 해시를 대조했다.
- **설치 대조**: 2026-11-26 정식 Settings → Updates 다운로드·재시작 후 공식 패키지와 설치 1,179파일의 SHA-256·제품 커밋 일치. 업데이트 중 임시 최대화·화면 크기 보정으로 달라진 창 설정을 정상 종료 후 기준값으로 복원하고 공식본을 다시 실행했다. 최종 43설정키·값의 추가/누락/변경 0이며 최초 백업 대비 차이는 업데이트 확인 시각뿐이다. 새 공식 프로세스·H/W Info 복원과 기존 시작 오류 로그 불변을 확인했다.
- **현재 근거**: `artifacts/a437-review/`의 `official-release/verification.json`, `workflows.json`, `installed-release-verification.json`, `post-update-settings-restoration.json`, `official-update-ui.json`. 로컬 검증·28검수 설정 복원은 `validation.json`, 화면 관측과 남은 조건은 [DEVICE-CHECKS](DEVICE-CHECKS.md#a437), 요구 결과는 [A437](history/requirements-completed-index.md#a437)를 따른다.
- **보호 백업**: 교체 전 v0.433.9의 `artifacts/a437-review/pre-update-backup/` 1,182파일과 43설정키 기준을 재검증했다. 상위 폴더의 `backup-coverage.json`, `pre-update-request*.json`, `pre-update-revalidation.json`, 재시작 직전 설정 기준을 함께 보존한다. 이전 v0.433.8 백업·독립 복구 보존본·과거 경로 부재 사실은 [보호 자료 위치](history/evidence-index.md#protected-material)를 통해 회수한다. 문서 정리는 이 자료의 수정·삭제 근거가 아니다.
- **작업 환경**: `C:/Users/tsusa/github/kotu`의 master를 유지한다. 사용자 `.codex/`는 작업·검사 입력에서 제외하며 추적하지 않는다. 공식 앱 업데이트·재시작과 별도 검수 앱의 상시 승인은 [공식 절차](development/official-release-and-install.md)에 기록된 범위를 따른다.
- **문서 운영**: [A436](history/requirements-completed-index.md#a436)에서 실제 남은 일·완료 결과·확정 계약·검수 대기를 각각 정본으로 분리하고 고유 정보·근거와 기존 주소를 보존했다. [완료·인계 절차](development/maintenance-notes.md#document-lifecycle)와 역할·상태·중복·링크 검사, 회귀시험을 build/release에 연결했다.

## 열린 작업과 다음 시작점

- **A438 HWP·PDF 검색**: [검색 계약](features/editor/paged-document-search-A438.md)은 문서 체크포인트에 포함하며 v0.435.0 버전·릴리스 설명·제품 30파일은 미커밋 상태다. 초점·휠 두 수정과 독립 재검토, 전체 App publish·1,540시험·20프로젝트 검사를 마쳤다. 수정본 실행은 `GetCursorPos` 접근 거부로 실패했고 검수 설정 원본 복원·공식 설정 불변·KOTU 프로세스 없음을 확인했다. 다음은 입력 도구 복구 후 [최초 초점 조사](REQUIREMENTS.md#open-work)·[남은 화면 검수](DEVICE-CHECKS.md#a438) → 제품 커밋·공식 배포 → 현재 v0.434.0 새 백업·공식 설치 대조다. 근거는 `artifacts/a438-review/validation.json`과 `second-candidate-ui-restoration.json`; 새 화면 관측이나 공식 제품 변경은 없다. A437 잔여는 [기존 검수 행](DEVICE-CHECKS.md#a437)에 유지한다.
- **A439 디자인 철회·복원**: 사용자가 다중 스킨 제공을 철회하고 구현 보존·사용 중단, 통일성을 유지한 원래 테마와 메뉴 호버 등 누락 동작 복원, 스킨 도입 뒤 UI 지연 원인 조사·수리를 선택했다. [변경 계약](features/shell/design-library-A427.md#a439)에 따라 도입 전 `f74a289`와 도입 `db101e7` 이후의 런타임 경로·메뉴 상태를 읽기 전용으로 조사한다. 현재 Original 강제 선택이나 과거 코드 통째 회귀로 대체하지 않는다. A438과 제품 변경·배포 단위를 구분한다.
- **선택 밖 범위**: 남은 개발·조사·결정은 [REQUIREMENTS](REQUIREMENTS.md#open-work), 검수 대기는 [DEVICE-CHECKS](DEVICE-CHECKS.md), 확정 계약은 [기능 정본](features/product-contracts.md)에서 관리한다. A418의 테스트판 동시 설치 중 웹 업데이트는 일반 정식 업데이트 성공과 구분한 미관측 범위다. 녹음·녹화 실제 검증과 A406/A407 보류는 유지한다.

이전 세션의 배포·관측·정정 근거는 [공유 근거 색인](history/evidence-index.md)에서 찾는다. 완료·인계 때 이 파일은 최신 상태와 바로 다음 행동으로 고쳐 쓴다.
