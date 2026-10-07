# A386 UI·워커 전수 검토 및 개선 결과

2026-10-07. 시작 소스 `7af74e6` / v0.386.0. 사용자 승인 범위는 전 기능 감사와
검토 계획의 우선 개선 1→2→3→4 순차 구현이다. 다른 백로그는 구현하지 않는다.
코드 사실·자동 시험·실환경 측정을 구분하며 빌드 통과를 UI 응답성 입증으로 표현하지 않는다.

## 1. 설정 저장 — v0.387.0

최대 함정은 비동기화하면서 저장 순서와 해제 시 마지막 스냅샷을 잃는 것이다.
`JsonSettingsService`는 메모리 잠금 안에서 스냅샷만 취득하고 직렬 워커가 JSON/임시 파일/교체를
수행한다. 실행 배치와 최신 대기 배치 하나만 존재하며 대기 중 요청은 같은 완료 Task를 공유한다.
스냅샷은 실행 순서대로 취득한다. Get/Set의 직렬화·역직렬화도 메모리 잠금 밖이다.

UI 호출자(셸·탐색기·설정·문서·영상·오디오·압축·하드웨어·이어보기)는 RequestSave를 사용한다.
실패는 공통 오류 표시와 재시도로 관측한다. 메모리 반영과 창 간 알림은 즉시 유지한다.
녹화 폴더의 Save는 기존 녹화 워커에서 완료/실패를 받아 기존 값 복원·폴더별 안내를 유지한다.
업데이트 확인의 Save도 기존 워커 경로다. Standalone은 SaveAsync도 메모리 전용이다.

일반 닫기: 미저장/작업 가드 → 설정·창 크기 캡처 → 구독/하단/패널/센터 해제 →
중첩 UserControl의 Unloaded 발화/동기 캡처·해제 예약 → 최종 SaveAsync → Close 순서다. 실패 시 재시도 또는
사용자의 명시적인 저장 없이 닫기를 제공한다. 모듈 전환의 저장은 뷰 워커 수명과 독립적이다.
업데이트/관리자 재시작도 UI 소유 설정을 캡처하고 저장 완료 후 기존 재시작 순서로 진행한다.
실제 WinUI Unloaded 순서·대화상자·UAC 취소·종료 조작은 자동 단위 시험으로 입증하지 않았다.

자동 회귀는 느린 쓰기 중 Get/Set, 연속 100회 저장 병합, 최신 값 순서, 저장 실패/재시도,
마지막 캡처 반영과 임시 파일 정리를 다룬다. 실제 SMB/이동식 드라이브 지연은 미측정이다.
로컬 전체 Release/x64 빌드 0경고·0오류. 517/517 및 제한 밖 파일작업36/36 통과(총553).
최초 제한 실행의 파일작업27실패는 같은 바이너리의 제한 밖 실행에서 모두 소멸했다.
오디오/영상의 기존 디스크 즉시 재읽기 시험은 SaveAsync 완료 후 재시작을 검증하도록 갱신했다.
구조20프로젝트·릴리스 게이트7변형·diff·한글 이스케이프 검사 통과.
증거: `artifacts/a386-1-build.log`, `a386-1-tests-final.log`, `a386-1-fileops-unrestricted.log`,
`TestResults/a386-1-final`, `TestResults/a386-1-unrestricted`.

## 2. 사용자 경로 조회 — v0.388.0

최대 함정은 조회 대기 중 사용자가 이동·재생·해제했는데 옛 결과로 목록 인덱스나 폴더를 바꾸는 것이다.
`LatestRequest`가 대기 취소·실행 결과 폐기를 담당하며 네이티브 질의를 강제로 끊지 않는다.

- ExplorerPane 감시: 워커에서 가장 가까운 존재 상위 경로 조회, 감시/탐색 세대·폴더·수명 대조,
  실행 하나와 보류 bool로 이벤트 폭주 병합. 편집 중 재스캔 보류·소실 루트의 기존 실패 경로 보존.
- 영상/오디오: 초기·직접 열기·수동 이웃·EOF 다음 파일 조회를 워커로 옮겼다. 수동 입력은 조회 중
  중복을 합치고, 목록 객체/현재 파일/인덱스/요청 취소를 대조한 뒤 이동한다. 소실 파일 제거,
  수동 한 칸 이동과 EOF 재시도, 루프 예산은 기존 순서다. 샘플 경로 확인도 같은 경로를 쓴다.
- 이미지/문서/All Readable: 초기 확인을 비동기화하고 새 열기·해제 뒤 결과를 폐기한다.
- 셸: 마지막/현재/바탕화면 폴더 선택, 좌 패널, 뒤로/앞으로, 활성화 전달과 세션 복원 확인을
  UI 밖에서 처리한다. 폴더 탐색이 시작되면 보류된 옛 폴더 조회를 취소한다. 활성화 요청은 직렬이다.
- 이름변경 시작: 디스크 재조회 없이 스캔된 IsFolder로 확장자 제외 선택을 결정한다.
- 트리 길목 노드: 존재 확인은 비동기, 노드 생성 전 펼침 세대 재검사. 압축 생성 대화상자의
  고유 이름 탐색도 워커·요청 세대 적용. 진단 로그 폴더 생성/열기 역시 설정 워커로 이동했다.

