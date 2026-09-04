# A345 — 탐색기 두 표면 UI 가상화 사전 조사 (2026-09-04)

> A342 배치 5 실측(상한 500으로 진입 5초→0.9초, GC 1/8)이 "XAML 객체 수가 GC의 원인"을 확정했고,
> 사용자가 "500개만 보이는 UX" 대신 **ⓑ 가상화**를 택했다. 이 문서는 착수 전 Explore 조사의 원문이다
> (코드 변경 0). 배치 계획의 정본은 `docs/REQUIREMENTS.md` A345 본문이며, 여기는 근거·전수 목록 보관용.

## 결과 (2026-09-04 완주 — v0.335.0~v0.338.0)

- 배치 1 v0.335.0 데이터 축 `ExplorerEntryVm`(Tag = 뷰모델) → 배치 2 v0.336.0 좌 리스트 가상화(+핫픽스 v0.336.1 콘텐츠 Stretch) →
  배치 3 v0.337.0 중앙 타일 가상화(CCC 위상 0/1 미리보기 · 뷰모델 캐시) → 배치 4 v0.338.0 정리(낙수 42 · 계측 축 정리 · 결정 확정).
- §6의 계획과 달라진 점: ① 좌·중앙 뷰모델 객체 공유는 **하지 않기로 확정**(셸 시그니처 무변경 · 표시 상태는 표면별 독립이 맞다)
  ② 이름변경은 보수안 ⓐ로 **확정**(뷰모델화 ⓑ 기각 — 실기기 문제 보고 없음) ③ 상세 fetch는 "목록 스냅샷 순회"가 아니라
  CCC 위상 0의 `RequestDetail`(보이는 행만)로 — A342 배치 3의 캐시 히트 조각화가 통째로 불필요해져 삭제 ④ 훅은 "부착 N종 = 해제
  N종"이 아니라 **컨테이너당 1회 부착 + 핸들러 안 지연 해석(`VmOf`)** 으로 — 해제할 것이 없다.
- 실기기 결과: 좌 리스트 10,000개 끝까지 확인(사용자). 중앙은 v0.337.0/v0.338.0 확인 대기(HANDOVER §4).
- 후속 = 낙수 41(스트리밍 열거 + 얇은 Entry — 스캔 천장 10,000 자체를 없애는 길).

## 0. 요약

- 두 표면 모두 `ItemsSource`를 쓰지 않고 **`ListViewItem`/`GridViewItem` 컨테이너 자체를 `Items.Add`** 한다
  (`ExplorerPane.xaml.cs:825-826`, `ThumbnailExplorer.xaml.cs:579,653`). 이 구조에서 가상화는 원리적으로 0%.
- 컨테이너 직접 접근 지점은 두 파일 안에 60여 곳, **외부 파일은 `ExplorerFileOps.ApplyCutMark`(:154-160) 하나**.
  셸(`MainWindow`)·오버레이(`FileListOverlay`)는 공개 API로만 접근 — 시그니처를 안 바꾸면 한 줄도 안 고친다.
- **가상화 선례 2건 실존**: `PdfPane`(ItemsSource + DataTemplate + `ContainerContentChanging` 위상 렌더 +
  ItemContainerStyle) / `ArchiveView`(ObservableCollection + `x:Bind` DataTemplate). 선례 0건 위험 아님.
- **최대 위험 = 컴파일이 통과하는 조용한 파괴.** 선택·클릭·드래그 판정이 전부
  `is FrameworkElement { Tag: ExplorerListing.Entry }` 패턴이라 `SelectedItem`/`ClickedItem`이 데이터 객체로
  바뀌면 예외 없이 null/빈 목록으로 떨어진다. CI도 `TreatWarningsAsErrors`도 못 잡는다.
- **두 번째 위험 = 컨테이너 재활용 잔존 상태.** 잘라내기 `Opacity`·체크박스 `IsChecked`·인라인 이름변경
  `TextBox` 삽입이 컨테이너 콘텐츠에 직접 쓰인다 → 재활용되면 엉뚱한 파일이 흐려지고·체크되고·이름이 바뀐다.

## 1. 현재 구조

### 1.1 XAML

