# KOTU 현재 상태와 인수인계

## 현재 상태

- **공식 제품·지정 PC**: v0.437.0, 제품 커밋 `f155a2b41a58155fe86487089dd6362f82878d36`. [공식 릴리스](https://github.com/zpstudios/kotu/releases/tag/v0.437.0). A439 메뉴 강조와 Preview 타일 캐시·측정 전 치수 적용 수리를 포함한다. 해당 커밋의 build `45961253647`·release `45961253751` 성공. 로컬 정상 publish·무계측 UI·독립 검토와 공식 전체 native 빌드·1,553시험·시작/설치·Standalone 검증을 마쳤다.
- **설치 대조**: 2026-11-27 정식 Settings → Updates에서 v0.436.0→v0.437.0 다운로드·Install and restart를 완료했다. 설치 1,188파일이 공개 해시를 검증한 공식 Full과 각각 일치하고 추가 제품 파일은 없었다. 설치 도우미 두 파일의 이름/위치 매핑도 대조했다. 재시작 직전 43설정키·값·파일 해시가 그대로이며 보호 백업 대비 차이는 업데이트 확인 시각뿐이다. 설정 파일을 덮어쓰지 않았다. 새 공식 프로세스·H/W Info 화면과 기존 시작 오류 로그 불변을 확인했다.
- **현재 근거**: `artifacts/a439-followup/`의 `workflows.json`·`official-release/verification.json`·`installed-release-verification.json`·`official-update-ui.json`. 공개 태그·latest·8자산 메타데이터·릴리스 본문과 Full/Delta/Setup·세 피드 해시를 대조했다. 로컬 검증은 `final-validation.json`·`versioned-normal-build.json`·`final-normal-ui.json`, 성능·화면 및 업데이트 패키지 대조의 조건은 [A439 정본](features/shell/design-library-A427.md#a439)을 따른다. 이전 배포·복원 근거는 [공유 색인](history/evidence-index.md)에 보존했다.
- **보호 백업**: 교체 전 v0.436.0의 `artifacts/a439-followup/pre-update-backup/` 1,191파일을 설치 직전 다시 대조했다. 백업과 상위 `backup-coverage.json`·`pre-update-request*.json`·`pre-update-startup-log.json`, 새 설치 기준 `pre-install-v0.437.0-ready*.json`을 수정·삭제하지 않는다. v0.435.0의 `artifacts/a439-review/pre-update-backup/` 1,191파일과 상위 기준 JSON도 보호한다. A438 v0.434.0·A437 v0.433.9·A435 v0.433.8·독립 복구 보존본은 [보호 자료 위치](history/evidence-index.md#protected-material)를 따른다.
- **작업 환경**: `C:/Users/tsusa/github/kotu`의 master를 유지한다. 사용자 `.codex/`는 작업·검사 입력에서 제외하며 추적하지 않는다. 공식 앱 업데이트·재시작과 별도 검수 앱의 상시 승인은 [공식 절차](development/official-release-and-install.md), 문서 정리는 [완료·인계 절차](development/maintenance-notes.md#document-lifecycle)를 따른다.

## 열린 작업과 다음 시작점

- **A439 후속 조건**: 선택된 메뉴·속도 수리의 배포·이 PC 설치는 완료했으므로 다시 착수하지 않는다. [기존 첫 WinUI 종료](REQUIREMENTS.md#a439)는 추가 재현 시 조작·WER/덤프로 조사하며 후속 비재현을 해결로 세지 않는다. 단회 성능 결과를 전체 입력 지연 해결·회귀 배수·GC 제거로 확대하지 않는다. 정상 UI에서 확인한 열 변경 후 End→Enter 경로와 [전체 초점·픽셀 앵커·PDF 썸네일 등 미관측 범위](DEVICE-CHECKS.md#a439)는 구분한다.
- **A438 HWP·PDF 검색**: 구현·공식 배포·설치를 완료했고 현재 버전에도 포함된다. 인덱싱/검색 대기 중 휠·Ctrl+휠, pending/error 팝업·실제 IME·고대비/DPI·암호/초대형/빠른 교체의 미관측 조건은 [DEVICE-CHECKS](DEVICE-CHECKS.md#a438)에 유지한다. 과거 A438/A439 `uncommitted-product-manifest.json`은 당시 해시 근거이며 현재 미커밋 상태를 뜻하지 않는다.
- **선택 밖 범위**: 남은 개발·조사·결정은 [REQUIREMENTS](REQUIREMENTS.md#open-work), 검수 대기는 [DEVICE-CHECKS](DEVICE-CHECKS.md), 확정 사양은 [기능 정본](features/product-contracts.md)에서 관리한다. A418의 테스트판 동시 설치 중 웹 업데이트는 일반 정식 업데이트 성공과 구분한 미관측 범위다. 녹음·녹화 실제 검증과 A406/A407 보류는 유지한다. 새 백로그 항목은 별도 선택 없이 시작하지 않는다.

이전 세션의 배포·관측·정정 근거는 [공유 근거 색인](history/evidence-index.md)에서 찾는다. 완료·인계 때 이 파일은 최신 상태와 바로 다음 행동으로 고쳐 쓴다.
