# A384 트리 표시 밀도 개선

- 범위: `src/KOTU.App/Overlays/FileListOverlay.xaml`의 `FolderTree`만 변경.
- 글자 크기: TreeView와 생성되는 TreeViewItem에 12 DIP를 적용. 하단 `ExplorerPane.xaml` 파일명 TextBlock의 FontSize 12와 일치한다.
- 행 간격: TreeView 리소스 범위에서 `TreeViewItemPresenterMargin`을 `4,2`에서 `4,0`, `TreeViewItemPresenterPadding`을 `0,3,0,5`에서 `0,1`, `TreeViewItemMinHeight`를 28에서 22로 조정한다. `TreeViewItemContentHeight`는 20을 유지한다. 기본 템플릿 기준 정상 글자 크기의 행은 32 DIP에서 22 DIP로 줄어든다.
- 근거: 설치된 Microsoft.WindowsAppSDK.WinUI 1.8.260803003의 `Microsoft.WinUI/Themes/generic.xaml`에 위 네 리소스 키가 선언되어 있으며, TreeViewItem 기본 템플릿이 동일한 키를 ThemeResource로 참조한다.
- 최대 함정 대응: TreeView FontSize만 줄이는 것으로는 기본 Presenter의 여백/최소 높이가 줄지 않는다. 해당 네 리소스와 항목 FontSize를 함께 조정한다.
- 기본 템플릿을 복제하지 않으므로 펼침 화살표의 히트 영역, 선택 표시, 들여쓰기, 키보드 포커스, 테마 상태 및 탐색 이벤트를 유지한다. 고정 Height를 지정하지 않아 Windows 텍스트 확대 시 콘텐츠 높이에 따라 행이 늘어난다. 셸의 기존 UiScale 변환도 그대로 적용된다.

검증: XAML XML 파싱 및 git diff 공백 검사 통과. App x64 로컬 빌드 통과(TreatWarningsAsErrors=true, 경고 0개/오류 0개). 최초 AnyCPU 빌드는 기존 ScreenRecorderLib의 플랫폼 제한으로 실패했으며 x64로 다시 실행했다.

실기기 확인: 앱의 트리 실제 행 높이, 하단 파일명과 글자 크기 일치, 긴 폴더명 및 텍스트 확대, 펼침/접기와 키보드 선택, 라이트/다크 및 UI 배율 변경은 GUI에서 추가 확인이 필요하다. 이번 검증에서는 GUI를 실행하지 않았다.

## A430 이름과 펼침 표시 정렬

2026-11-23 사용자는 v0.433.2에서 폴더명과 펼침·접힘 표시의 세로 위치가 어긋난다고 보고했다. 스킨별 차이는 아직 사용자 관측으로 확정하지 않았다. [요구와 현재 상태](../../REQUIREMENTS.md#a430).

기본 템플릿의 20-DIP 콘텐츠 영역 안에서도 이름을 세로 중앙에 배치한다. 별도로 지정된 콘텐츠 정렬 값이나 바인딩은 보존한다. 펼침·접힘 표시의 원래 12×12 슬롯에는 공통 디자인 아이콘을 그리며, 원본의 표시 여부·투명도·색·크기·위치를 따라간다. 원래 글리프와 입력 컨트롤은 유지하고, 테마별 글꼴에 남아 있던 Segoe용 내부 여백에 새 아이콘을 의존시키지 않는다.

배경 장식과 마찬가지로 컨트롤이 직접 소유한 기본 템플릿 부분만 대상으로 삼는다. 해제 시 표시·구독·클립과 직접 지정한 정렬을 복원한다. A384의 행 높이·여백·들여쓰기와 선택·키보드·펼침 입력 경로는 유지한다. 실제 스킨별 정렬과 동작 판정은 [실기기 기록](../../DEVICE-CHECKS.md#native-media)에 구분한다.
