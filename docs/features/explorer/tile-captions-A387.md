# A387 썸네일 파일명 확대

- `ThumbnailExplorer.xaml`의 공용 TileCaption을 FontSize 11에서 14 DIP로 확대한다. S1과 S4가 같은 템플릿을 사용하므로 양쪽에 적용된다.
- 캡션의 Auto 행 높이 증가가 미리보기 영역을 줄이지 않도록 `ApplyTileSize`의 ItemsWrapGrid.ItemHeight를 타일 폭 + 4 DIP로 조정한다. 기본 글꼴/배율의 줄 높이 증가분에 맞춘 여유이며 타일 폭과 열 수 계산은 유지한다. 고정 캡션 높이를 사용하지 않으므로 Windows 텍스트 확대 시 잘림을 강제하지 않는다.
- 긴 파일명은 기존 CharacterEllipsis와 전체 파일명 툴팁을 유지한다.
- `ExplorerRenameBox.Begin`이 nameBlock.FontSize와 Margin을 편집 TextBox에 복사하므로 이름 변경 중에도 같은 14 DIP가 적용된다.
- 가상화된 컨테이너는 같은 XAML 템플릿을 재사용하므로 개별 컨테이너 상태나 후처리가 필요 없다. 셀 크기는 기존 ApplyTileSize 경로에서 전체에 적용한다.
- 이미지 디코딩, 리샘플링, 작업 스레드 및 UI 반영 경로에는 변경이 없다.

검증: XAML XML 파싱 및 git diff 공백 검사 통과. App x64 로컬 빌드 통과(TreatWarningsAsErrors=true, 경고 0개/오류 0개).

실기기 확인: S1/S4 파일명 가독성, 캡션/미리보기 간격, 긴 이름과 이름 변경, 작은 창과 UI 배율 변경은 GUI 확인이 필요하다. 셀 높이 +4 DIP는 기본 글꼴의 증가분 보정이며 OS 텍스트 확대까지 이전 미리보기 높이를 정확히 보장하는 수치는 아니다. 이번 검증에서는 GUI를 실행하지 않았다.
