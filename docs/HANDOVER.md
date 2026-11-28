# KOTU 현재 상태와 인수인계

## 현재 상태

- **공식 제품·지정 PC**: v0.440.0, 제품 커밋 `5a3b736c0bd804b2098260901c63ff1657c5e568`. [공식 릴리스](https://github.com/zpstudios/kotu/releases/tag/v0.440.0). A444 우클릭 전체 체크 해제를 A443 기준의 minor로 배포·설치했다. 정확한 제품 커밋의 build·release는 전체 native 빌드와 각각 1,595시험을 포함해 성공했다. 공개 latest·8자산과 설치본 대조를 마쳤다.
- **설치·창 배치 판정**: A444는 공식 재시작 알림의 Yes → 다운로드 → Install and restart로 설치했고, 실제·저장 1438×1128·위치 1,1이 정상 종료·다음 재시작까지 유지됐다. A445 시작의 읽기 전용 관측에서는 저장값은 같지만 실제 1438×1090·위치 1,0, 작업 영역 1440×1083이었다. 변화 시점·원인은 미확정이며 임의 원복하지 않았다. 검수 실행 요청이 연 공식 추가 창은 정상 종료했다. 원래 PID21944·Hardware를 유지하며 설정은 최신 확인 시각만 달라지고 기존 오류 로그는 불변이다. 근거는 `artifacts/a445-review/baseline-restoration.json`과 같은 폴더의 원본·창 상태다.
- **현재 근거**: `artifacts/a444-review/final-validation.json`이 로컬 검수·공식 CI·자산·설치·후속 종료/재시작 검증을 연결한다. 최종 문서 커밋 `0853f948ff07a5e6afd8b7ce49cbe77c9a3a25b0`의 build·release도 성공해 A444를 마쳤다. 상세 관측·실패·해시 대조 범위와 이전 A441/A442 근거는 [공유 근거 색인](history/evidence-index.md)에서 회수한다.
- **보호 백업**: 최신은 교체 전 v0.439.1의 `artifacts/a444-review/pre-update-backup/`와 상위 백업·설정 기준·설치 검증이다. 전체 파일 해시를 검증했으며 이전 A442 v0.439.0 이하 보호본도 유지한다. 파일 수·manifest와 보호 범위는 [보호 자료 위치](history/evidence-index.md#protected-material)에 둔다. 기존 실패·설정 차이·복원·정상 종료·재시작 근거를 수정·삭제하지 않는다.
- **작업 환경**: `C:/Users/tsusa/github/kotu`의 master를 유지한다. 사용자 `.codex/`는 작업·검사 입력에서 제외하며 추적하지 않는다. 공식 앱 업데이트·재시작과 별도 검수 앱의 상시 승인은 [공식 절차](development/official-release-and-install.md), 문서 정리는 [완료·인계 절차](development/maintenance-notes.md#document-lifecycle)를 따른다.

## 열린 작업과 다음 시작점

- **선택 작업 상태**: [A445 Details 열](history/requirements-completed-index.md#a445)은 v0.441.0 구현과 독립 소스 검토·통합 publish·Standalone·전체 1,634시험을 통과했으며 제품은 미커밋이다. 정상 Test UI에서 기본/Reset 꺼짐, 창/전체화면의 넓은 S1, 선택·체크·행 간격과 열 변경 시 스크롤, 텍스트 경계·ZIP·7z/RAR 정상 열기/닫기·Refresh를 확인하고 정상 종료·Test 원본 복원을 마쳤다. 다음은 [S4·좁은 화면·대량 목록·변경 감시 앵커 등 남은 검수](DEVICE-CHECKS.md#a445), 제품 커밋·즉시 push, 공식 CI·새 v0.440.0 보호 백업·공식 설치 검증이다. 미커밋 manifest·근거·실행권은 `artifacts/a445-review/checkpoint.json`을 따른다.
- **A442 후속 조건**: [이전 위치·DPI 차이](REQUIREMENTS.md#a442)의 발생 시점·원인은 여전히 미확정이다. A444 업데이트 전 구버전 정상 종료가 이미 관측된 실제 배치로 저장값 네 키를 갱신한 사실과, 이전 A442의 복원·저장·재시작 성공은 [근거 색인](history/evidence-index.md)에 함께 보존한다. 추가 재현 시 조작·배율·실제 창과 저장값·종료 저장 순서를 대조한다. Snap·연속 프레임 등 미관측 조건은 [검수 기록](DEVICE-CHECKS.md#a442)에 유지하며 기존 수리를 최초 차이의 원인 전체로 확대하지 않는다.
- **A439 후속 조건**: 선택된 메뉴·속도 수리의 배포·이 PC 설치는 완료했으므로 다시 착수하지 않는다. [기존 첫 WinUI 종료](REQUIREMENTS.md#a439)는 추가 재현 시 조작·WER/덤프로 조사하며 후속 비재현을 해결로 세지 않는다. 단회 성능 결과를 전체 입력 지연 해결·회귀 배수·GC 제거로 확대하지 않는다. 정상 UI에서 확인한 열 변경 후 End→Enter 경로와 [전체 초점·픽셀 앵커·PDF 썸네일 등 미관측 범위](DEVICE-CHECKS.md#a439)는 구분한다.
- **A438 HWP·PDF 검색**: 구현·공식 배포·설치를 완료했고 현재 버전에도 포함된다. 인덱싱/검색 대기 중 휠·Ctrl+휠, pending/error 팝업·실제 IME·고대비/DPI·암호/초대형/빠른 교체의 미관측 조건은 [DEVICE-CHECKS](DEVICE-CHECKS.md#a438)에 유지한다. 과거 A438/A439 `uncommitted-product-manifest.json`은 당시 해시 근거이며 현재 미커밋 상태를 뜻하지 않는다.
- **선택 밖 범위**: 남은 개발·조사·결정은 [REQUIREMENTS](REQUIREMENTS.md#open-work), 검수 대기는 [DEVICE-CHECKS](DEVICE-CHECKS.md), 확정 사양은 [기능 정본](features/product-contracts.md)에서 관리한다. A418의 테스트판 동시 설치 중 웹 업데이트는 일반 정식 업데이트 성공과 구분한 미관측 범위다. 녹음·녹화 실제 검증과 A406/A407 보류는 유지한다. 새 백로그 항목은 별도 선택 없이 시작하지 않는다.

이전 세션의 배포·관측·정정 근거는 [공유 근거 색인](history/evidence-index.md)에서 찾는다. 완료·인계 때 이 파일은 최신 상태와 바로 다음 행동으로 고쳐 쓴다.