| 요소 | 위치 | 속성 |
|---|---|---|
| `IconGrid` (GridView) | ExplorerPane.xaml:63-68 | `SelectionMode=Extended` · `IsItemClickEnabled` · `ItemClick=OnItemClick` |
| `ListPane` (ListView) | ExplorerPane.xaml:88-95 | 동일 + `Padding=4,4,4,48` · `BorderThickness=1,0,0,0` |
| `TileGrid` (GridView) | ThumbnailExplorer.xaml:33-38 | 동일 + `SizeChanged=OnSizeChanged` |

`ItemsPanel` 명시 없음 → 기본값(`ItemsStackPanel`/`ItemsWrapGrid`) = 가상화 패널은 이미 자리에 있다. 호스트
(`FileListOverlay` `ListHost` Grid 3* / `MainWindow` `ExplorerHost`·`S4CenterHost` Grid)는 `ScrollViewer`로
감싸여 있지 않다(감싸면 무한 높이로 가상화가 깨진다).

### 1.2 좌 리스트 생성 경로

`NavigateTo(:653)` → `NavigateToAsync(:666)` → `Worker.Run(ExplorerListing.List)(:728)` → `RefreshView(:492)` →
`Fill(:788-816)`(Items.Clear → 첫 조각 `AppendFillRange(:819-828)` → `StartFillAppendLoop(:839-891)` 80/틱) →
`FinishFill(:913-940)`(안내 행 · `_fillDone` · `ApplyCurrentFileSelection` · `LoadDetailsAsync` · `FillCompleted`).
`MakeListItem(:1298-1385)` = 평평한 Grid(아이콘/이름/상세/체크박스) + `ListViewItem{Tag=entry, MinHeight=0,
Padding=12,1,12,1}` + `ApplyDetail`·`ApplyCutMark`·`AttachContextMenu`·`AttachDragDrop`·`DoubleTapped`.
`MakeGridItem(:1140-1179)` 동형. `MakeOverflowNotice(:953-966)`는 Tag 없음·IsEnabled=false.

### 1.3 중앙 타일 생성 경로

`ShowEntries(:569)` → `_showSeq++` → `Items.Clear` → 첫 조각 60 → `UpdateLayout`·`ApplyTileSize(:820-839)` →
`StartTileAppendLoop(:629-680)` → `FinishShowEntries(:703-727)`(안내 타일 · `ApplyTileSize` · 보류 이름변경 소비).
`MakeTile(:846-889)` = 미리보기 4갈래(폴더 글리프/이미지/텍스트/셸 썸네일) + A337 클라우드 배지 + 캡션 +
`GridViewItem{Tag=entry, Stretch}` + 훅. `ApplyTileSize` 폴백(:833-838)은 `Items` 순회로 크기 직접 대입.

### 1.4 설계 의도(되돌리면 안 되는 것)

`ExplorerPane.xaml.cs:775` "표시 목록을 항목 컨테이너로 다시 만들어 채운다(`ItemsSource`·`DataTemplate` 없음
— 구조 규칙)". 가상화는 이 구조 규칙의 폐기 선언이고, 이를 인용하는 주석 전수(EP:944-945, 2219 · TE:731-732,
755, 1118-1120 · FO:157)가 개정 대상이다.

## 2. 컨테이너 직접 접근 지점 전수 (EP=ExplorerPane.xaml.cs · TE=ThumbnailExplorer.xaml.cs · FO=ExplorerFileOps.cs · RB=ExplorerRenameBox.cs)

- **(A) 조립·상한·수명** — EP:319-321 `ApplyCutMarks` 전량 순회 · EP:698-699,741-742,794-795 `Items.Clear` ·
  EP:801,825-826 `Items.Add` · EP:919-924 안내 행 `Items.Add` · EP:1140-1179/1298-1385 생성 · EP:167,176,839-902,974
  루프 수명 · TE:574,579,611,653,711 · TE:586,723 `UpdateLayout` · TE:820-839 `ApplyTileSize` 폴백 · TE:846-889,739-753.
