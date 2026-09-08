# A365 하단 바 개별 위치와 빈 상태 복원 — v0.361.0

2026-09-08 · 사용자가 A364 후속 제안에 "하자"로 승인했고, REQUIREMENTS 최상단에 먼저
등록한 뒤 master에서 구현했다. 압축 배치와 기타 백로그는 변경하지 않았다.

## 현재 변경

- Video 배속 뒤 0폭 열을 제거했다. 자막은 c10, Fit 묶음은 c11이다.
- Fit 묶음은 본체32 + 간격6 + 화살표32 = 70이다. Image·Document·All Readable도 같은
  간격과 오른쪽 끝 기준 위치를 쓴다. 떨어진 두 버튼은 전체 테두리와 모서리 반경4를 쓴다.
- Document 빈 상태의 Print/Fit을 상시 표시·비활성으로 복원했다. 활성 판정은 기존
  UpdatePrintButton/CanPrintNow, ShowPdfFitState/ShowTextFitState가 계속 담당한다.
  텍스트 Zoom·장식의 기존 관련 콘텐츠 표시 조건은 유지한다.
- All Readable 빈 OwnBar는 비활성 Print/Fit만 복원했다. 가짜 Zoom은 없고, 자식이 열리면
  기존 ChildBarHost가 자식 바를 그대로 표시한다.

## 개별 좌표 비교

단위는 XAML 논리 픽셀이다. 배속 오른쪽 끝을 0으로 두면 다음과 같다.

| 바 | 첫 버튼 | 둘째 버튼 | 셋째 버튼 | 묶음 끝 |
|---|---|---|---|---|
| Audio | Devices [6,38] | EQ [44,76] | Visualizer [82,114] | 114 |
| Video 변경 전 | 자막 [12,44] | Fit [50,82] | 화살표 [82,114] | 114 |
| Video 변경 후 | 자막 [6,38] | Fit [44,76] | 화살표 [82,114] | 114 |

각 모듈 바의 오른쪽 끝 R을 기준으로 한 공통 위치:

| 기능 | 시작 | 끝 | 적용 |
|---|---|---|---|
| Zoom | R−198 | R−114 | Image, 텍스트 Document |
| Print | R−108 | R−76 | Image, Document, 빈 All Readable |
| Fit 본체 | R−70 | R−38 | Image, Document, Video, 빈 All Readable |
| Fit 화살표 | R−32 | R | Image, Document, Video, 빈 All Readable |

Video 고정 조작 폭447 + 바깥 열 간격11×6 = 513이다. 기존 바깥 간격6을 Fit 내부로 옮겨
총폭은 변하지 않는다. Audio도 기존513이며 양쪽 축약 임계727을 유지했다. 시간·볼륨을
숨기는 기존 조건과 키보드 대체 조작은 그대로다. Video 열 번호를 쓰는 코드 참조는 없으며
XAML 자막/Fit 배치만 각각10/11로 옮겼다. Image Fit 열은64에서70, Document는 Auto 열의
실제 묶음 폭이70으로 늘어난다. 텍스트 조작이 접히면 그 컨트롤의 여백도 함께 사라진다.

## 검증 상태

- 변경 포함 XAML 6개 XML 파싱 통과. 실제 Video 열/Spacing을 읽어 +6/+44/+82와 끝114를 확인했다.
- 구조 검사 19개 프로젝트 통과. 변경 파일 diff 공백 검사 통과.
- 전체 Release/x64 빌드 경고 0·오류 0, 8개 프로젝트 388/388 통과(실패·건너뜀 0). 배포는 대기다. 로그: artifacts/a365-build.log, artifacts/a365-tests.log; TRX: TestResults/a365-final.
- 실제 WinUI 화면에서 픽셀·배율·포커스·비활성 테마를 확인하지 않았다. 좌표표는 소스 레이아웃
  검산이며 실제 UI 캡처가 아니다. A364의 도형 미리보기도 같은 검증 한계를 유지한다.

---
# A364 배포 이력 — v0.360.0

2026-09-08 · master 직접 작업. 사용자가 직전 감사 결과를 가리켜 "수정해주라"고 승인했다.
구현 전에 REQUIREMENTS에 이 범위를 등록했다. 새 요청 등록과 실제 개발 선택은 구분하며,
등록된 항목 중 사용자가 번호·이름·설명으로 명시적으로 선택한 것만 개발한다.

## 승인 범위

- 문서 빈 화면에서 불필요한 100% 표시와 가이드/마커 조작을 숨긴다.
- Zoom의 열 폭뿐 아니라 실제 버튼 폭을 84로 맞추고, 접힌 조작의 빈 간격을 제거한다.
- All Readable 빈 화면의 동작 없는 가짜 Zoom을 제거한다. 없는 기능을 새로 만들지 않는다.
- 재생/일시정지·음소거의 실제 아이콘을 공통 생성 경로로 맞춘다.
- 감사에서 확인한 모듈별 아이콘 영역과 간격 차이를 정리한다.

기존 우측 기능 위치와 의미, 비활성 상태의 테마 표현, 콘텐츠·키보드 기능 동작을 보존한다.
A363 광고 서버·분석과 다른 미선택 백로그는 구현하지 않는다. 범위를 임의로 넓히지 않는다.

## 구현 결과

