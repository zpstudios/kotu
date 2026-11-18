# KOTU 현재 상태와 인수인계

## 현재 상태

- 제품 v0.427.0의 공식 배포와 지정 PC 설치는 2026-11-16 완료됐다. 해당 PC에서 설치 파일 대조와 설정 보존을 확인했다. [공식 릴리스](https://github.com/zpstudios/kotu/releases/tag/v0.427.0) · [제품 커밋](https://github.com/zpstudios/kotu/commit/72a25d757696450d894a14bdd639c365f14bfcd6) · [A408 검수 범위](features/shell/original-icon-A408.md).
- 공식 설치 백업은 해당 PC의 `%USERPROFILE%/KOTU-Validation/20261116-official-v0.427.0/`, 설치 근거는 당시 작업 폴더의 `artifacts/a408-release-427/`에 기록했다. 다른 체크아웃에서는 경로 존재를 먼저 확인한다.
- 11월 17일 복구 문서 4개의 대조와 중복 원문 폐기를 마쳤다. 독립 복구 보존본은 보호 대상이다. [나중에 반영한 요구와 폐기 근거](history/recovered-notes-review-A417.md).
- 문서 정리로 지침과 현재 상태를 줄이고, 과거 기록과 기능 자료를 목적별로 찾을 수 있게 했다. 문서 수정 후 `pwsh -File eng/Test-Documentation.ps1`로 확인한다. [문서 지도](README.md)

## 열린 작업과 다음 시작점

| 항목 | 다음 작업을 선택할 때 확인할 것 |
|---|---|
| 테스트 Setup 분리 A418 | 기존 정식 v0.427.0에서 다음 웹 업데이트 수신을 직접 확인할 수 있도록 정식판 설치·적용은 검수 시점까지 보존한다. [패키지와 검수 절차](development/official-release-and-install.md) |
| 문서 편집 A401 | 원격 검수에서 저장·선택·편집·인쇄 취소 등을 확인했다. 직접 접속 시 실제 한국어 IME와 연속 입력·스크롤 체감, 다른 테마·배율 및 극단 조건이 남았다. [편집기 검수](features/editor/local-text-editor-A401.md) |
| 이미지 A398 | v0.426.0에서 PNG/TIFF 썸네일과 APNG 일부 경로를 검수했다. 사용자 원본·다른 코덱·클라우드 캐시 조건 등은 부분 반영으로 남았다. [이미지 방향](features/image/exif-orientation-A398.md) |
| 녹음·녹화 | 일반 녹화 저장과 검은 프레임 수정은 v0.426.0에 포함됐다. 원격 환경에서 미뤘던 실기기 검수 및 앱 내 재생의 검증 한계를 구분한다. 빠른 녹음·트레이 A406/A407은 보류다. [녹화 검수](features/recording/recording-save-and-editor-fullscreen.md) · [보류 범위](features/recording/quick-recording-A406-A407.md) |
| 나머지 요구·결정 | [요구사항](REQUIREMENTS.md)에서 선택 항목과 관련 결정만 확인한다. [실기기 확인 목록](DEVICE-CHECKS.md)은 해당 동작을 검수할 때 조회한다. |

새 작업은 사용자가 선택한 범위에서 시작한다. `master`의 현재 경로를 확인하고, 이전 기록에 등장하는 OneDrive·Temp 사본이나 당시 검수 창을 작업 정본으로 가정하지 않는다. 예전 판단·승인·원본 관측은 [당시 기록의 절별 색인](history/handover-index-through-2026-11-17.md)에서 찾는다.