- **(B) 선택/열린 파일 표시(A240·A323·A336)** — EP:1748-1763 `SelectedFilePath`/`SelectedEntry`(`SelectedItem is
  FrameworkElement { Tag: Entry }`) · EP:1808-1821 `RevertSelectionToCurrentFile` · EP:1829-1855
  `ApplyCurrentFileSelection`/`SelectCurrentIn`(`FindItemByPath` → `SelectedItem` → `ScrollIntoView`) ·
  EP:1896-1903,1950-1956 `SelectedPathsOf`(`.OfType<FrameworkElement>().Select(i => i.Tag)`) · EP:2024,2034,2045,2090
  키보드 · TE:225-231,246-250,369-418,493-519,721-722,1825-1827.
- **(C) 체크박스 다중 선택(A179)** — EP:2283 `_checkedPaths`(작업 집합 단일 원본) · EP:674,764-768 prune ·
  EP:1345-1362 `IsChecked = _checkedPaths.Contains` · EP:2301-2322 · EP:2332-2339 `CheckedPathsInView`(`Items` 순회) ·
  EP:2345-2377. 중앙 타일에는 체크박스 없음.
- **(D) 잘라내기 표시** — FO:154-160 `ApplyCutMark(object)`(`SelectorItem { Content, Tag: Entry }` → `Opacity`) ·
  EP:317-321/TE:334-337 전량 재적용 · 생성 시점 반영 EP:1173,1379/TE:883 · `CutMarksChanged` 구독 수명.
- **(E) 이름변경(A94·A156·A192)** — EP:1189-1205 `FindItemBlock`(콘텐츠 패널 자식 이름 조회) · EP:2143-2149
  `BeginRenameOf` · EP:1018/TE:1606 메뉴 Rename(컨테이너 캡처) · EP:2183-2217 `CreateFolderThenRenameAsync`
  (`WhenFillCompleteAsync` → `FindItemByPath` → `UpdateLayout` → `BeginRenameOf`) · EP:2220-2223 `FindItemByPath` ·
  TE:211,447,465,716-726 `_pendingRenamePath` · TE:756-772 `FindTileByPath`·`tile.Children[1]` 인덱스 계약 ·
  RB:64-139 `Begin`(host.Children.Insert · nameBlock Collapsed) · EP:2672,2693-2696 편집 중 재스캔 보류.
- **(F) 상세 줄 지연 적용** — EP:1273-1279 `ApplyDetail` · EP:1436-1540 `LoadDetailInfoAsync`(`Items.ToList()`
  스냅샷 · 캐시 히트 즉시 적용 · A342 배치 3 80건 양보) · EP:1388 `_infoCache` · EP:1240-1267 순수 문자열 조립.
- **(G) 좌 그리드 썸네일** — EP:1652-1713 · **EP:1675 `(Grid)((StackPanel)item.Content).Children[0]`** 무명 캐스트.
- **(H) 우클릭 메뉴(A335)** — EP:995-1000 `AttachContextMenu`(빈 MenuFlyout + Opening) · EP:1007-1137 ·
  TE:1575-1710. 항목당 남은 객체 = MenuFlyout 1 + 델리게이트 1.
- **(I) 드래그/드랍** — EP:1915-1936 `AttachDragDrop`(`CanDrag`·`DragStarting` 데퍼럴·폴더면 `AllowDrop`) ·
  EP:1909-1911 `DragItemsStarting`은 await 불가라는 설계 근거 · TE:897-922,1848-1879.
- **(J) 더블클릭·원시 눌림·키보드** — EP:2386-2398 `OnItemClick`(`e.ClickedItem is FrameworkElement { Tag }`) ·
  EP:2406-2416(`item is GridViewItem ? IconGrid : ListPane`) · EP:2233-2272 A131 원시 눌림 · EP:2010-2105 ·
  TE:1724-1838.
- **(K) 중앙 전용** — TE:109-124 `DeferPreview`(`EffectiveViewportChanged`) · TE:941-1404 미리보기 4갈래
  (`host.Children.Clear` · `_showSeq` 이중 대조) · TE:861,1313-1326 클라우드 배지 · TE:1281-1290 대기 배지 ·
  TE:1515-1558 오디오 정보.