- 문서 빈 화면은 New와 파일/드라이브 정보만 표시한다. 텍스트에서는 기존 조작을,
  PDF에서는 페이지·Print·Fit을 표시하며 텍스트 전용 Save/View/Zoom/가이드·마커를 숨긴다.
  열기 실패·PDF 이탈·무제·렌더/편집 전환의 기존 상태 관문에서 표시를 함께 갱신한다.
- Document Zoom은 실제 Width=84다. 자동 열의 ColumnSpacing은 0이며 보이는 요소가
  왼쪽 Margin 6을 소유한다. Collapsed 요소는 열과 간격을 모두 반납한다.
  오른쪽 끝 R 기준 실제 Zoom 시작 R−192, Print R−102, Fit R−64를 유지한다.
  PDF에서도 Print/Fit 위치는 같으며 빈 문서의 조작 열은 모두 0폭이다.
- Archive는 기존 열 번호와 작업 동작을 유지하면서 같은 간격 규칙을 적용한다.
  작업 중 진행/취소 영역은 166+90=256, 유휴 상태에서는 0이다. 왼쪽 버튼 위치는 유지한다.
- All Readable 빈 바의 동작 없는 Zoom·Print·Fit 세 그룹을 제거했다. 열린 자식의 실제 바는
  기존대로 전달한다. 새 확대·인쇄 기능을 만들지 않았다.
- Audio/Video의 초기 상태와 재생·일시정지·종료·음소거 전환은 공통 18×18 벡터 생성기를 쓴다.
  버튼/메뉴의 원본 배율 아이콘도 같은 좌표 원본을 쓴다. 호출마다 새 요소와 도형을 만들고,
  버튼은 상속된 Foreground를 추적하며 메뉴는 PathIcon의 Foreground를 사용한다.
  채우지 않는 경계 도형은 메뉴/버튼의 공통 영역만 정하고 잉크를 추가하지 않는다.
- Document와 Archive 하단 바의 일반 글리프를 18로 맞췄다. 문서 ¶ 표시는 기존 14를 유지하고,
  Archive 목록행 아이콘은 수정하지 않았다. 감사 범위 밖 기능·키보드 동작은 변경하지 않았다.

## 검증

로컬 구현·검증과 GitHub CI·정식 배포를 완료했다. 독립 소스 검토에서 남은 지적은 없다.

- 전체 Release/x64 빌드: 경고 0·오류 0 (`artifacts/a364-build.log`).
- Windows 전체 회귀: 8개 프로젝트 388/388 통과, 실패·건너뜀 0.
  Core 184(새 도형 검사 10건 포함), DocumentModel 22, FileOperations 36, Image 21,
  Audio 22, Video 36, Archive 42, Hardware 25.
  로그는 `artifacts/a364-tests.log`, TRX는 `TestResults/a364-final`이다.
- 구조 검사: 19개 프로젝트 통과. 공유 Toolbar 소스 2개가 App/Audio/Video/Image/Document
  5개 소비 프로젝트에 연결됨을 확인했다. 변경 XAML 5개 파싱·diff·한글 이스케이프 검사 통과.

`artifacts/a364-icon-preview.png`는 실제 생성 좌표를 그린 디자인 검토용 산출물이며
WinUI 화면 캡처가 아니다. 도형 테스트는 경계·높이·중심·좌우 대칭·새 데이터 소유를 확인한다.
실제 WinUI 픽셀 배치·테마·배율·포커스/키보드 조작을 실행하지 않았다면 완료로 주장하지 않는다.
수동 확인은 빈 문서와 파일 열림, All Readable 빈 화면과 자식 바, Audio/Video 재생·음소거,
밝은/어두운 테마의 비활성 아이콘, 서로 다른 창 폭과 배율에서 공통 위치·간격을 대조한다.

## 정식 배포 증거

소스와 v0.360.0 태그는 `5f2874548d47d9df20c3e808f98ea0ed2e8b36f3`으로 일치한다.
[build 34209280968](https://github.com/zpstudios/kotu/actions/runs/34209280968)과
[release 34209280982](https://github.com/zpstudios/kotu/actions/runs/34209280982)가 모두 성공했다.
전체 빌드·테스트, 실행본 준비·시작, 패키징, 실제 설치·설치 파일 정합성·시작과 업로드를 통과했다.
master 일반 빌드의 작업 브랜치 설치 단계는 의도대로 건너뛰며, 정식 설치 검사는 release에서 수행했다.

[정식 v0.360.0](https://github.com/zpstudios/kotu/releases/tag/v0.360.0)은
2026-09-08 18:25:35 KST에 게시됐다(초안·사전 공개 아님).
[Setup EXE](https://github.com/zpstudios/kotu/releases/download/v0.360.0/KOTU-win-Setup.exe)
164,378,596 bytes, Portable 159,829,183 bytes, full 159,886,308 bytes,
delta 693,940 bytes와 `assets.win.json`·`RELEASES`·`releases.win.json`을 확인했다.
공개 업데이트 피드에는 현재 0.360.0 full/delta와 직전 0.359.0 full이 들어 있다.
델타 생성은 확인했으며 기존 설치본의 제자리 업데이트 시험은 수행하지 않았다.
실제 WinUI 픽셀·테마·배율·포커스 검증 한계는 위 기록대로 남는다.
