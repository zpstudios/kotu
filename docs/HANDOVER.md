# KOTU 현재 상태와 인수인계

## 현재 상태

- **공식 제품·지정 PC**: v0.433.9, 제품 커밋 `6fb335f5aa5536eff160b9a876865f67e8545662`. [공식 릴리스](https://github.com/zpstudios/kotu/releases/tag/v0.433.9). build `45563367461`·release `45563367509` 성공, 전체 1,495개 시험 통과.
- **설치 대조**: 2026-11-25 정식 Settings → Updates 다운로드·재시작 후 공식 패키지와 설치 1,179파일의 SHA-256·제품 커밋 일치. 업데이트 직전 43설정키·값의 추가/누락/변경 0, 최초 백업 대비 차이는 업데이트 확인 시각뿐이다. 새 공식 프로세스·H/W Info 복원과 기존 시작 오류 로그 불변을 확인했다.
- **현재 근거**: `artifacts/a435-review/official-release/verification.json`, `workflows.json`, `installed-release-verification.json`, `post-update-settings-verification.json`, `official-update-ui.json`. 대표 화면 관측과 남은 조건은 [DEVICE-CHECKS](DEVICE-CHECKS.md#native-media), 요구 결과는 [A435](history/requirements-completed-index.md#a435)를 따른다.
- **보호 백업**: 교체 전 v0.433.8의 `artifacts/a435-review/pre-update-backup/` 1,182파일과 43설정키 기준을 재검증했다. 상위 `artifacts/a435-review/`의 `backup-coverage.json`, `pre-update-request*.json`, `pre-update-revalidation.json`을 함께 보존한다. 이전 백업·독립 복구 보존본·과거 경로 부재 사실은 [보호 자료 위치](history/evidence-index.md#protected-material)를 통해 회수한다. 문서 정리는 이 자료의 수정·삭제 근거가 아니다.
- **작업 환경**: `C:/Users/tsusa/github/kotu`의 master를 유지한다. 사용자 `.codex/`는 작업·검사 입력에서 제외하며 추적하지 않는다. 공식 앱 업데이트·재시작과 별도 검수 앱의 상시 승인은 [공식 절차](development/official-release-and-install.md)에 기록된 범위를 따른다.
- **문서 운영**: [A436](history/requirements-completed-index.md#a436)에서 실제 남은 일·완료 결과·확정 계약·검수 대기를 각각 정본으로 분리하고 고유 정보·근거와 기존 주소를 보존했다. [완료·인계 절차](development/maintenance-notes.md#document-lifecycle)와 역할·상태·중복·링크 검사, 회귀시험을 build/release에 연결했다.

## 열린 작업과 다음 시작점

- **A437 검증 진행**: [오디오 EQ](features/media/audio-equalizer-A437.md) 구현·독립 재검토, 전체 App 빌드·1,531개 시험과 native 합성 음원 검증을 통과했다. KOTU.Test 검수본에서 키보드·Reset·Off/프리셋 왕복·두 창 Custom 보존·재시작·좁은 창 스크롤·시각화 교체를 관측하고 기존 28설정키를 바이트 단위 복원했다. Standalone의 Custom·Tab/PageUp 조절과 영구 설정 비변경도 확인했다. 로컬 근거는 `artifacts/a437-review/`다. v0.434.0 배포 준비 상태이며 건별 커밋/푸시·정확한 제품 HEAD의 공식 배포·이 PC 공식 설치를 마친다. 현재 공식 v0.433.9는 유지 중이다.
- **선택 밖 범위**: 남은 개발·조사·결정은 [REQUIREMENTS](REQUIREMENTS.md#open-work), 검수 대기는 [DEVICE-CHECKS](DEVICE-CHECKS.md), 확정 계약은 [기능 정본](features/product-contracts.md)에서 관리한다. A418의 테스트판 동시 설치 중 웹 업데이트는 일반 정식 업데이트 성공과 구분한 미관측 범위다. 녹음·녹화 실제 검증과 A406/A407 보류는 유지한다.

이전 세션의 배포·관측·정정 근거는 [공유 근거 색인](history/evidence-index.md)에서 찾는다. 완료·인계 때 이 파일은 최신 상태와 바로 다음 행동으로 고쳐 쓴다.
