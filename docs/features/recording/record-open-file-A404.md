# A404 — Record의 Open file 노출 확인

2026-10-24. **현재 v0.406.0 동작 검증으로 종결, 제품 변경·새 버전 없음.**
메인 조율/독립 검수와 GPT-6 Astra/high의 읽기·정상 앱 화면 조사를 직렬 진행했다.
원래 제보 당시의 설치본·진입 경로는 미확인이며 이번 결과로 소급해 단정하지 않는다.

## 확인 결과

- 정상 v0.406.0에서 H/W→Record, 파일이 열린 Document→Record, Settings→Record는
  셸 `Open file` 버튼과 빈자리가 사라진다. Record→빈 Document에서는 파일 열기가 다시 나타난다.
- Record에서 Ctrl+O를 눌러도 Ready 상태를 유지했고 파일 선택창/탐색 화면이 열리지 않았다.
- 별도 `Open result folder`는 폴더 아이콘과 명시적 접근성 이름을 유지한다. 저장 결과가 없는
  시험 상태에서는 비활성이다. 툴팁은 `Open result folder (last saved or partial recording)`이다.
  실제 결과 폴더를 여는 동작은 시험하지 않았다.
- 메인은 결정적인 문서→Record, Record→Document, Settings→Record 화면과 소스·접근성 자료를 대조했다.
  캡처는1280×752 JPEG이므로 정확한 물리 픽셀/DIP 측정 근거로 사용하지 않았다.

## 소스와 한계

`RecordModule.SupportedExtensions`가 비어 있어 `MainWindow.UpdateOpenFileButton`이
Visible/Collapsed와 모듈 바 왼쪽 여백82/44 DIP를 함께 결정한다. 이는 소스 수치이며
별도로 측정한 네이티브 경계값은 아니다. `SetContentState`가 전환마다 이전 파일/탐색 상태를 지운다.
`EnterOpenFileBrowsing`의 추가 방어는 확장자 검사가 아니라 현재 파일/모듈의 null 검사다.
Record는 파일 열기 계약을 구현하지 않고 별도 결과 폴더 기능은 저장/부분 결과의 부모 위치를 연다.

이전 콘텐츠 세션은 교체 전에 분리되며 큐에 들어간 통지도 실행 시 현재 뷰/해제 여부를 검사한다.
지연 완료 경합은 소스 감사이며 강제 주입 시험을 하지는 않았다. 전역 파일 활성화/drop은 적절한
파일 모듈로 라우팅되는 별도 기능이고 drop의 실제 동작은 이번 범위에서 검증하지 않았다.
정상 시작은 H/W를 선택하므로 문자 그대로 빈 셸→Record는 초기 Collapsed/전환 경로의 소스만 확인했다.
이전 설치본·다른 배율·저장 결과 폴더 실행·녹화 중 상태는 미검증이다.

## 실행본과 복원

실행본은 검수한 정상 `artifacts/a408-implementation/agent/production/KOTU.exe`, 버전0.406.0.0이다.
DLL SHA256은 `41C711BE075EE320CCD0D7DBA4688BD3698786C57F2B570CE2DA034FB35FB39E`로 전후 동일하다.
시험 전에 새 설정 백업/프로세스 목록을 확보하고 합성 텍스트1개만 열었다. 실제 녹음/촬영·사용자 파일·
OS 테마/배율/보안 설정은 바꾸지 않았다.00:58:19 설정을 바이트 일치 복원했고 최종 창/프로세스0,
자료·제품 도형·실행본 해시가 유지됐다. Record의 기존 자동 쓰기 검사 때문에 원래 존재하던
Music/Video 기본 폴더의 디렉터리 시각은 달라질 수 있으며 시각 보존을 주장하지 않는다.

상세 증거: `artifacts/a404/agent/report.md`, `preservation.json`, 경로별 JPEG/접근성 자료.
메인 검수: `artifacts/a404/main/review.md`. 제품 변경이 없어 별도 빌드·시험·버전 증가는 하지 않았다.