- **(L) 표면 밖** — FO:154-160만 영향. `MainWindow.xaml.cs:269-292,2069-2086,3844-4388` ·
  `FileListOverlay.xaml.cs:86-259`는 전부 공개 API(`ShowEntries`·`ShowLoading`·`FillCompleted`·`SelectedEntry`·
  `ClearSelection`·`RevertSelectionToCurrentFile`·`SetCurrentFile`·`DisplayFolder`·`CurrentEntries`·`SetColumns`).

## 3. 가상화 뒤의 분류

- **(a) 뷰모델 속성으로** — 이름·상세·툴팁(`BuildDetailText`/`BuildTooltipText` 재사용) · `ApplyDetail` → 세터 +
  INotifyPropertyChanged(화면 밖 값도 보존) · 체크 `IsChecked` · 잘라내기 `ContentOpacity` · 클라우드/대기 배지
  Visibility · 확장자 타일 라벨·색 · `CheckedPathsInView`(뷰모델 순회 = 같은 WYSIWYG 계약) · 선택 API
  (`SelectedItems.OfType<EntryVm>()` — ArchiveView.xaml.cs:442 선례) · `FindItemByPath`(오히려 정확) ·
  `MakeOverflowNotice` 폐기.
- **(b) 실체화된 컨테이너만(CCC/ContainerFromItem)** — 컨텍스트 메뉴 부착(Opening 구독 누적 주의) · 드래그/드랍
  (`InRecycleQueue`에서 반드시 해제) · `DoubleTapped` · `AllowDrop`(폴더만 — 매번 재설정) · `MinHeight/Padding` →
  `ItemContainerStyle`(PdfPane.xaml:31-38 선례) · 좌 그리드 썸네일·중앙 미리보기 → CCC 위상 렌더
  (PdfPane.xaml.cs:176-206 선례 — `DeferPreview` 통째 폐기) · `ApplyTileSize` 폴백 삭제 · `SelectCurrentIn` =
  `SelectedItem = vm` + `ScrollIntoView(vm)`.
- **(c) 구조적으로 어려운 것** — **인라인 이름변경**(편집 중 스크롤 = 컨테이너 재활용 = 편집 상자가 다른 파일 행
  으로 이동 → 데이터 사고. 보수안 ⓐ `ScrollIntoView→UpdateLayout→ContainerFromItem` + CCC 재활용 시 강제 커밋 /
  권장 ⓑ 뷰모델 편집 상태 `IsRenaming`·`EditingName` + 템플릿 안 TextBox) · `CreateFolder/FileThenRenameAsync`
  순서 계약 · `_pendingRenamePath` 소비(조립 개념 소멸 → ItemsSource 대입 직후로 단순화) · TE:770 인덱스 계약 ·
  A131 원시 눌림(경로 키 기반이라 그대로 유효, `.Tag`→`.Content`만) · `LoadDetailInfoAsync`/`LoadThumbnailsAsync`
  스냅샷 계약(상한이 사라지면 fetch 폭주 → CCC 위상으로 "보이는 것만" + 캐시) · `ApplyTileSize`의
  `ItemsPanelRoot` 타이밍 · `IconGrid` 리스트 전용 모드 가드 · 안내 행(`ItemsSource` 상태에서 `Items.Add` = 즉시
  예외 → 삭제).

## 4. 저장소 선례

- **PdfPane**(정본) — PdfPane.xaml:13-39(ListView + `ContainerContentChanging` + DataTemplate +
  ItemContainerStyle `MinHeight=0/Padding=0`) · PdfPane.xaml.cs:113,135(ItemsSource 대입/null) · :162-177
  (`ContentTemplateRoot` 캐스트 · `InRecycleQueue` 해제 · Phase 0 `RegisterUpdateCallback`) · :179-206
  (`ReferenceEquals(args.ItemContainer.Content, item)` 재활용 검증) · :126,418,436(`UpdateLayout`·`ItemsPanelRoot`).
- **ArchiveView** — ArchiveView.xaml:53-77(`ItemsSource="{x:Bind Rows}"` · `x:DataType` DataTemplate · 리스트 수준
  `DoubleTapped`) · ArchiveView.xaml.cs:16-31 `ArchiveRow`(표시 전용 래퍼, 탐색기와 같은 글리프) · :197
  `ObservableCollection` · :442 `SelectedItems.OfType<ArchiveRow>()`.
