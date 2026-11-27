# KOTU 현재 상태와 인수인계

## 현재 상태

- **공식 제품·지정 PC**: v0.439.0, 제품 커밋 `cf685bfae2c2f7727735e4508c92f13a766e32a8`. [공식 릴리스](https://github.com/zpstudios/kotu/releases/tag/v0.439.0). A441 시작·24시간 업데이트 확인과 정확한 버전별 거절 저장을 포함한다. 해당 커밋의 build `46001773756`·release `46001773719` 성공. 로컬·공식 각각 1,584시험과 정상 publish·Standalone·독립 검토·무계측 UI, 공식 전체 native 빌드·시작/설치 검증을 마쳤다.
- **설치 대조·복원 한계**: 정식 Settings → Updates에서 v0.438.0→v0.439.0 다운로드·Install and restart를 수행했다. 공식 Full의 1,188파일(도우미 두 파일 경로 매핑 포함)이 설치본과 일치하고 추가 제품 파일은 없다. 설치본의 현재/최신 버전과 시작 확인·약 24시간 카운트다운을 관측했다. 최초 설정 검증은 창 너비·위치 세 키 차이로 실패했다. 정상 종료 후 세 키만 복원했다. 전체 43개 키 중 최신 확인 시각만 달라지고 기존 시작 로그는 불변이다. 재실행 후 최종 파일·설정 검증은 통과했다. 저장된 값의 복원이며 실제 창 기하의 영구 수리나 후속 저장 불변을 뜻하지 않는다.
- **현재 근거**: `artifacts/a441-review/`의 `final-validation.json`·`workflows.json`·`official-release/verification.json`·`official-update-ui.json`·`installed-release-final.json`. 공개 태그·latest·8자산 메타데이터·본문과 Full/Delta/Setup·세 피드 해시를 대조했다. 앱 준비 ZIP과 공개 Full의 표현 차이·1,193항목 바이트 일치는 `app-update-package-comparison.json`이다. 최초 실패 `installed-release-verification.json`과 복원 `window-settings-restoration.json`을 별도로 보호한다. 단계별 관측·이전 A440의 창 설정/배율 한계는 [공유 색인](history/evidence-index.md)에서 회수한다.
- **보호 백업**: 교체 전 v0.438.0의 `artifacts/a441-review/pre-update-backup/` 1,191파일과 상위 `backup-coverage.json`·`pre-update-request*.json`·`pre-update-startup-log.json`, `pre-install-v0.439.0*.json`·설치 검증/복원 근거를 보호한다. 기존 v0.437.0의 `artifacts/a440-review/pre-update-backup/`, v0.436.0의 `artifacts/a439-followup/pre-update-backup/`, v0.435.0의 `artifacts/a439-review/pre-update-backup/` 및 각 기준 JSON도 수정·삭제하지 않는다. 이전 보호본·독립 복구 보존본은 [보호 자료 위치](history/evidence-index.md#protected-material)를 따른다.
- **작업 환경**: `C:/Users/tsusa/github/kotu`의 master를 유지한다. 사용자 `.codex/`는 작업·검사 입력에서 제외하며 추적하지 않는다. 공식 앱 업데이트·재시작과 별도 검수 앱의 상시 승인은 [공식 절차](development/official-release-and-install.md), 문서 정리는 [완료·인계 절차](development/maintenance-notes.md#document-lifecycle)를 따른다.

## 열린 작업과 다음 시작점

- **선택된 A442 창 배치 수리**: 미커밋 후보 10파일의 r9 native 회귀·정상 1,588시험·publish·Standalone·무계측 UI와 정상 종료 저장을 통과했다. 검수 종료·Test 원복·공식 설정/로그 불변을 확인했다. A443 정책상 기존 동작 수리인 patch로 분류해 소스 버전·변경 설명을 0.439.1로 준비했다. 위 검증은 버전 변경 전 0.439.0 후보 기준이다. 최종 버전 검증·독립 검토→커밋·즉시 push→공식 전체 native CI·배포→새 v0.439.0 백업 후 이 PC 설치를 이어간다. 공식 창의 실제 기하와 저장값 차이가 있어 구버전 종료 저장의 영향을 먼저 보존·대조하고, 새 앱의 실제 배치·후속 저장·재시작까지 확인한다. [남은 조사](REQUIREMENTS.md#a442)·[Snap 등 검수 경계](DEVICE-CHECKS.md#a442), 해시·재개점은 `artifacts/a442-review/checkpoint.json`·[근거](history/evidence-index.md)를 따른다. 현재 공식 제품은 위 v0.439.0이다.
- **A439 후속 조건**: 선택된 메뉴·속도 수리의 배포·이 PC 설치는 완료했으므로 다시 착수하지 않는다. [기존 첫 WinUI 종료](REQUIREMENTS.md#a439)는 추가 재현 시 조작·WER/덤프로 조사하며 후속 비재현을 해결로 세지 않는다. 단회 성능 결과를 전체 입력 지연 해결·회귀 배수·GC 제거로 확대하지 않는다. 정상 UI에서 확인한 열 변경 후 End→Enter 경로와 [전체 초점·픽셀 앵커·PDF 썸네일 등 미관측 범위](DEVICE-CHECKS.md#a439)는 구분한다.
- **A438 HWP·PDF 검색**: 구현·공식 배포·설치를 완료했고 현재 버전에도 포함된다. 인덱싱/검색 대기 중 휠·Ctrl+휠, pending/error 팝업·실제 IME·고대비/DPI·암호/초대형/빠른 교체의 미관측 조건은 [DEVICE-CHECKS](DEVICE-CHECKS.md#a438)에 유지한다. 과거 A438/A439 `uncommitted-product-manifest.json`은 당시 해시 근거이며 현재 미커밋 상태를 뜻하지 않는다.
- **선택 밖 범위**: 남은 개발·조사·결정은 [REQUIREMENTS](REQUIREMENTS.md#open-work), 검수 대기는 [DEVICE-CHECKS](DEVICE-CHECKS.md), 확정 사양은 [기능 정본](features/product-contracts.md)에서 관리한다. A418의 테스트판 동시 설치 중 웹 업데이트는 일반 정식 업데이트 성공과 구분한 미관측 범위다. 녹음·녹화 실제 검증과 A406/A407 보류는 유지한다. 새 백로그 항목은 별도 선택 없이 시작하지 않는다.

이전 세션의 배포·관측·정정 근거는 [공유 근거 색인](history/evidence-index.md)에서 찾는다. 완료·인계 때 이 파일은 최신 상태와 바로 다음 행동으로 고쳐 쓴다.
