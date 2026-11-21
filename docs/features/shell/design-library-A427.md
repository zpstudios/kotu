# A427 전체 UI 디자인과 선택 화면

## 승인 범위

2026-11-21 사용자는 좌우 패널·중앙 영역·하단 작업표시줄 등 전체 UI를 통일하는 11개 후보를 모두 제품에 반영하도록 승인했다. 색만 다른 유사안은 별도 디자인으로 늘리지 않는다. 원본 비교 시안은 로컬 `artifacts/ui-design-candidates/kotu-design-directions.html`, 사용자 첨부 화면은 같은 폴더의 `reference.png`에 있다. 시안의 마지막 CSS 재정의를 포함해 비교한다.

승인한 방향은 정밀 선형, 둥근 휴머니스트, 솔리드 픽토그램, 편집물 타이포그래피, 터미널 계기판, 촉각적 기계, 굵은 프레임, 접힌 종이, 입체 타일, 픽셀 격자, 손그림 잉크다. 각각 선끝·면·글자의 역할·체크 표시·컨테이너 형태까지 구별한다. 색·굵기만 바꾼 11개 항목으로 완료 처리하지 않는다.

| 저장 ID | 영어 표시 이름 | 구별할 조형 |
|---|---|---|
| `precision` | Precision line | 가는 선과 절제된 평면 경계 |
| `humanist` | Rounded humanist | 둥근 선끝·패널·버튼 |
| `solid` | Solid pictograms | 채운 아이콘과 반전된 작업 막대 |
| `editorial` | Editorial typography | 명조 제목·파일명, 가는 가로 구분선 |
| `terminal` | Terminal console | 고정폭 글자, 문자 기호, 대괄호 체크 |
| `mechanical` | Mechanical controls | 눌린 면과 양각 조작부 |
| `frame` | Bold frames | 굵은 사각 경계와 어긋난 단단한 그림자 |
| `paper` | Folded paper | 접힌 모서리와 종이 층 |
| `isometric` | Dimensional tiles | 앞·위·옆면을 구별한 타일 |
| `pixel` | Pixel grid | 계단형 선과 픽셀 글자 |
| `ink` | Hand-drawn ink | 손글씨와 통제된 불규칙 윤곽 |

## 오프라인 글꼴

시스템 글꼴은 이름으로 참조하고 복사하지 않는다. Nunito Sans와 Barlow Semi Condensed의 한글은 Malgun Gothic으로 보완한다. 편집물의 한글 제목에는 선택 설치인 Batang 대신 Nanum Myeongjo를 동봉한다. 터미널·픽셀·손그림은 한영을 같은 가족으로 표시한다.

동봉 대상은 Nunito Sans Regular/SemiBold, Barlow Semi Condensed Regular/SemiBold, Nanum Myeongjo Regular/Bold, Nanum Gothic Coding Regular/Bold, Galmuri11 Regular/Bold, Gaegu Regular/Bold다. 원본을 수정하지 않고 각 가족의 SIL OFL 1.1 저작권·라이선스 전문과 함께 배포한다. 다운로드 후 TTF 내부 family 이름·문자 지원·크기·해시를 기계적으로 확인한다.

원본 검증에서 두 Nanum 가족과 Galmuri11은 각 두께 모두 한글 음절 11,172자와 인쇄 가능한 ASCII 95자를 포함했다. Gaegu는 한글 2,350자만 포함하고 백틱이 없어 드문 한글·누락 기호에 시스템 보완 글꼴이 필요하다. WinUI 파일 참조의 가족 이름은 `NanumMyeongjo`, `NanumGothicCoding`처럼 공백이 없다. 검증 원본·라이선스·출처와 해시는 로컬 `artifacts/a427-review/fonts/manifest.json`에 있다. 실제 네이티브 렌더링 확인은 별도 단계다.

