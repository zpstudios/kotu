# KOTU 현재 상태와 인수인계

## 현재 상태

- **공식 제품·지정 PC**: v0.433.9, 제품 커밋 `6fb335f5aa5536eff160b9a876865f67e8545662`. [공식 릴리스](https://github.com/zpstudios/kotu/releases/tag/v0.433.9). build `45563367461`·release `45563367509` 성공, 전체 1,495개 시험 통과. 이번 문서 작업은 제품 버전과 설치본을 바꾸지 않는다.
- **설치 대조**: 2026-11-25 정식 Settings → Updates 다운로드·재시작 후 공식 패키지와 설치 1,179파일의 SHA-256·제품 커밋 일치. 업데이트 직전 43설정키·값의 추가/누락/변경 0, 최초 백업 대비 차이는 업데이트 확인 시각뿐이다. 새 공식 프로세스·H/W Info 복원과 기존 시작 오류 로그 불변을 확인했다.
- **현재 근거**: `artifacts/a435-review/official-release/verification.json`, `workflows.json`, `installed-release-verification.json`, `post-update-settings-verification.json`, `official-update-ui.json`. 대표 화면 관측과 남은 조건은 [DEVICE-CHECKS](DEVICE-CHECKS.md#native-media), 요구 결과는 [A435](history/requirements-completed-index.md#a435)를 따른다.
- **보호 백업**: 교체 전 v0.433.8의 `artifacts/a435-review/pre-update-backup/` 1,182파일과 43설정키 기준을 재검증했다. 같은 폴더의 `backup-coverage.json`, `pre-update-request*.json`, `pre-update-revalidation.json`을 함께 보존한다. 이전 백업·독립 복구 보존본·과거 경로 부재 사실은 [보호 자료 위치](history/evidence-index.md#protected-material)를 통해 회수한다. 문서 정리는 이 자료의 수정·삭제 근거가 아니다.
- **작업 환경**: `C:/Users/tsusa/github/kotu`의 master를 유지한다. 사용자 `.codex/`는 작업·검사 입력에서 제외하며 추적하지 않는다. 공식 앱 업데이트·재시작과 별도 검수 앱의 상시 승인은 [공식 절차](development/official-release-and-install.md)에 기록된 범위를 따른다.

## 열린 작업과 다음 시작점

- **A436 검토 대기**: 현행 문서를 역할별로 정리하고 완료·인계 절차와 구조 검사, 회귀시험, build/release 문서 검사를 연결했다. [현재 요구](REQUIREMENTS.md#a436) · [문서 정리 절차](development/maintenance-notes.md#document-lifecycle).
- **다음 행동**: 독립 검토에서 열린 범위·사용자 정정·고정 원문 링크·보호 경로를 기준 커밋 `67c4b1c`와 대조한다. 문서 검사·회귀시험·기존 릴리스 검사 결과를 확인한 뒤 A436을 결과 색인으로 정리하고 v0.433.9 문서 커밋·즉시 push 및 원격 CI 확인을 마친다. 제품 재배포·재설치는 필요 없다.
- **선택 밖 범위**: 열린·부분·보류와 후보/B 결정은 [REQUIREMENTS](REQUIREMENTS.md#open-work)에 남아 있다. A418의 테스트판 동시 설치 중 웹 업데이트는 일반 정식 업데이트 성공과 구분한 미관측 범위다. 녹음·녹화 실제 검증과 A406/A407 보류는 유지한다.

이전 세션의 배포·관측·정정 근거는 [공유 근거 색인](history/evidence-index.md)에서 찾는다. 완료·인계 때 이 파일은 최신 상태와 바로 다음 행동으로 고쳐 쓴다.
