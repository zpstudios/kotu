# KOTU 현재 상태와 인수인계

## 현재 상태

- **공식 제품·지정 PC**: v0.436.0, 제품 커밋 `ab6e992c043ddc8f015b7b1fe1e85414c81e03cd`. [공식 릴리스](https://github.com/zpstudios/kotu/releases/tag/v0.436.0). 해당 커밋의 build `45837085360`·release `45837085341` 성공. A439의 스킨 구현·저장 선택을 보존하고 활성 스킨 처리·갤러리를 끄며 네이티브 테마·상태를 복원했다. 로컬 계측 없는 publish·1,553시험·독립 검토와 공식 CI의 전체 native 빌드·시험·설치·Standalone 검증을 마쳤다.
- **설치 대조**: 2026-11-26 정식 Settings → Updates 다운로드·재시작 후 공식 Full과 설치 1,188파일의 SHA-256·제품 커밋 일치. 최초 재시작에서 달라진 `window.maximized`는 최대화·정상 종료·공식본 재실행으로 복원했다. 최종 43설정키·값은 재시작 직전 기준과 바이트 일치하며, 최초 백업 대비 차이는 업데이트 확인 시각뿐이다. 설정 파일을 덮어쓰지 않았다. 새 공식 프로세스·H/W Info 실행과 기존 시작 오류 로그 불변 확인.
- **현재 근거**: `artifacts/a439-review/`의 `official-release/verification.json`·`workflows.json`·`installed-release-verification.json`·`official-update-ui.json`. 공개 태그·latest·8자산·릴리스 본문·Full/Delta/Setup·업데이트 피드 해시 대조 완료. 로컬은 `validation.json`·`product-build-verification.json`·`test-results/`·`ui-restoration.json`, 성능·화면 조건은 [A439 정본](features/shell/design-library-A427.md#a439)에서 회수한다. 이전 A438 v0.435.0 배포·설치 근거는 [공유 색인](history/evidence-index.md)에 보존했다.
- **보호 백업**: 다음 교체용 v0.436.0의 `artifacts/a439-followup/pre-update-backup/` 1,191파일을 만들고 상위 `backup-coverage.json`으로 대조했다. 재생성·덮기 없이 설치 직전 재검증한다. 이전 v0.435.0의 `artifacts/a439-review/pre-update-backup/` 1,191파일과 상위 백업·설정·재검증 JSON도 보호한다. A438 v0.434.0 1,371파일, A437 v0.433.9 1,182파일·A435 v0.433.8·독립 복구 보존본은 [보호 자료 위치](history/evidence-index.md#protected-material)를 따른다.
- **작업 환경**: `C:/Users/tsusa/github/kotu`의 master를 유지한다. 사용자 `.codex/`는 작업·검사 입력에서 제외하며 추적하지 않는다. 공식 앱 업데이트·재시작과 별도 검수 앱의 상시 승인은 [공식 절차](development/official-release-and-install.md), 문서 정리는 [완료·인계 절차](development/maintenance-notes.md#document-lifecycle)를 따른다.

## 열린 작업과 다음 시작점

- **선택된 A439 후속 수리**: 메뉴 강조와 Preview 캐시·측정 전 치수 적용 수리의 정상 publish·무계측 UI·1,553시험·Standalone 빌드를 검증했다. 근거는 `artifacts/a439-followup/final-validation.json`·`final-normal-build.json`·`final-normal-ui.json`이다. 이 검수는 v0.436.0 후보에서 수행했고, 뒤의 v0.437.0 버전 반영 publish·소스/진단 부재 대조는 `versioned-normal-build.json`으로 확인했다. 필수 검사·독립 검토도 통과했다. [정본](features/shell/design-library-A427.md#a439)을 따라 제품 커밋·즉시 push → 공식 전체 native CI·자산/피드/해시 → 이 PC 공식 Updates 설치·파일/설정/로그 대조를 이어간다.
- **A439 조사·검수 경계**: `normal-source`는 root 원본으로 다시 복사했고 현재 고정 출력은 네 종류 probe·강제 테마·하네스가 없는 Test 앱이다. 기존 단회 성능 결과는 전체 입력 지연 해결·회귀 배수·GC 제거를 증명하지 않는다. 정상 UI에서 열 변경 후 재클릭 없는 End→Enter 경로를 확인했지만 전체 초점·정확한 픽셀 앵커·PDF 썸네일은 미확인이다. [기존 첫 WinUI 종료](REQUIREMENTS.md#a439)·[미관측 환경](DEVICE-CHECKS.md#a439)은 유지한다. 최신 `ui-restoration.json`에서 정상 종료·프로세스 없음·Test 28키 바이트 복원·공식 43키와 OS 불변을 확인했다.
- **A438 HWP·PDF 검색**: 구현·공식 배포·설치를 완료했고 현재 버전에도 포함된다. 인덱싱/검색 대기 중 휠·Ctrl+휠, pending/error 팝업·실제 IME·고대비/DPI·암호/초대형/빠른 교체의 미관측 조건은 [DEVICE-CHECKS](DEVICE-CHECKS.md#a438)에 유지한다. 과거 A438/A439 `uncommitted-product-manifest.json`은 당시 해시 근거이며 현재 미커밋 상태를 뜻하지 않는다.
- **선택 밖 범위**: 남은 개발·조사·결정은 [REQUIREMENTS](REQUIREMENTS.md#open-work), 검수 대기는 [DEVICE-CHECKS](DEVICE-CHECKS.md), 확정 사양은 [기능 정본](features/product-contracts.md)에서 관리한다. A418의 테스트판 동시 설치 중 웹 업데이트는 일반 정식 업데이트 성공과 구분한 미관측 범위다. 녹음·녹화 실제 검증과 A406/A407 보류는 유지한다. 새 백로그 항목은 별도 선택 없이 시작하지 않는다.

이전 세션의 배포·관측·정정 근거는 [공유 근거 색인](history/evidence-index.md)에서 찾는다. 완료·인계 때 이 파일은 최신 상태와 바로 다음 행동으로 고쳐 쓴다.