자동시험: 느린 진행 중 조회 뒤 세대 교체/해제, 큐 진입 전 취소, 존재 상위/전부 소실/탐색 도중 취소.
전체 빌드 0경고·0오류, 520/520 + 제한 밖 파일작업36/36(총556) 통과.
증거: `artifacts/a386-2-build.log`, `a386-2-tests.log`, `a386-2-fileops.log`, `TestResults/a386-2`.
실제 WinUI 연타·SMB 끊김·드라이브 탈착·네이티브 재생의 시간/반응성은 미검증이다.
파일 열기 자체의 오류 처리는 그대로 유지한다. 존재 확인 성공은 실제 열기 성공 보장이 아니다.

## 3. 아이콘 파일 정보 — v0.389.0

최대 함정은 같은 경로 저장·외부 변경 뒤 크기가 고정되거나 늦은 조회가 새 파일을 덮는 것이다.
셸의 `FileMetadataCache`는 경로별 32개 상한·동일 경로 진행 조회 공유·무효화된 엔트리의 결과
폐기를 제공한다. 창 아이콘은 캐시만 읽으며 미확인은 기존 `—`, 소실은 유휴 표시다.
영상 비트레이트·압축률·All Readable 기본 트레이 표시도 `IFileSizeConsumer`로 같은 크기를 받는다.

파일 전환/재열기/저장/정보 변경에 무효화한다. 문서의 같은 경로 저장은 성공 후 기존 정보 변경
계약으로 알린다. 외부 크기 변경·삭제·이름변경은 파일 감시 이벤트를 200ms로 합친다.
감시의 생성·교체·해제와 파일 정보 읽기는 전용 워커 FIFO이며 UI에는 결과만 적용한다.
창 닫힘은 요청 취소 → 감시 해제 Post → 워커 Dispose 순서다. 취소할 수 없는 조회 결과는 버린다.
감시 등록 자체가 실패하면 자동 외부 갱신은 보장하지 않으며 이후 열기/저장/정보 통지에서 재조회한다.

자동시험: 중복 조회 공유, 느린 조회 중 무효화, 빠른 경로 교체와 창 수명 종료, 조회 실패,
캐시 상한, 실제 임시 파일 크기 변경·삭제. 전체 빌드 0경고·0오류, 524/524 + 파일작업36/36(총560).
Release/win-x64 publish 및 구조20프로젝트·릴리스7변형 검사 통과.
증거: `artifacts/a386-3-build.log`, `a386-3-tests.log`, `a386-3-fileops.log`, `a386-3-publish.log`.
실제 WinUI 아이콘·파일 감시/SMB 이벤트 전달과 GDI 비용은 미검증이다. 패키지의 고정 .ico 파일
조회·GDI 합성은 사용자 파일 메타데이터와 별개로 기존 UI 경로에 남아 추가 감사에 기록한다.

## 4. 드라이브 조회 병합 — v0.390.0

최대 함정은 Unloaded에서 워커를 닫은 직후 재표시하면서 옛 조회와 새 조회가 겹치는 것이다.
`CoalescingRefresh<T>`가 실행 하나 + 보류 bool 하나를 유지한다. 타이머는 요청만 보내고 실제
DriveInfo/WMI는 기존 워커에서 실행한다. 숨김은 보류 취소·세대 변경, 다시 보임은 최신 조회 예약이다.
실행 중인 호출을 강제로 중단하지 않으며 끝나야 새 워커의 조회도 시작할 수 있다.
더 최신 요청이 있으면 옛 결과를 표시하지 않는 종전 `_seq` 의미도 유지한다.
실패·취소는 finally에서 실행 상태를 풀며 다음 요청이 정상 재시도한다.

지연 수집 중 요청100회 → 실제 수집2회·최대 동시1회, 숨김/재표시·해제·취소·실패/재시도를
주입 시험했다. 공통 워커의 Post→Dispose FIFO 정리도 검증했다. 고정 순환 풀의 긴 작업 뒤
대기 현상은 제어된 게이트 시험으로 확인했다(다른 워커는 비어 있어도 해당 요청은 미완료).
이는 구조 재현이며 실제 폴더 작업의 지연 ms/분포나 풀 크기 적정성을 측정한 결과는 아니다.
공통 워커 큐·스레드 수·정리 폴백 정책은 변경하지 않았다.

앞 배치의 최종 검수에서 설정 오류의 세대 비교/게시를 한 짧은 잠금으로 묶어 늦은 완료 경합을
막았고, 설정 파일 존재 재조회 직후에도 뷰 수명을 확인하도록 보강했다.
Unloaded 대기는 이벤트 발화와 동기 부분의 완료를 뜻한다. async void 해제의 비동기 후반부나
네이티브 자원 해제 전체를 기다린다는 보장은 추가하지 않았다(기존 비동기 정리 계약 유지).

전체 Release/x64 빌드 0경고·0오류. 최종 일반530 + 파일작업36 = **566/566** 자동시험 통과.
Release/win-x64 self-contained publish, 구조20프로젝트·릴리스 게이트7변형·diff 검사 통과.
증거: `artifacts/a386-4-build-final.log`, `a386-4-tests-final.log`, `a386-4-fileops.log`,
`a386-4-publish-final.log`, `TestResults/a386-4-final`, `TestResults/a386-4-fileops`.
실제 WMI 지연·USB 탈착·WinUI 숨김/재표시 조작과 CPU/메모리 시간 계측은 미검증이다.

