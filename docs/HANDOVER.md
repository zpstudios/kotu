# KOTU 현재 상태와 인수인계

## 현재 상태

- **공식 제품·지정 PC**: v0.436.0, 제품 커밋 `ab6e992c043ddc8f015b7b1fe1e85414c81e03cd`. [공식 릴리스](https://github.com/zpstudios/kotu/releases/tag/v0.436.0). 해당 커밋의 build `45837085360`·release `45837085341` 성공. A439의 스킨 구현·저장 선택을 보존하고 활성 스킨 처리·갤러리를 끄며 네이티브 테마·상태를 복원했다. 로컬 계측 없는 publish·1,553시험·독립 검토와 공식 CI의 전체 native 빌드·시험·설치·Standalone 검증을 마쳤다.
- **설치 대조**: 2026-11-26 정식 Settings → Updates 다운로드·재시작 후 공식 Full과 설치 1,188파일의 SHA-256·제품 커밋 일치. 최초 재시작에서 달라진 `window.maximized`는 최대화·정상 종료·공식본 재실행으로 복원했다. 최종 43설정키·값은 재시작 직전 기준과 바이트 일치하며, 최초 백업 대비 차이는 업데이트 확인 시각뿐이다. 설정 파일을 덮어쓰지 않았다. 새 공식 프로세스·H/W Info 실행과 기존 시작 오류 로그 불변 확인.
- **현재 근거**: `artifacts/a439-review/`의 `official-release/verification.json`·`workflows.json`·`installed-release-verification.json`·`official-update-ui.json`. 공개 태그·latest·8자산·릴리스 본문·Full/Delta/Setup·업데이트 피드 해시 대조 완료. 로컬은 `validation.json`·`product-build-verification.json`·`test-results/`·`ui-restoration.json`, 성능·화면 조건은 [A439 정본](features/shell/design-library-A427.md#a439)에서 회수한다. 이전 A438 v0.435.0 배포·설치 근거는 [공유 색인](history/evidence-index.md)에 보존했다.
- **보호 백업**: 교체 전 v0.435.0의 `artifacts/a439-review/pre-update-backup/` 1,191파일과 상위 `backup-coverage.json`·`pre-update-request*.json`·`pre-update-final-settings.json`·`pre-update-revalidation.json` 보존. 이전 A438 v0.434.0 1,371파일, A437 v0.433.9 1,182파일·A435 v0.433.8·독립 복구 보존본은 [보호 자료 위치](history/evidence-index.md#protected-material)를 따른다. 문서 정리는 수정·삭제 근거가 아니다.
- **작업 환경**: `C:/Users/tsusa/github/kotu`의 master를 유지한다. 사용자 `.codex/`는 작업·검사 입력에서 제외하며 추적하지 않는다. 공식 앱 업데이트·재시작과 별도 검수 앱의 상시 승인은 [공식 절차](development/official-release-and-install.md), 문서 정리는 [완료·인계 절차](development/maintenance-notes.md#document-lifecycle)를 따른다.

## 열린 작업과 다음 시작점

- **선택된 A439 후속 수리**: 메뉴 정상 빌드·라이트/전체 앱 다크의 강조·초점·스타일 검증을 유지한다. Preview 캐시 1→4→1 반전에서 실제 항목 90→216→90과 큐 대기 약 0.52→2.50→0.56초를 확인했고 WinUI UIA 반복 탐색 경로와 연결했다. 12열에서 남은 대기를 줄인 최종 후보는 `CacheLength=0.25`다. 메뉴와 `ThumbnailExplorer.xaml` 두 파일이 미커밋이며 버전은 0.436.0이다. [정본](features/shell/design-library-A427.md#a439)과 `artifacts/a439-followup/` 근거를 읽고 0.25 소스 새 빌드·최초 생성·큰 창/빠른 스크롤 검수 → 정상 전체 검증 → 별도 버전 공식 배포·이 PC 설치를 이어간다.
- **A439 조사·검수 경계**: 캐시 0.25는 단계 적용 후 좁은 12열에서 108개 캐시·최대 큐 대기 약 0.35초와 마지막 TXT 선택·열기를 확인했으나 최종 소스 빌드는 아직이다. 고정 출력 `response-product-cache-r1`은 캐시 1 제품 후보와 v3 계측본이다. 외부 UIA 클라이언트·전체 입력 지연, [기존 첫 WinUI 종료](REQUIREMENTS.md#a439), [OS 테마·고대비/DPI 등](DEVICE-CHECKS.md#a439)은 구분한다. 최신 `ui-restoration.json`에서 검수 정상 종료·KOTU 프로세스 없음·Test 28키 바이트 복원·공식 43키와 OS 불변을 확인했다. 최종 정상 빌드에서 네 종류 A439 probe·강제 테마·하네스를 제외하며 다음 설치 전 별도 v0.436.0 백업을 만든다.
- **A438 HWP·PDF 검색**: 구현·공식 배포·설치를 완료했고 현재 버전에도 포함된다. 인덱싱/검색 대기 중 휠·Ctrl+휠, pending/error 팝업·실제 IME·고대비/DPI·암호/초대형/빠른 교체의 미관측 조건은 [DEVICE-CHECKS](DEVICE-CHECKS.md#a438)에 유지한다. 과거 A438/A439 `uncommitted-product-manifest.json`은 당시 해시 근거이며 현재 미커밋 상태를 뜻하지 않는다.
- **선택 밖 범위**: 남은 개발·조사·결정은 [REQUIREMENTS](REQUIREMENTS.md#open-work), 검수 대기는 [DEVICE-CHECKS](DEVICE-CHECKS.md), 확정 사양은 [기능 정본](features/product-contracts.md)에서 관리한다. A418의 테스트판 동시 설치 중 웹 업데이트는 일반 정식 업데이트 성공과 구분한 미관측 범위다. 녹음·녹화 실제 검증과 A406/A407 보류는 유지한다. 새 백로그 항목은 별도 선택 없이 시작하지 않는다.

이전 세션의 배포·관측·정정 근거는 [공유 근거 색인](history/evidence-index.md)에서 찾는다. 완료·인계 때 이 파일은 최신 상태와 바로 다음 행동으로 고쳐 쓴다.
