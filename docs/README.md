# KOTU 문서 지도

| 찾는 내용 | 시작할 곳 |
|---|---|
| 지금 배포된 버전과 다음 작업 | [현재 상태와 인수인계](HANDOVER.md) |
| 남은 개발·조사·결정·보류 | [요구사항](REQUIREMENTS.md) |
| 구현 결과·후보의 영구 주소 | [결과 색인](history/requirements-completed-index.md) |
| 현재 확정 사양·결정 | [제품 계약](features/product-contracts.md)에서 주제별 기능 정본으로 이동 |
| 실기기에서 확인할 동작 | [검수 목록](DEVICE-CHECKS.md) |
| 사용법과 단축키 | [한국어 설명서](guides/user-guide.ko.md) · [English guide](guides/user-guide.en.md). 가이드 상단의 기준 버전을 확인한다. |
| 개발·빌드·배포 | [내부 빌드](development/build.md) · [공식 설치](development/official-release-and-install.md) · [버전 증가·릴리스 본문](development/release-notes-workflow.md) · [구현 시 참고](development/maintenance-notes.md) |
| 기능별 설계·조사 | [탐색기](features/explorer/) · [문서 편집](features/editor/) · [이미지](features/image/) · [오디오·비디오](features/media/) · [녹화](features/recording/) · [압축](features/compression/) · [창·공통 UI](features/shell/) |
| 완료·인계 때 문서 정리 | [공용 절차와 검사 계약](development/maintenance-notes.md#document-lifecycle) |
| 검수 결과·진단 근거 | [검수 자료](verification/) · [썸네일 충돌 로그 채취 시점](verification/thumbnail-crash-trace-A352.md) |
| 과거 작업의 결정과 당시 한계 | [공유 근거 색인](history/evidence-index.md) · [과거 자료](history/) · [과거 인수인계 절별 색인](history/handover-index-through-2026-11-17.md) |
| 버전별 변경 설명 | 루트의 `RELEASE-v버전.md`는 배포 도구의 입력이다. [v0.427.0 변경 설명](RELEASE-v0.427.0.md) |

소스·CI에서 경로를 참조하는 일부 설계 문서는 기존 위치에 있다. [재생목록](A11-playlist-design.md), [패널 키 검수](A135-audit.md), [탐색기 동작표](A94-matrix.md), [녹화 엔진 빌드와 동작](RECORDING.md)이 해당한다. `gen_test_audio.py`와 `gen_test_clip.py`는 제품 샘플 미디어 생성기다. `assets/kotu-brand-sheet.png`는 브랜드 이미지 생성에 쓰는 원본이다.

새 문서는 역할과 기능으로 분류하고 파일명은 기능을 앞에, 관련 A번호를 뒤에 쓴다. 작업마다 필요한 항목과 링크만 연다.