## 5. 전 기능 정적 감사 범위와 호출 경로

v0.390.0 기준 src의 생성물을 제외한 C#/XAML **219개**를 목록화하고 I/O·COM·레지스트리·프로세스·
타이머·워커·동기 대기·저장 호출을 검색한 뒤 아래 기능별 진입/실행/반영/해제 경로를 추적했다.
App63, Core68, DocumentModel5, FileOperations4, AllReadable3, Archive13, Audio8, Document15,
Hardware9, Image8, Record10, Video8, Shared5다. 계약·순수 모델·아이콘 도형/XAML 선언도 목록에 포함한다.
이 범위의 **정적 전수 감사**이며 모든 UI 조합·장치·네이티브 API 내부 실행 시간을 실증한 것은 아니다.
표의 '분리'는 확인한 실행 경로를 뜻하며 해당 모듈 전체에 잔여 문제가 없다는 판정이 아니다.
R번호는 다음 절의 추가 발견 사항으로 연결된다.

| 기능/진입 | 실제 작업과 UI 반영 | 취소·해제/최종 판정 | 근거 파일·메서드 |
|---|---|---|---|
| 시작·CLI·외부 활성화·세션 복원 | UI 생성 전 단일 인스턴스 전달 대기. 셸 선택 ParseAsync, 복원 읽기/존재 조회는 비UI. 창 생성·라우팅은 UI | 전달 직렬 게이트 적용. 시작 설정 읽기·재시작 쓰기는 잔여 R8 | [Program](../src/KOTU.App/Program.cs) Main, [App](../src/KOTU.App/App.xaml.cs) OnLaunched, [WindowManager](../src/KOTU.App/WindowManager.cs) DispatchAsync/RestoreSessionCoreAsync |
| 셸·패널·모드·히스토리·창 아이콘·트레이 | 경로는 PathWorker, 파일 크기/감시는 IconWorker. 입력·레이아웃·아이콘 합성·트레이 통지는 UI | 요청/경로/수명 검사, 감시 정리 FIFO. 고정 자산/GDI 잔여 R9 | [MainWindow](../src/KOTU.App/MainWindow.xaml.cs) ShowListOverlay/NavigateHistoryAsync/EnsureIconMetadata/RefreshShellIcons |
| 설정 변경·문서 줌·이어보기·종료 | 메모리 즉시 반영, JSON/파일 교체는 저장 워커. 종료는 UI 캡처 후 await | 최신 대기 병합, 실패 알림/재시도, Standalone 메모리 정책. 전체 GUI 종료는 미검증 | [JsonSettingsService](../src/KOTU.Core/Settings/JsonSettingsService.cs), [SettingsPersistence](../src/KOTU.Core/Settings/SettingsPersistence.cs), CaptureSettings/ConfirmThenCloseAsync |
| 설정의 파일 연결·우클릭 등록 | Settings Worker에서 레지스트리·셸 통지/조회, 결과만 UI | `_uiAlive` 검사/Unloaded 해제, 작업 오류 표시. 관리자/업데이트 종료 잔여 R8 | [SettingsView](../src/KOTU.App/SettingsView.xaml.cs), [ExplorerIntegration](../src/KOTU.App/Integration/ExplorerIntegration.cs) |
| 업데이트 확인·다운로드 | UI 타이머는 시각 갱신/CheckNowAsync 요청만, Task.Run에서 네트워크·저장 | 확인 중복 방지·취소, 설정 뷰 구독수명. 재시작 앞 설정 플러시 | [UpdateCoordinator](../src/KOTU.App/Integration/UpdateCoordinator.cs) StartAutoCheckTimer/CheckNowAsync/RunCheckAsync, [UpdateService](../src/KOTU.App/Integration/UpdateService.cs) |
| 탐색기 스캔·정렬·감시·상세 | Worker 폴더 스캔/감시 생성, FetchPool 상세/썸네일. UI 데이터/표시 대입 | 세대·뷰 수명·취소/감시 병합. 스캔 중복 큐와 UI 전체 정렬 잔여 R7/R10 | [ExplorerPane](../src/KOTU.App/ExplorerPane.xaml.cs) NavigateToAsync/RefreshView/EnsureWatch/OnWatchDebounceExpired |
| 중앙 S1/S4 썸네일·텍스트 미리보기 | ThumbPool/TextPool에서 WinRT/디코드/WIC 축소, UI 픽셀/템플릿 반영 | `_showSeq`·토큰·컨테이너 동일성·캐시 상한/클라우드 가드. 실제 프레임 시간 미측정 | [ThumbnailExplorer](../src/KOTU.App/Controls/ThumbnailExplorer.xaml.cs), [ThumbnailRaster](../src/KOTU.App/Controls/ThumbnailRaster.cs), [ShellFetch](../src/KOTU.App/ShellFetch.cs) |
| 폴더 트리·선택/열린 파일 정보 | 트리 열거 Task.Run, 선택 상세 Worker/프리페치 제한. UI 노드/행 생성 | 길목 존재 조회 세대 검사 적용. 트리 자체 반영 수명 R6, 기본/placeholder 정보 R3 | [FileListOverlay](../src/KOTU.App/Overlays/FileListOverlay.xaml.cs), [ContentInfoOverlay](../src/KOTU.App/Overlays/ContentInfoOverlay.xaml.cs), [SelectionQuickInfo](../src/KOTU.App/SelectionQuickInfo.cs) |
| 파일 복사·이동·삭제·충돌·드래그/클립보드 | FileTransferService/BackgroundJobService 및 Storage 항목 수집은 비UI. UI 충돌 대화상자는 비동기 응답 | 전송 취소·임시 파일 검증·교체 순서 보존. 이름변경/새 파일·폴더는 R2 | [ExplorerFileOps](../src/KOTU.App/ExplorerFileOps.cs), [FileTransferService](../src/KOTU.FileOperations/FileTransferService.cs), [PhysicalTransferStore](../src/KOTU.FileOperations/PhysicalTransferStore.cs), ExplorerDialogs/ExplorerConflictDialog |
| 텍스트 편집·저장·Markdown | document worker에서 읽기/인코딩/쓰기·파싱, UI 편집기·Markdown 객체 생성 | DocumentSession 세대/저장 커밋, 렌더 배치/대형 입력 상한. 편집 UI 예산 R7 | [DocumentView](../src/KOTU.Module.Document/DocumentView.xaml.cs) OpenPath/SaveCoreAsync/CommitSave, [DocumentSession](../src/KOTU.DocumentModel/DocumentSession.cs), MarkdownParser/MarkdownRenderBatch |
| PDF·HTML·오피스·인쇄 | PDF 파일/문서 로드는 워커, 페이지 렌더는 WinRT async+UI 비트맵. WebView2는 UI API+브라우저. Office 파싱/2048자 청크는 워커. 인쇄 UIElement는 UI | PDF load seq, HTML nav seq/Close, Office token/closed, PrintHost 해제. COM 호출 시작/컨트롤 대입 비용 미측정 | [PdfPane](../src/KOTU.Module.Document/PdfPane.xaml.cs), [HtmlPane](../src/KOTU.Module.Document/HtmlPane.cs), [OfficeTextView](../src/KOTU.Module.Document/OfficeTextView.xaml.cs), [PrintHost](../src/KOTU.App/Printing/PrintHost.cs) |
| 이미지 열기·이웃·회전·삭제·배경·클립보드 | worker 읽기/디코드/Magick/PNG/배경 준비·폴더 목록, UI SetSource/좌표/피커·Clipboard | open seq/선읽기 캐시/Unloaded 폐기. 선읽기 File.GetAttributes는 R3 | [ImageViewerView](../src/KOTU.Module.Image/ImageViewerView.xaml.cs), [Clipboard](../src/KOTU.Module.Image/ImageViewerView.Clipboard.cs), ImageFolderNavigator/ImageClipboardSnapshot |
| 영상 재생·루프·자막·이어보기 | worker 엔진 생성/정리·재생목록·자막 탐지/변환·경로 조회, libvlc 디코드/이벤트는 자체 스레드 | Dispatch/tornDown·목록/요청 세대, 초기화 뒤 정리 순서 유지. UI native 제어 R5 | [VideoPlayerView](../src/KOTU.Module.Video/VideoPlayerView.xaml.cs) EnsurePlayer/MoveToNeighbor/AdvanceAfterEnd/OnUnloaded, SubtitleFileLocator/SubtitleCharset |
| 오디오 재생·장치·EQ·VU | audio worker 엔진/목록/경로, VuMeterEngine 장치 init/teardown Task.Run·25fps thread timer | generation·틱 재진입 방지·해제, 스냅샷 UI 전달. UI native 제어/장치 COM R5 | [AudioPlayerView](../src/KOTU.Module.Audio/AudioPlayerView.xaml.cs), [VuMeterEngine](../src/KOTU.Module.Audio/VuMeterEngine.cs), [DefaultAudioInput](../src/KOTU.App/Integration/DefaultAudioInput.cs) |
| 압축 목록·생성·해제·암호·내부 미리보기 | 뷰 worker 목록, 앱 작업 서비스 실행·취소·암호 응답, 고유 출력 이름도 worker | load seq/작업 ID·소유권, 창 닫기 가드·진행 통지. 대량 행 UI 조립 R7 | [ArchiveView](../src/KOTU.Module.Archive/ArchiveView.xaml.cs), [ArchiveJobCoordinator](../src/KOTU.Module.Archive/ArchiveJobCoordinator.cs), SevenZipBackend/ExtractHerePlanner |
| 하드웨어·드라이브·그래프 | 공유 PollingWorker WMI/LHM/이력, 드라이브 전용 worker+최신 요청 병합. 그래프는 UI 메모리 스냅샷 렌더 | 구독 없으면 휴면, Rendering/타이머 해제. 종료 잠금 R4, 늦은 UI 스냅샷 R6, 그래프 비용 R7 | [HardwareModule](../src/KOTU.Module.Hardware/HardwareModule.cs), [SensorService](../src/KOTU.Module.Hardware/SensorService.cs), [HardwareView](../src/KOTU.Module.Hardware/HardwareView.xaml.cs), [DriveStrip](../src/KOTU.App/Controls/DriveStrip.xaml.cs) |
| 녹화 소스·화면/창/마이크·저장/취소 | 발견 worker와 세션 worker 분리, 캡처/인코드/패킷 기록은 native/capture 스레드. UI 피커/대화상자/상태만 | 발견 pending/sequence, 중지·완료 대기→Dispose→임시 파일 공개, Unloaded/작업 소유권 가드 | [RecordView](../src/KOTU.Module.Record/RecordView.cs) DiscoverAsync/StopAsync/OnUnloaded, RecordingSourceDiscovery/RecordingOutput/ScreenRecordingSession/MicrophoneRecordingSession |
| All Readable·공통 계약·작업 서비스 | ChildContentHost/ContentContractSession으로 통지 UI 전달. 자식 worker와 앱 작업 서비스 재사용 | 구독 해제→바 제거→센터 제거 유지, 세대/현재 세션 검사. 전환 로그 R1 | [AllReadableView](../src/KOTU.Module.AllReadable/AllReadableView.xaml.cs), [ChildContentHost](../src/KOTU.Core/Content/ChildContentHost.cs), [ContentContractSession](../src/KOTU.Core/Content/ContentContractSession.cs), [BackgroundJobService](../src/KOTU.Core/Jobs/BackgroundJobService.cs) |
| 진단·광고/브랜딩·공통 스레딩 | 진단 UI 타이머는 메모리 계측/표시. 고정 자산 읽기와 진단 기록은 별도 잔여 | R1/R8/R9/R10. 순수 계약·라우팅·미디어 포맷·Shared 도형에는 작업 스레드 소유권 없음 | [DiagTrace](../src/KOTU.Core/Diagnostics/DiagTrace.cs), ContentTransitionJournal, SponsorAds/BrandAssets, [Threading](../src/KOTU.Core/Threading/ModuleWorker.cs) |