- [Nunito Sans 원본](https://github.com/googlefonts/NunitoSans/tree/058bd7a2f33d6ad5ef1df985b3db403622016a8c/fonts/ttf)과 같은 커밋의 `OFL.txt`.
- [Google Fonts 고정 원본](https://github.com/google/fonts/tree/06bfe0d62a98375cb09c942fa18e68bd32cff057/ofl)의 `barlowsemicondensed`, `nanummyeongjo`, `nanumgothiccoding`, `gaegu` 폴더와 각 `OFL.txt`.
- [Galmuri 고정 원본](https://github.com/quiple/galmuri/tree/bdb86ae89466a361eb8df861222736b81ff975ef/dist)의 `Galmuri11.ttf`, `Galmuri11-Bold.ttf`, `LICENSE.txt`.

## 설정에서 선택

- 같은 설정 섹션에 **Single view**와 **Gallery view**를 제공한다. 두 보기는 같은 미리보기 선택을 공유한다.
- Single view는 전체 화면 예시 하나와 이름·이전/다음·현재 위치를 표시한다. 좌우로 넘겨 모든 디자인을 볼 수 있다.
- Gallery view는 여러 전체 화면 썸네일을 적응형 열로 나란히 보여 준다. 높이가 부족하면 스크롤하고 카드를 선택하면 같은 디자인의 큰 미리보기로 연결한다.
- 탐색은 미리보기 선택만 바꾼다. **Apply design**이 실제 앱과 저장값을 바꾸며 현재 적용 디자인은 별도로 표시한다.
- 미리보기는 같은 파일·한영 이름·체크·현재 선택·비활성 상태를 사용하는 작은 예시다. 실제 사용자 파일이나 모듈을 열지 않는다. 실제 UI와 동일한 디자인 정의를 사용한다.
- 새 기본값은 Precision line이다. 안정적인 문자열 ID를 `display.design`에 저장하고 알 수 없는 ID는 기본값으로 처리한다. 기존 `display.thinControls` 키를 삭제하거나 덮어쓰지 않는다.

## 적용과 보존

- 열린 창과 나중에 생성되는 창·메뉴·가상화된 목록 요소에 같은 선택을 적용한다. Standalone도 현재 세션 안에서 선택할 수 있다.
- 적용 때문에 창이나 모듈을 다시 만들거나 파일을 다시 열지 않는다. 미저장 문서·재생·체크/선택·스크롤·패널 상태를 보존한다.
- 글꼴·아이콘과 버튼·체크박스·기호·입력·정보 표·패널 경계·하단 면을 함께 다룬다. 문서 원문·코드·자막 등 콘텐츠 자체의 글꼴은 기존 역할을 유지한다.
- 명암 테마는 디자인과 별개이며 시스템 Light/Dark를 따른다. 고대비에서는 시스템 상태색과 포커스 식별성을 우선한다. 외부 글꼴은 라이선스와 함께 동봉하고 실행 중 CDN을 사용하지 않는다.
- 하단 높이 44, 기본 버튼 32×32, 텍스트 조작부 84×32와 A426의 공통 버튼 위치를 유지한다. A112 Back/Esc와 F11/F12의 패널 동작도 유지한다.
- 체크한 작업 대상·현재 선택·열린 콘텐츠 표시는 서로 다른 의미로 남긴다. hover/pressed/disabled/focus/mixed 상태를 확인한다.

### 실제 요소 연결 원칙

- 아이콘의 의미와 디자인 정의를 명시 입력으로 받는 같은 팩토리를 실제 UI와 미리보기에서 사용한다. 모듈마다 연결된 Shared 코드의 정적 상태를 공유 상태로 가정하지 않고 Core의 디자인 선택을 단일 출처로 삼는다. 미리보기와 팩토리 내부 도형은 전체 UI 갱신의 하위 탐색에서 제외한다.
- 현재 창의 실현된 요소와 새 창·가상화 컨테이너·팝업은 Loaded 때 연결하고 Unloaded·Closed에서 구독을 정리한다. 갱신은 UI 디스패처에서 수행하며 매 프레임 전체 시각 트리를 훑거나 데이터 항목을 전부 열거하지 않는다. 창을 닫을 때 분리된 요소도 정리할 수 있게 소유 루트를 기억한다.
- 이름 있는 FontIcon과 바인딩 대상은 같은 객체를 유지한다. 터미널 문자로 바꿀 때 원래 의미 글리프를 별도로 보관하고 외부 글리프 갱신·컨테이너 재활용을 반영한다. 버튼 템플릿이 필요하면 초기 구성에만 설정하며 디자인 전환 때문에 모듈·콘텐츠를 옮겨 붙이지 않는다.
- 체크·드롭다운·트리·슬라이더의 네이티브 입력·상태·포커스는 유지한다. 원래 템플릿 상태 대상을 제거하지 않고 필요한 표시 부분만 디자인 기호와 연결한다. 시스템 고대비에서는 재정의한 네이티브 상태 자원에도 시스템 색을 제공한다. 갤러리의 미리보기 선택 외곽은 실제 적용 디자인 표시와 독립적으로 보존한다.
- 콘텐츠 제외 정보는 기존 의미 Tag를 덮어쓰지 않는 약한 역할 정보로 전달한다. 문서 편집·Markdown·가상 편집기·WebView·PDF·Office 페이지/본문·인쇄와 문서 텍스트 썸네일을 보호한다. 파일명·메타 정보·문서 도구막대는 UI로 남긴다. 녹음 상태의 빨간 표시와 그래프 의미색은 중립 팔레트로 덮지 않는다.

## 완료 기준

11개 선택·저장·재실행·기존 창/다중 창 갱신과 두 보기의 선택 동기화를 검증한다. 전체 앱의 대표 모듈·메뉴·가상화 타일에서 디자인 누락과 한글 대체 글꼴, 밝고 어두운 테마·배율을 확인한다. 실제 화면 검수는 사용자에게 먼저 묻고 진행한다. 현재 구현·검수·배포 상태는 [HANDOVER](../../HANDOVER.md)에 갱신한다.
