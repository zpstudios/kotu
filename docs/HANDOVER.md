# KOTU 현재 상태와 인수인계

## 현재 상태

- **공식 제품·지정 PC**: v0.439.1, 제품 커밋 `3169216b2550f323a678829687a32c3884558553`. [공식 릴리스](https://github.com/zpstudios/kotu/releases/tag/v0.439.1). A442 창 배치 수리를 A443 기준의 patch로 배포했다. 해당 커밋의 build `46031241724`·release `46031241737`은 전체 native 빌드와 각각 1,588시험을 포함해 성공했다. 버전 변경 전 동일 제품 소스의 로컬 1,588시험·무계측 UI, 최종 0.439.1 publish·Standalone·독립 검토를 마쳤다.
- **설치·창 배치 판정**: 정식 Settings → Updates에서 v0.439.1을 설치했다. 공식 Full의 1,188파일(도우미 두 파일 경로 매핑 포함)이 설치본과 일치하며 추가 제품 파일은 없다. 구버전 정상 종료가 기존 실제 배치로 저장값 네 키를 덮은 차이를 보존했다. 새 앱 정상 종료 후 기하 5키만 원복했고, 실제 2590×1634·위치 793,278·최대화 false가 활성화·정상 종료 저장·다음 재시작에서도 유지됐다. 최종 43설정키는 최신 확인 시각만 달라지고 기존 시작 로그는 불변이다. 과거 위치·DPI 변화 전체의 최초 원인까지 확정한 것은 아니다.
- **현재 근거**: `artifacts/a442-review/`의 `final-validation.json`·`workflows.json`·`official-release/verification.json`·`official-update-ui.json`·`official-window-state.jsonl`·`installed-release-final.json`. 공개 태그·latest·8자산 메타데이터·본문과 Full/Delta/Setup·세 피드 해시를 대조했다. 구버전 저장 변화·최초 설치/복원 실패와 성공, 후속 저장·재시작은 [공유 근거 색인](history/evidence-index.md)에서 구분해 회수한다. 이전 A441 배포·설치 상세도 같은 색인의 고정 Git 원문과 연결했다.
- **보호 백업**: 교체 전 v0.439.0의 `artifacts/a442-review/pre-update-backup/` 1,189파일과 상위 `backup-coverage.json`·`pre-update-request*.json`·`pre-update-startup-log.json`·`pre-install-v0.439.1*.json`을 보호한다. 잠금 파일로 중단된 부분 백업은 `backup-lock-failure.json`으로 보존한 뒤 기존 파일을 해시 대조해 재사용했다. 이전 v0.438.0 이하 백업, 구버전 저장 차이·복원 실패 후보·복원/저장/재시작 근거는 [보호 자료 위치](history/evidence-index.md#protected-material)를 따르며 수정·삭제하지 않는다.
- **작업 환경**: `C:/Users/tsusa/github/kotu`의 master를 유지한다. 사용자 `.codex/`는 작업·검사 입력에서 제외하며 추적하지 않는다. 공식 앱 업데이트·재시작과 별도 검수 앱의 상시 승인은 [공식 절차](development/official-release-and-install.md), 문서 정리는 [완료·인계 절차](development/maintenance-notes.md#document-lifecycle)를 따른다.

## 열린 작업과 다음 시작점

- **선택 작업 상태**: [A444](history/requirements-completed-index.md#a444)는 후보 16에서 선택한 우클릭 `Uncheck all` 개발이다. 현재 폴더의 숨겨진 체크까지 해제하면서 선택·스크롤 위치를 유지한다. 로컬 1,595시험은 통과했으며 앱 연결 누락의 첫 publish 실패와 독립 검토 지적을 보존했다. 제품·시험·가이드·준비 버전 v0.440.0은 미커밋이고 단계별 판정은 `artifacts/a444-review/checkpoint.json`을 따른다. 다음은 수정본의 정상 UI에서 좌측/중앙·선택 강조·스크롤 보존을 확인하고 Test 설정을 원복하는 것이다. 이후 제품 커밋·즉시 push·공식 배포·이 PC 업데이트와 창 배치 유지까지 마친다. [근거 색인](history/evidence-index.md)에서 변경 전 화면·설정 원본·실패를 회수한다.
- **A442 후속 조건**: [이전 위치·DPI 차이](REQUIREMENTS.md#a442)는 추가 재현 시 조작·배율·실제 창과 저장값·종료 저장 순서를 대조한다. A444 인계 직전 읽기 전용 측정은 같은 공식 PID에서 실제 1438×1128·위치 1,1, 저장값은 기존 2590×1634·위치 793,278 불변을 관측했다. 작업 영역은 1440×1130이며 변화 시점·원인은 미확정이다. `artifacts/a444-review/handoff-official-window-state.json`을 보존하고 업데이트 사전 확인에서 현재 실제/저장값을 다시 대조한다. 이전 배치로 임의 복원하지 않는다. 기존 수리를 최초 차이의 원인 전체로 확대하지 않으며 Snap·연속 프레임 등 미관측 조건은 [검수 기록](DEVICE-CHECKS.md#a442)에 유지한다.
- **A439 후속 조건**: 선택된 메뉴·속도 수리의 배포·이 PC 설치는 완료했으므로 다시 착수하지 않는다. [기존 첫 WinUI 종료](REQUIREMENTS.md#a439)는 추가 재현 시 조작·WER/덤프로 조사하며 후속 비재현을 해결로 세지 않는다. 단회 성능 결과를 전체 입력 지연 해결·회귀 배수·GC 제거로 확대하지 않는다. 정상 UI에서 확인한 열 변경 후 End→Enter 경로와 [전체 초점·픽셀 앵커·PDF 썸네일 등 미관측 범위](DEVICE-CHECKS.md#a439)는 구분한다.
- **A438 HWP·PDF 검색**: 구현·공식 배포·설치를 완료했고 현재 버전에도 포함된다. 인덱싱/검색 대기 중 휠·Ctrl+휠, pending/error 팝업·실제 IME·고대비/DPI·암호/초대형/빠른 교체의 미관측 조건은 [DEVICE-CHECKS](DEVICE-CHECKS.md#a438)에 유지한다. 과거 A438/A439 `uncommitted-product-manifest.json`은 당시 해시 근거이며 현재 미커밋 상태를 뜻하지 않는다.
- **선택 밖 범위**: 남은 개발·조사·결정은 [REQUIREMENTS](REQUIREMENTS.md#open-work), 검수 대기는 [DEVICE-CHECKS](DEVICE-CHECKS.md), 확정 사양은 [기능 정본](features/product-contracts.md)에서 관리한다. A418의 테스트판 동시 설치 중 웹 업데이트는 일반 정식 업데이트 성공과 구분한 미관측 범위다. 녹음·녹화 실제 검증과 A406/A407 보류는 유지한다. 새 백로그 항목은 별도 선택 없이 시작하지 않는다.

이전 세션의 배포·관측·정정 근거는 [공유 근거 색인](history/evidence-index.md)에서 찾는다. 완료·인계 때 이 파일은 최신 상태와 바로 다음 행동으로 고쳐 쓴다.