## 6. 추가 발견 사항 — 구현하지 않은 후속 범위

P1 = 사용자 경로의 동기 I/O·종료 잠금 등 우선 분리/검증할 항목. P2 = 수명·처리 예산·정책 보강.
R1~R10은 코드상 경로/주입 시험 사실이며 **실환경 멈춤·크래시가 재현됐다는 뜻이 아니다**.
R11~R12는 후속 실제 UI 조작에서 관찰한 갱신 문제다(§9). 기존 버전과의 UI 비교는 하지 않았다.
이번 사용자 승인인 네 항목을 넘는 수정은 하지 않았다. 새 구현 착수 시 A386 후속으로 선택할 수 있다.

| ID/우선순위 | 확인한 근거·영향 | 권장 다음 변경 / 완료 검증 |
|---|---|---|
| R1 **P1** 진단 동기 저장/공유 잠금 | AllReadable의 Journal→`ContentTransitionJournal.Record`가 UI 전환마다 lock 안에서 Open/WriteLine/Flush/회전. Standalone 외 기본 활성. `DiagTrace.Write/SetEnabled`도 UI 호출이 있고 같은 구조(진단 켰을 때) | 순서 보장·상한 있는 기록 worker와 명시적 종료 flush. 크래시 직전 기록 내구성 요구를 먼저 보존하고 느린 디스크/회전/종료 시험 |
| R2 **P1** 파일 이름변경·생성 | `ExplorerRenameBox.Finish`→`ExplorerFileOps.Rename`, ExplorerPane/ThumbnailExplorer CreateFolder/CreateFile→고유 이름 Exists 반복·Move/Create가 UI. 단일 메타데이터라는 기존 주석은 지연 상한이 아님 | UI 입력/검증과 파일 변경 worker 분리, 작업 중 편집/감시·취소 정책, 이름 충돌/권한/소실과 기존 사용자 오류 안내 보존 |
| R3 **P1** 기본 정보·이미지 선읽기 속성 | `ContentInfoOverlay.LoadAsync`의 provider 실패/부재 폴백과 placeholder 갈래→BuildBasicFileInfo→FileInfo.Length/LastWriteTime. `ImageViewerView.PreloadAsync`는 Worker.Run 전에 File.GetAttributes 실행 | 스캔 스냅샷 재사용 또는 worker 메타데이터 조회. 클라우드 내용 비접근 규칙과 행 순서 보존. 느린 경로/빠른 선택·선읽기 시험 |
| R4 **P1** 마지막 창 종료의 센서 잠금 | `WindowManager.Create`의 Closed→`SensorService.Shutdown`이 UI에서 `_gate`를 얻고 Computer.Close. 같은 lock을 Read의 LHM/WMI 수집이 점유. 구독 해지는 이미 진행 중인 poll을 끝내지 않음 | 공유 poller 종료 요청→수집 완료→센서 해제를 같은 비UI 수명에서 처리. 두 창/긴 Read/종료를 주입해 UI lock 대기와 중복 해제 방지 |
| R5 **P1 조사** 재생/장치 네이티브 호출 | Video/Audio `PlayCurrent/ReplayCurrent/TogglePlayPause`의 new Media/Play/Pause·seek·트랙 조회, DefaultAudioInput COM이 UI. 엔진 생성/해제 분리만으로 이 호출 비용은 보장되지 않음 | 네이티브 진입/복귀 시간을 측정하고 UI 필수 바인딩과 엔진 제어를 분리할지 결정. 취소/EOF/seek/자막/장치 변경·다중 창 순서 회귀 필수 |
| R6 **P2** 트리/하드웨어의 늦은 UI 반영 | FileListOverlay `EnsureDriveRootsAsync/LoadChildrenAsync`는 await 뒤 수명/트리 세대 검사 없이 노드 변경. HasUnrealizedChildren을 작업 전 내려 동시 자동 펼침이 진행 중 로드를 기다리지 않음. HardwareView `OnSnapshot`은 Unloaded 전 큐에 든 ApplySnapshot을 거르지 않음 | 노드별 진행 Task·트리 epoch/뷰 loaded epoch, 하드웨어 최신 스냅샷 하나만 큐잉. 펼침/토글/해제/재로드 지연 주입 시험 |
| R7 **P2 계측** UI 결과 반영 예산 | ExplorerPane `RefreshView`의 Arrange/Fill 데이터 생성은 UI 전체 목록 처리. 문서 Text 대입·EditorDecor/Markdown UI 생성, 압축 행·하드웨어 그래프도 UI. 기존 가상화·대형 문서 상한·렌더 분할은 있으나 프레임 비용 미측정 | 정렬의 데이터 처리만 worker로, UI는 상한/증분 유지. 현재 정렬 안정성·선택 순서 보존. 큰 폴더/문서/압축에서 p95/p99 dispatcher 공백과 배치 예산 측정 |
| R8 **P2** 시작/재시작 로컬 I/O | JsonSettingsService 생성자 Load는 UI 초기화 중 읽음. WindowManager.WriteRestartSession→RestartSessionFile.Write/Delete와 AdminRelaunch Process.Start, UpdateService 관리자 생성/적용 일부는 UI. 시작 전 Program redirect Wait와 치명 오류 로그는 별도 생명주기 | 정상 UI 주기 작업과 시작/실패 경로를 구분. 재시작은 UI 캡처→worker 기록→시작→실패 시 복구 순서를 보존. UAC 거절/세션 쓰기 실패·설치판/Standalone 실제 시험 |
| R9 **P2 계측** 고정 자산/OS UI 호출 | MainWindow.ApplyWindowIcon/RefreshShellIcons의 packaged .ico Exists와 WindowIcon/TrayIcon/BrandAssets/InstanceIcon 파일 읽기·GDI, SponsorAds 초기 JSON/자산 열거. 창 핸들 작업·WebView2/Clipboard/피커는 UI 소유 | 불변 자산 데이터 캐시와 UI 핸들 조작을 분리 검토. 앱 설치 경로/다중 창·모니터 배율·트레이 재생성 비용 계측. UI 객체 자체는 worker로 옮기지 않음 |
| R10 **P2** 공통 큐·낡은 스캔 요청 | ModuleWorker는 상한 없는 FIFO, 취소는 실행 차례의 시작 및 작업의 협조 지점에서만 관측. ExplorerPane 스캔은 loadSeq로 결과만 버리며 Run에 토큰 미전달. ModuleWorkerPool은 고정 순환으로 긴 작업 뒤 대기(주입 재현). Dispose 뒤 Post는 pool 폴백이라 기존 큐와 FIFO 보장 없음 | 호출자 단위 병합·토큰·큐 길이/대기시간 관측 먼저. 공통 큐 변경은 정리 Post/작업 순서/진행률 영향 별도 설계. 스레드 증설은 실제 혼합 부하 계측 뒤 결정 |
| R11 **P2** 외부 변경 뒤 열린 정보 패널의 오래된 값 | 실제 UI에서 TXT 72→4,186 B, ZIP 306→1,306 B로 외부 변경했으나 열린 정보 패널은 72 B/306 B·382% 유지. 탐색기 목록 재조회는 4.1 KB/1.3 KB·1632% 표시. `ContentInfoOverlay.LoadAsync`의 열림 캐시는 경로로 적중하고, 셸 파일 감시의 무효화는 아이콘 메타데이터에 한정 | 현재 파일의 외부 변경을 정보 캐시에도 전달하되 편집 중 본문을 자동 덮어쓰지 않기. 선택/열림 정보의 세대·재조회 예산 유지. 아이콘 자체의 값이 잘못됐다는 증거는 아님 |
| R12 **P2** 현재 폴더 자체 이동의 감시 계기 누락 | 실제 UI에서 열린 `sample` 폴더를 같은 부모의 `sample-moved`로 외부 이동한 뒤 옛 경로·파일 목록 유지. 이동한 폴더 안 파일을 수정하자 부모 폴더로 복구. `ExplorerPane.EnsureWatch`는 현재 폴더 내용만 감시하고 `OnWatchDebounceExpired`의 상위 폴더 탐색은 이벤트에 의존 | 부모의 현재 폴더 이름 변경/삭제 감시 또는 재활성화 시 존재 재확인 검토. UI 폴링 I/O를 추가하지 않기. 이동만 한 경우와 후속 이벤트가 있는 경우를 분리 시험 |