- `ItemsRepeater` 0건 → 쓰지 말 것. `ItemsStackPanel`/`ItemsWrapGrid` 명시 0건 → 명시하지 않는 것이 정답.
- CI 판정: 필요한 API 전부 실사용 선례 있음 → 규칙 3 "선례 0건" 조항에 걸리지 않는다. 런타임 부류는 별개(§5).

## 5. 위험 목록

1. **`Items`/`ItemsSource` 혼용 = 즉사** — `ItemsSource` 상태에서 `Items.Add/Clear`는 `InvalidOperationException`.
   현행 직격탄 EP:919-924, TE:711(안내 행), `Items.Clear` 5쌍. 컴파일 100% 통과 → 대형 폴더 첫 진입에 터진다
   (v0.174.1 계열). 통과 조건 = `grep "Items\.Add\|Items\.Clear"`에 `flyout.Items.*`만 남기.
2. **패턴 매칭의 조용한 실패(최대 함정)** — EP:1752,1763,1899,1953,2024,2045,2388,2410,2415,2221-2223,2304,2317,
   2334-2336 · TE:226,231,380,496,505,757-758,768,834,1764,1778,1798. 증상 = Enter/더블클릭 열기·정보 패널·
   Del/Ctrl+C/드래그·F2·체크 전멸(예외 없음). 통과 조건 = `Tag:` 패턴 0건 grep 표.
3. **가상화 패널 요건** — 무한 높이 호스트 금지(현행 안전) · `ItemsPanel` 명시 금지 · `ItemsWrapGrid`는 균일 셀
   (`ApplyTileSize`가 충족, 단 `ItemsSource`↔`ItemWidth` 대입 순서) · 좌 리스트 행 높이 가변(상세 줄 초기값이 빈
   문자열이면 스크롤 점프 — 현행은 EP:1378 초판 적용으로 자리 확보) · **A198 행 높이 압축 3하한을
   `ItemContainerStyle`로 옮기지 않으면 40px 회귀**(눈에 보인다).
4. **재활용 잔존 상태** — 잘라내기 Opacity(표시) · 체크 IsChecked(높음 — 오조작 유도) · AllowDrop(높음) ·
   CanDrag/DragStarting 누적(높음) · ContextFlyout Opening 누적(최고 — 옛 항목 Cut/Delete) · DoubleTapped 누적 ·
   인라인 편집 TextBox(최고 — 잘못된 파일 이름변경). 대응 = PdfPane.xaml.cs:166-170 `InRecycleQueue` 해제 +
   :195/199 `ReferenceEquals` 검증 — 통과 조건표의 핵심.
5. `IsItemClickEnabled` + `Extended` 조합은 현행 그대로 합법. `ClickedItem`이 데이터 객체가 되는 것만 주의.
   A85/A131 세 겹 판정 유지, 대상 해석만 교체. `SelectionMode`를 `Multiple`로 바꾸지 말 것(EP:1338-1339).
6. **CI가 못 잡는 런타임 부류** — v0.174.1(공유 PathGeometry 크래시) · 이번 고유 = 혼용 예외 ·
   `ContentTemplateRoot` 캐스트 실패(PdfPane `is Border` 가드 선례) · `ItemsPanelRoot` null · `ContainerFromItem`
   null. DataTemplate 안 공유 리소스 인스턴스 금지(TE:1066 `new SolidColorBrush`는 컨버터/뷰모델 속성으로).
   `System.IO.Path` 모호 참조(v0.310.1).
7. **성능** — 방향은 맞다(실체화 = 화면 분). 단 뷰모델 수가 2,000→무제한이면 `ExplorerListing.List` `maxItems`
   (ExplorerListing.cs:47) · 상세 fetch · 프리페치(4,000 상한)가 비례 → "상한 복원"과 "스캔 상한"은 별개 결정.
   분할 조립 루프·`_fillDone`·`WhenFillCompleteAsync`·`CompositionTarget.Rendering` 해제 의무가 소멸.
   `NavDiagnostics` 마크(`fill0/fillN/cfill0/clay/cfillN/prev0`, `NoteTick` L/C)가 조립 구조 전제 → 판별식 재정의.

