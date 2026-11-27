# KOTU 현재 상태와 인수인계

## 현재 상태

- **공식 제품·지정 PC**: v0.438.0, 제품 커밋 `e4e3ee1051368c757dababa776abb6e8b1a8e283`. [공식 릴리스](https://github.com/zpstudios/kotu/releases/tag/v0.438.0). A440 Settings Credits와 HWP 안내 정리를 포함한다. 해당 커밋의 build `45981038568`·release `45981038590` 성공. 로컬 정상 publish·무계측 UI·독립 검토와 공식 전체 native 빌드·1,553시험·시작/설치·Standalone 검증을 마쳤다.
- **설치 대조·복원 한계**: 정식 Settings → Updates에서 v0.437.0→v0.438.0 다운로드·Install and restart와 설치본 Credits를 확인했다. 공식 Full의 1,188파일(도우미 두 파일의 경로 매핑 포함)이 설치본과 일치하고 추가 제품 파일은 없었다. 최초 설정 검증은 창 위치·크기 네 키 차이로 실패했다. 정상 종료 후 네 키만 복원하고 최신 `update.lastCheckedAt`을 유지했으며 다른 설정키·기존 시작 로그는 불변이다. 최종 파일·설정 검증은 통과했다. 마지막 재실행 화면은 일부 가림·화면 밖으로 실제 창 기하를 확인하지 못했다. 후속 저장 때 실제 창 값이 다시 반영될 수 있으며 원인 해결을 뜻하지 않는다.
- **현재 근거**: `artifacts/a440-review/`의 `final-validation.json`·`workflows.json`·`official-release/verification.json`·`installed-release-final.json`·`official-update-ui.json`. 공개 태그·latest·8자산 메타데이터·릴리스 본문과 Full/Delta/Setup·세 피드 해시를 대조했다. 최초 실패는 `installed-release-verification.json`, 복원은 `window-settings-restoration.json`에 별도 보존한다. 이전 배포·검수 실행 도구의 복원·등록 CI 재실행 근거는 [공유 색인](history/evidence-index.md)에서 회수한다.
- **보호 백업**: A441 교체 전 현재 v0.438.0의 `artifacts/a441-review/pre-update-backup/` 1,191파일과 상위 `backup-coverage.json`·`pre-update-request*.json`·`pre-update-startup-log.json`을 새로 검증했으며 설치 직전 재대조한다. 기존 v0.437.0의 `artifacts/a440-review/pre-update-backup/`와 교체·복원 기준, v0.436.0의 `artifacts/a439-followup/pre-update-backup/`, v0.435.0의 `artifacts/a439-review/pre-update-backup/` 및 각 기준 JSON을 수정·삭제하지 않는다. 이전 보호본·독립 복구 보존본은 [보호 자료 위치](history/evidence-index.md#protected-material)를 따른다.
- **작업 환경**: `C:/Users/tsusa/github/kotu`의 master를 유지한다. 사용자 `.codex/`는 작업·검사 입력에서 제외하며 추적하지 않는다. 공식 앱 업데이트·재시작과 별도 검수 앱의 상시 승인은 [공식 절차](development/official-release-and-install.md), 문서 정리는 [완료·인계 절차](development/maintenance-notes.md#document-lifecycle)를 따른다.

## 열린 작업과 다음 시작점

- **선택된 A441 업데이트 안내**: [시작·24시간·정확한 버전별 거절 요구](history/requirements-completed-index.md#a441)의 구현은 로컬 검증 완료·공식 배포/설치 대기다. 전체 독립 코드 검토에서 확정 결함이 없었고 비출하 검수본에서 [거절·Yes·창/프로세스 경계·실패·입력 잠금·두 번째 미저장 취소와 복원](DEVICE-CHECKS.md#a441)을 관측했다. 검수 앱 정상 종료·Test 설정 복원을 마쳤다. v0.439.0 버전·4가이드·변경 설명·계약을 준비했고 전체 1,584시험·정상 publish·무계측 UI·Standalone을 통과했다. `artifacts/a441-review/final-validation.json`에서 커밋/즉시 push·공식 배포·이 PC 설치를 이어간다. 상세 근거와 미관측은 검수 정본·`artifacts/a440-a441/checkpoint.json`을 따른다. A440 재착수나 세부 구현 선택의 사용자 확정은 아니다.
- **A439 후속 조건**: 선택된 메뉴·속도 수리의 배포·이 PC 설치는 완료했으므로 다시 착수하지 않는다. [기존 첫 WinUI 종료](REQUIREMENTS.md#a439)는 추가 재현 시 조작·WER/덤프로 조사하며 후속 비재현을 해결로 세지 않는다. 단회 성능 결과를 전체 입력 지연 해결·회귀 배수·GC 제거로 확대하지 않는다. 정상 UI에서 확인한 열 변경 후 End→Enter 경로와 [전체 초점·픽셀 앵커·PDF 썸네일 등 미관측 범위](DEVICE-CHECKS.md#a439)는 구분한다.
- **A438 HWP·PDF 검색**: 구현·공식 배포·설치를 완료했고 현재 버전에도 포함된다. 인덱싱/검색 대기 중 휠·Ctrl+휠, pending/error 팝업·실제 IME·고대비/DPI·암호/초대형/빠른 교체의 미관측 조건은 [DEVICE-CHECKS](DEVICE-CHECKS.md#a438)에 유지한다. 과거 A438/A439 `uncommitted-product-manifest.json`은 당시 해시 근거이며 현재 미커밋 상태를 뜻하지 않는다.
- **선택 밖 범위**: 남은 개발·조사·결정은 [REQUIREMENTS](REQUIREMENTS.md#open-work), 검수 대기는 [DEVICE-CHECKS](DEVICE-CHECKS.md), 확정 사양은 [기능 정본](features/product-contracts.md)에서 관리한다. A418의 테스트판 동시 설치 중 웹 업데이트는 일반 정식 업데이트 성공과 구분한 미관측 범위다. 녹음·녹화 실제 검증과 A406/A407 보류는 유지한다. 새 백로그 항목은 별도 선택 없이 시작하지 않는다.

이전 세션의 배포·관측·정정 근거는 [공유 근거 색인](history/evidence-index.md)에서 찾는다. 완료·인계 때 이 파일은 최신 상태와 바로 다음 행동으로 고쳐 쓴다.