## 7. 검증 수준과 남은 실환경 시험

- **자동 검증 완료**: 버전별 Release/x64 전체 솔루션 빌드, win-x64 publish, 구조/릴리스 게이트,
  설정/경로/아이콘/드라이브의 지연·실패·취소·해제 주입 시험과 기존 전체 회귀.
  최종 구성: Core248 + Document91 + Video36 + Audio22 + Image37 + Archive71 + Hardware25 + FileOperations36.
- **제한 환경 구분**: 최초 FileOperations27실패는 제한 밖에서 같은 바이너리36개 전부 통과했다.
  이후 버전도 파일작업은 제한 밖에서 실행했다. 이를 제품 실패나 네트워크 재현으로 해석하지 않는다.
- **실제 UI 후속 확인**: 사용자의 컴퓨터 사용하기 요청에 따라 v0.390.0 로컬 실행본을 직접 조작했다.
  설정 저장/재실행, 미저장 종료 취소/저장, 다중 창, 이미지/영상/오디오, 압축 해제, 드라이브 재표시,
  녹화 소스 목록과 로컬 폴더 소실을 확인했다. 구체적인 통과·발견·한계는 §9를 따른다.
- **미검증**: 중첩 Unloaded/native 해제의 내부 시간 순서, 설정 쓰기 실패와 재시도 UI,
  실제 SMB 끊김/지연·USB 탈착, 파일 감시 실패복구/삭제 시 아이콘 시각값, 영상 EOF·고속 연타·장치 변경,
  실제 녹화/녹음과 중지/폐기, UAC 취소·업데이트 재시작, 대용량 화면의 프레임 지연·CPU/메모리·풀 대기시간.
  오디오의 실제 청음 품질도 확인하지 않았다. 반복 실행 가능한 UI 자동 E2E 시험은 추가하지 않았다.