## 6. 배치 분할(직렬 4배치) — 좌 리스트 먼저

근거: ① 실사용 표면은 `ListPane` 하나(`IconGrid`는 `ConfigureListOnly`로 휴면) — 롤백 단위가 작다 ② ListView +
ItemsStackPanel + ItemContainerStyle이 PdfPane과 1:1 ③ 중앙은 좌의 하류(`ViewChanged`가 EntryVm을 흘려 뒤 배치가
얹힌다) ④ 데이터 사고 갈래(체크·편집·잘라내기)가 좌에 모여 있다 ⑤ 중앙은 미리보기 4갈래+배지 3종으로 두껍다.

- **배치 1 데이터 축 신설(표면 무변경)** — `EntryVm`(`src/KOTU.App/ExplorerEntryVm.cs`, ArchiveRow 확장형) ·
  순수 함수 이동 · `RefreshView`가 `_displayVms` 생성 · `Tag = vm` + 패턴 전수 교체(FO:158 포함).
  통과 = `Tag: ExplorerListing.Entry` 0건 · `Tag = entry` 0건 · 실기기 회귀 0. 최대 함정 = FO:158 미수정 시
  잘라내기 조용히 전멸.
- **배치 2 좌 리스트 가상화** — ItemTemplate(`x:DataType`+`x:Bind`) + ItemContainerStyle(A198 이관) · 조립 루프
  → `ItemsSource` 한 줄 · 안내 행 삭제 · CCC(부착 N종 = 해제 N종) · `ApplyDetail` → 세터 · 이름변경 보수안 ⓐ.
  `IconGrid` 무접촉. 통과 = `Items.Add/Clear` 0건 · §5-2 표 전수 · CCC 쌍 대조표 · 10,000개 끝까지 스크롤 ·
  왕복 후 체크/흐림 잔존 없음 · 행 높이 32px · `gc/pause` ≤ v0.333.0. 최대 함정 = 재활용 잔존 · 행 높이 40px 회귀.
- **배치 3 중앙 타일 가상화 + 미리보기 CCC 이관** — ItemTemplate + ItemContainerStyle(Stretch) · `ShowEntries`
  축약 · `DeferPreview` 폐기 → CCC Phase 0 + `RegisterUpdateCallback` · 4갈래 `_showSeq` 대조를 `ReferenceEquals`
  검증으로 보강 · 배지 3종 뷰모델화 · `ApplyTileSize` 폴백 삭제 · `_pendingRenamePath` 소비 시점 단순화 ·
  TE:770 인덱스 → 이름 조회. 통과 = 첫 화면 셀 크기 즉시 · 왕복 시 다른 파일 미리보기 없음 · `SetColumns` 즉시 ·
  A175 하이드레이션 0 · 계측 라벨 재정의. 최대 함정 = `ItemsWrapGrid` 셀 크기 타이밍 · TE:1118-1120 "컨테이너
  재사용 없음" 전제 무효.
- **배치 4 이름변경 뷰모델화 + 상한/스캔 정책 + 정리** — `ExplorerRenameBox` 결정 · `MaterializeLimit`·
  `maxItems` 최종 · 고아 정리(`FillChunkItems`·`TileChunkItems`·`EagerPreviewCount`·`PreviewPrefetchDip`·
  `WhenFillCompleteAsync`·`_fillDone`·핸들러 — CI가 잡아 준다) · `NavDiagnostics` 재정의 · 주석 정본 개정
  (EP:775,944-945,2219 · TE:731-732,755,1118-1120 · FO:157) · 가이드. 최대 함정 = 문서·주석 부채.

## 부록 — 착수 전 확인 3건

1. 상한 정책: 가상화 후 `MaterializeLimit` 폐지? `ExplorerListing.List` `maxItems=2000`(스캔 상한)은? — 후자를
   두면 가상화해도 2,000개까지만 보인다.
2. 이름변경 편집: 보수안 ⓐ(`ContainerFromItem` 대기) vs 뷰모델화 ⓑ(`ExplorerRenameBox` 전면 개정).
3. A342 부록 B(상한 500 vs 가상화) 소진 — 이 조사가 그 결정의 입력.
