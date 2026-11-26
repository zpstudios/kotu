# KOTU 현재 상태와 인수인계

## 현재 상태

- **공식 제품·지정 PC**: v0.435.0, 제품 커밋 `6403859a1df482d3cc4a4852166d959fd1414bee`. [공식 릴리스](https://github.com/zpstudios/kotu/releases/tag/v0.435.0). 해당 HEAD의 build `45809836382`·release `45809836209` 성공. 로컬 전체 publish·1,540시험과 공식 CI의 전체 native 빌드·시험·설치·Standalone 검증을 마쳤다. 공개 태그·latest·8자산 목록·피드와 내려받은 Full/Delta/Setup의 해시를 대조했다.
- **설치 대조**: 2026-11-26 정식 Settings → Updates 다운로드·재시작 후 공식 패키지와 설치 1,188파일의 SHA-256·제품 커밋 일치. 재시작 직전 43설정키·값 대비 추가/누락/변경 0이며 최초 백업 대비 차이는 업데이트 확인 시각뿐이다. 창 설정을 바꾸지 않았고 새 공식 프로세스·H/W Info 복원과 기존 시작 오류 로그 불변을 확인했다.
- **현재 근거**: `artifacts/a438-review/`의 `official-release/verification.json`, `workflows.json`, `installed-release-verification.json`, `official-update-ui.json`. 로컬 검증·28검수 설정 복원은 `validation.json`·`fifth-candidate-ui-restoration.json`, 관측과 남은 조건은 [DEVICE-CHECKS](DEVICE-CHECKS.md#a438), 요구 결과는 [A438](history/requirements-completed-index.md#a438)를 따른다. PdfPig 고지 전문은 CI CRLF/로컬 LF만 정규화해 일치하며 공식 패키지와 설치본 대조는 바이트 해시 기준이다.
- **보호 백업**: 교체 전 v0.434.0의 `artifacts/a438-review/pre-update-backup/` 1,371파일과 43설정키를 보존·재검증했다. 상위 `backup-coverage.json`, `pre-update-request*.json`, `pre-update-final-settings.json`, `pre-update-revalidation.json`도 함께 보존한다. 이전 A437 v0.433.9 1,182파일·A435 v0.433.8 백업·독립 복구 보존본·과거 경로 부재 사실은 [보호 자료 위치](history/evidence-index.md#protected-material)에서 회수한다. 문서 정리는 수정·삭제 근거가 아니다.
- **작업 환경**: `C:/Users/tsusa/github/kotu`의 master를 유지한다. 사용자 `.codex/`는 작업·검사 입력에서 제외하며 추적하지 않는다. 공식 앱 업데이트·재시작과 별도 검수 앱의 상시 승인은 [공식 절차](development/official-release-and-install.md)에 기록된 범위를 따른다.
- **문서 운영**: [A436](history/requirements-completed-index.md#a436)에서 실제 남은 일·완료 결과·확정 계약·검수 대기를 각각 정본으로 분리하고 고유 정보·근거와 기존 주소를 보존했다. [완료·인계 절차](development/maintenance-notes.md#document-lifecycle)와 역할·상태·중복·링크 검사, 회귀시험을 build/release에 연결했다.

## 열린 작업과 다음 시작점

- **A438 HWP·PDF 검색**: [검색 계약](features/editor/paged-document-search-A438.md)의 구현·독립 검토·공식 배포·설치를 마쳤다. 관측한 문서·초점·검색·치환 범위와 대기 중 휠·실제 IME 등 미관측 조건은 [DEVICE-CHECKS](DEVICE-CHECKS.md#a438)에 유지한다. 커밋 전 제품 31파일 기준 `artifacts/a438-review/uncommitted-product-manifest.json`은 현재 제품과 해시가 일치하는 당시 기준 자료이며 미커밋 상태를 뜻하지 않는다.
- **A439 디자인 철회·복원**: 다중 스킨 제공 철회·구현 보존·사용 중단, 통일성을 유지한 원래 테마와 누락 동작 복원, UI 지연 수리가 선택됐다. [정적 조사·변경 계약](features/shell/design-library-A427.md#a439)에 중복 Refresh·도형 재생성 경로와 Original 다크 호버 색 충돌을 기록했다. 실제 성능 기여도는 미측정이며 제품 구현은 아직이다. 다음은 동일 조건 측정과 단일 활성 스타일 구현·상태/성능 검증이다. 현재 Original 강제나 과거 통째 회귀로 대체하지 않는다. A438과 제품 변경·배포 단위를 구분한다.
- **선택 밖 범위**: 남은 개발·조사·결정은 [REQUIREMENTS](REQUIREMENTS.md#open-work), 검수 대기는 [DEVICE-CHECKS](DEVICE-CHECKS.md), 확정 계약은 [기능 정본](features/product-contracts.md)에서 관리한다. A418의 테스트판 동시 설치 중 웹 업데이트는 일반 정식 업데이트 성공과 구분한 미관측 범위다. 녹음·녹화 실제 검증과 A406/A407 보류는 유지한다.

이전 세션의 배포·관측·정정 근거는 [공유 근거 색인](history/evidence-index.md)에서 찾는다. 완료·인계 때 이 파일은 최신 상태와 바로 다음 행동으로 고쳐 쓴다.