- 다음 실기기 시험은 위 실패/장치/성능 경로와 R11~R12 재현을 중심으로 한다.
  진단 계측을 켤 때 R1의 동기 로그 자체가 측정에 영향을 줄 수 있음을 구분한다.

배포 결과와 커밋은 아래 릴리스 검증 기록으로 확인한다. 위 로컬 로그/TRX는 ignored 검증 산출물이며
저장소에는 이 결과 요약과 재실행 가능한 회귀 시험 소스를 남긴다.

## 8. 버전별 원격 검증

각 구현 커밋 직후 master에 push했고 앞 버전 release 성공을 확인한 뒤 다음 버전을 push했다.
GitHub Actions/Release 공개 API로 원본 커밋·워크플로 결과·정식 배포 여부를 대조했다.

| 버전 | 구현 커밋 | build | release | 배포 |
|---|---|---|---|---|
| v0.387.0 | `c271c4cec1a55092317dd705d04297bbf254a0f4` | [성공37563000290](https://github.com/zpstudios/kotu/actions/runs/37563000290) | [성공37563000187](https://github.com/zpstudios/kotu/actions/runs/37563000187) | [정식 릴리스·자산8개](https://github.com/zpstudios/kotu/releases/tag/v0.387.0) |
| v0.388.0 | `0a99945483d692de1c949f08075a641f5e662f16` | [성공37564126334](https://github.com/zpstudios/kotu/actions/runs/37564126334) | [성공37564126379](https://github.com/zpstudios/kotu/actions/runs/37564126379) | [정식 릴리스·자산8개](https://github.com/zpstudios/kotu/releases/tag/v0.388.0) |
| v0.389.0 | `abe6e5cf4a16ea536a7181f6a24921a48f1d61da` | [성공37565392802](https://github.com/zpstudios/kotu/actions/runs/37565392802) | [성공37565392835](https://github.com/zpstudios/kotu/actions/runs/37565392835) | [정식 릴리스·자산8개](https://github.com/zpstudios/kotu/releases/tag/v0.389.0) |
| v0.390.0 | `233356a72dab549f7de230b739384627098cd46b` | [성공37566464053](https://github.com/zpstudios/kotu/actions/runs/37566464053) | [성공37566464055](https://github.com/zpstudios/kotu/actions/runs/37566464055) | [정식 릴리스·자산8개](https://github.com/zpstudios/kotu/releases/tag/v0.390.0) |

네 태그 모두 표의 구현 커밋과 일치하며 draft=false/prerelease=false다. v0.390.0 게시 시각은
2026-10-07 12:29:33 KST. 마지막 release의 전체 테스트·Explorer 선택 COM 전달·실행본 정합성/시작·
설치/설치본 정합성·Standalone build/verification 단계가 각각 success임을 jobs API로 확인했다.
검증 기록을 마무리하는 후속 문서 커밋은 제품 코드와 버전을 변경하지 않는다.

릴리스 워크플로의 설치·시작·Standalone 검사는 배포물 자동 smoke 검증이다.
7절의 남은 장치·네트워크·반응성 측정 완료를 대신하지 않는다.

## 9. 컴퓨터 사용하기로 실행한 실제 UI 확인 — 2026-10-07

사용자의 후속 요청으로 Windows 컴퓨터 사용하기 스킬의 `@oai/sky`를 사용했다.
스크린샷과 접근성 트리를 관찰한 뒤 클릭/키 입력으로 조작했다. UI 조작을 셸 명령으로 대체하지 않았다.
파일 도구는 임시 시험 자료 준비, 외부 변경 재현, 저장 결과/해시 대조, 설정 백업·복원에 사용했다.

### 대상과 환경

- 시험 시간: 약 12:51~13:06 KST. Windows 10.0.26200, x64, 기존 앱 UI 배율150%.
- 대상: `artifacts/a386-4-publish/KOTU.exe`, 설정 화면에서 **Current version v0.390.0** 확인.
  v0.390.0 소스의 로컬 Release publish 실행본이며 설치된 v0.386.0은 정상 종료한 뒤 시험했다.
  게시된 Setup.exe/Standalone.exe 자체를 이번 UI 시험에서 새로 설치·실행한 것은 아니다.
- 로컬 publish에 `7z.dll`이 없어 최초 압축 열기는 의존성 오류를 표시했다. 배포 준비 단계
  `eng/Prepare-Package.ps1`이 별도로 추가하는 파일임을 확인하고 기존 로컬 시험 엔진
  `artifacts/native/7zip/7z.dll`(26.03)을 복사한 뒤 압축 열기/해제를 재검증했다.
  이를 정식 릴리스의 엔진 누락으로 판정하지 않는다.
- 시험 자료: ignored `artifacts/a386-ui-verification` 아래 TXT2개, PNG2개, MP3/MP4 각2개, ZIP.
  실제 사용자 문서는 수정하지 않았다. 설정은 사전 백업했고 시험 앱 종료 뒤 원본과 SHA-256이
  같은 상태로 복원했다. 녹화 버튼은 누르지 않았다.

### 확인 결과

| 시나리오 | 실제 관찰 결과 | 범위/한계 |
|---|---|---|
| 설정 저장·종료·재실행 | Auto-play next file ON→OFF, 정상 종료, 같은 실행본 재시작 후 OFF 유지. 다시 ON 복원 | 일반 성공 경로. 저장 실패/재시도는 미실행 |
| 동일 경로 문서 저장 | TXT48 B를 편집 후 Ctrl+S. 수정 표시 제거·저장 버튼 비활성화, 디스크72 B 및 수정 내용 일치. 정보 패널72 B | 본문 저장과 정보 통지 성공. 작업표시줄/트레이 아이콘 글자는 직접 판독하지 않음 |
| 미저장 문서 종료 취소 | TXT32 B에31자 추가 후 Alt+F4→Cancel. 편집 내용 유지, 디스크32 B 유지 | 취소가 파일 저장/창 해제를 진행하지 않음 |
| 미저장 문서 저장 후 종료 | 같은 문서에서 다시 Alt+F4→Save. 디스크63 B와 수정 내용 확인, 마지막 창/프로세스 종료 | 저장 후 정상 종료의 외부 결과. 내부 Unloaded/native Dispose 완료 시점 계측 아님 |
| 탐색·모듈 전환 | All Readable에서 임시 폴더 선택, 문서→이미지→영상→오디오→압축→문서 전환 | 일반 순차 조작. 고속 연타/지연 경합 시험 아님 |
| 이미지 | image-a 표시 후 Right로 image-b 전환. 이름·272×106·43.2 KB·2/2 일치 | PNG 두 파일의 실제 표시/이웃 탐색 |
| 영상 | MP4 디코딩 화면 표시, Space로0:13/0:32 일시정지. 다른 창 생성 뒤에도0:13 유지 | 영상 EOF, 자막, 탐색바/장치 변경 미실행 |
| 오디오·EOF | MP3 재생 위치 증가, audio-a→audio-b 자동 전환 관찰. 이후 audio-a 복귀도 관찰 | 기존 반복 설정 유지. 실제 소리의 품질·고속 연타는 미검증 |
| 다중 창 | 영상 열린 상태에서 Shift+N, 두 번째 창에서 오디오 재생. 첫 창 종료 후 둘째 창이1번으로 바뀌고 재생 계속 | 일반 두 창 성공 경로. 종료와 긴 native 작업의 경합 미주입 |
| 압축 취소·완료 | ZIP2개 항목/총80 B 표시. 대상 폴더 선택창 Escape 취소 후 원래 화면 유지. 새 폴더로 해제→Completed·결과 탐색기 열림, 파일48/32 B 생성, sample-b 원본과 SHA-256 일치 | 작업 시작 전 피커 취소. 실행 중 작업 취소/대용량은 미검증 |
| 드라이브 표시 수명 | 빈 All Readable/새 창에서 C: NVMe·175 GB/237 GB 표시, 파일 열기 때 사라짐. 녹화 화면을 거쳐 빈 All Readable로 돌아오면 다시 표시 | 실제 UI 표시/해제/재생성 확인. USB/SMB·실행 큐 최대 수 계측 아님 |
| 하드웨어·녹화 화면 | 하드웨어 CPU/RAM 그래프/값 갱신. Record Ready, 화면/창/출력/마이크 목록 발견. 다른 모듈로 전환 가능 | 관리자 전용 센서·드라이버, 실제 캡처·인코딩·녹음·중지/폐기는 미검증 |
| 외부 파일 크기 변경 | TXT72→4,186 B, ZIP306→1,306 B 외부 변경 뒤 열린 정보 패널 값 유지. 이후 탐색 목록은 최신 값 표시 | **추가 발견 R11**. 셸 아이콘 캐시의 성공/실패와 동일시하지 않음 |
| 현재 폴더 자체 소실 | 열린 sample을 sample-moved로 외부 이동하면 옛 경로/목록 유지. 이동한 폴더 안 파일을 수정해 감시 이벤트를 발생시키자 부모로 복구 | 상위 경로 조회/복구는 동작. 이동 자체의 감시 계기 누락은 **R12** |

R11의 열림 캐시 조건과 R12의 현재 폴더 전용 감시 구조는 변경 전 `bdbbd6b` 소스에도 있다.
이는 기존 구조가 남아 있다는 코드 근거이며, 이전 실행본에서 같은 현상을 재현했다는 뜻은 아니다.
두 발견은 P2 후속 항목으로 등록했고 이번 검증 요청에서 제품 코드를 변경하지 않았다.

이 시험은 일반 UI 경로의 실제 동작 확인이다. 모든 기존 기능·파일 형식·장애 경로가 UI로 검증됐거나,
입력 지연/프레임 시간·취소/해제의 모든 경합이 입증됐다는 뜻은 아니다.
