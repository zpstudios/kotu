# A389 녹화·녹음 하단 조작줄

- Start recording, Stop and save, Discard recording, Open result folder를 `IBottomBarProvider.TakeBottomBar`가 반환하는 `_bar`에 모았다. 기존 중앙 Open folder 버튼 및 중앙의 별도 큰 시계는 제거했다. 중앙에는 녹화/녹음 선택지·소스·저장 위치·오류 및 결과 경로가 남는다.
- 하단 줄을 가로 StackPanel에서 Stretch Grid로 바꿨다. 네 버튼 Auto 열과 상태 Star 열로 구성하며 ColumnSpacing은 6 DIP다. 버튼마다 Width/Height 32, MinWidth/MinHeight 0을 명시해 기본 최소 폭에 밀리지 않도록 한다. 네 버튼+간격의 최소 폭은 152 DIP이며 상태 텍스트는 CharacterEllipsis로 남은 공간에 맞춘다.
- 앱 공통 BottomBarButtonStyle을 리소스에서 가져오되, 리소스가 없으면 기본 버튼 템플릿으로 표시한다. 같은 32 DIP/패딩 2/테두리 1/모서리 4/중앙 정렬을 로컬 값으로 유지한다. AudioPlayerView.xaml과 VideoPlayerView.xaml의 BottomBarButtonStyle 선례를 따른다.
- RecordModule의 녹화 글리프 E7C8, 기존 삭제 E74D 및 폴더 E8B7 글리프와 SDK Symbol.Stop을 사용한다. 각각 별도의 FontIcon 인스턴스를 생성하며 사용자 지정 템플릿/공유 Geometry를 추가하지 않는다.
- 모든 버튼에 ToolTip와 AutomationProperties.Name을 설정한다. 결과 폴더 툴팁은 마지막 저장 또는 부분 녹화 파일의 폴더를 연다고 명시한다. 결과 경로 없음/녹화 중에는 기존 규칙대로 비활성화된다. 기본 버튼의 키보드 포커스/Enter/Space/Tab 동작을 유지한다.
- 각 버튼의 부모는 `_bar` 하나이며 `_bar`는 RecordView.Content에 넣지 않는다. 셸 ModuleBarHost만 TakeBottomBar 결과를 받는다. MainWindow의 기존 교체/정리 경로를 유지한다.
- 기존 `_barClock` 상태/시간 갱신 및 트레이 상태를 유지한다. 시작/종료/폐기/결과 파일/워커 타이머의 동작과 활성화 규칙은 변경하지 않는다. 후속 A390이 `_barClock`의 표시 상태를 확장할 수 있다.

검증: Record 모듈 Release/x64 빌드 통과(TreatWarningsAsErrors=true, 경고 0개/오류 0개). 정적 검사에서 한 부모 배치, 중앙 시계 참조 제거, 네 버튼의 명시 크기/툴팁/접근성 이름, 스레드 경로 미변경을 대조했다. git diff 공백 및 한글 이스케이프 검사 통과. 단순 표시 변경이므로 중복 단위 테스트는 추가하지 않았다.

실기기 확인: GUI 미실행. 좁은 창에서 네 조작 버튼/상태 표시, UI 배율과 라이트/다크 테마, 키보드 포커스 및 스크린리더 이름, 모듈 전환 시 하단 줄 교체는 실제 화면 확인이 필요하다. 152 DIP보다 좁은 모듈 바에 네 버튼이 모두 들어간다고 주장하지 않는다.
