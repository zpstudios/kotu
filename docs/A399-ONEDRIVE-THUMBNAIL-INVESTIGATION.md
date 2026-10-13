# A399 OneDrive 썸네일 누락·갱신 조사

2026-10-14. 조사 기준 `29b5638`, 제품 v0.391.0. 제품 소스·버전은 변경하지 않았다.
사용자 보고는 OneDrive 연동 폴더의 썸네일 누락·갱신 불량이며, 파일 유형과 다운로드 상태는
기억하지 못한다고 답했다. 아래 원인 후보를 그 사용자의 실제 재현 조건으로 치환하지 않는다.

## 결론과 증거 수준

가장 작은 확인 가능한 결함은 **파일 속성만 바뀌는 상태 전환을 현재 폴더 감시가 놓친다**는 점이다.
또한 일시적인 썸네일 없음/일반 실패도 현재 VM의 수명 전체에 걸쳐 재시도를 차단한다.
이 둘은 다운로드 완료 뒤 자동 갱신이 안 되는 경로를 설명할 수 있지만, 제보의 원인 확정은 아니다.
**명시적 Refresh는 이미 새 VM을 만들어 실패 캐시를 초기화한다.** 따라서 Refresh를 실제로
실행한 뒤에도 계속 없는 현상을 실패 캐시 하나만으로 설명할 수 없다.

| 구분 | 이번에 확보한 증거 | 입증하지 않은 것 |
| --- | --- | --- |
| 실제 로컬 OS 실증 | 합성 임시 파일의 Hidden 속성 변경 3회. 현 필터는 매회 통지 0, Attributes 추가 필터는 매회 1. 길이/수정시각 불변. 내용 변경 대조군은 양쪽 1, 오류 0 | OneDrive 다운로드가 반드시 이 이벤트 조합으로 끝난다는 주장 |
| 실제 제품 메서드 추출 실행 | `MarkPreviewMiss`를 소스에서 추출·컴파일. 일반 miss 즉시 고정, 첫 timeout 유예, 두 번째 timeout 고정, 새 VM 초기값 false | WinUI 컨테이너나 셸 제공자 실행 |
| 기존 관련 회귀 | Core Release/x64 빌드 0경고·0오류. ExplorerListing/BoundedOperation/CancellableBatch 시험 32/32 통과 | 새 구현 검증, 실제 UI 응답성·OneDrive 다운로드 방지 |
| 정적 경로 감사 | 아래 줄번호의 요청·감시·캐시·세대 경로 | 사용자 파일/설치본/원격 제공자 실기기 재현 |

사용자의 OneDrive 파일을 열거나 내려받거나 핀·동기화 상태를 바꾸지 않았다. 새 GUI 자동화 및
실제 앱 실행은 하지 않았다. 이번 로컬 실증은 WinRT/COM 썸네일 요청을 실행하지 않았다.
이전 A385의 WIC 축소 시험 및 A386의 워커 시험은 참고 근거이며 이번 실행으로 재계상하지 않는다.

## 상태별 경로

`ExplorerListing.Entry`에는 동기화 중/완료 상태가 따로 없다. 열거 시점의
`IsPlaceholder` bool만 저장한다(`ExplorerListing.cs:21–23, 59–69, 197–205`).
Offline/RecallOnDataAccess/RecallOnOpen 중 하나면 true다. 따라서 아래 동기화 중 행은
관측된 별도 앱 상태가 아니라, 당시 이 bool과 셸 응답이 어떻게 나오는지에 따른 분기다.
폴더 항목 자체는 미리보기 콜라주를 요청하지 않고 폴더 글리프를 그린다
(`ThumbnailExplorer.xaml.cs:739–742`). 여기서 조사한 대상은 폴더 안 파일 타일이다.

| 파일 상태 | 최초 진입 | 다른 폴더를 거쳐 재진입 / 명시 Refresh | 같은 폴더에서 다운로드·제공 완료 | 증거 |
| --- | --- | --- | --- | --- |
| 로컬 보유 | 텍스트는 앞부분 읽기. 나머지는 일반 셸 썸네일 요청; 오디오는 속성 조회도 수행 | 재열거→새 VM→다시 요청. 성공 비트맵은 앱 VM에 캐시하지 않음 | Size/LastWrite/이름 이벤트가 오면 400ms 디바운스 후 전체 갱신. 파일 통지 없이 셸 캐시만 갱신되면 표시 중인 타일은 그대로 | 정적 |
| 온라인 전용 | 텍스트 직접 읽기·오디오 속성 조회 제외. 모든 파일은 셸 `ReturnOnlyIfCached`; 없거나 Icon이면 확장자 타일 | 새 속성 스냅샷과 새 VM. 여전히 온라인 전용이면 cached-only 재조회; 캐시 없는 제공자는 계속 폴백 | 속성만 바뀌면 현재 감시 필터로는 갱신 안 됨. VM의 placeholder와 miss가 유지될 수 있음 | 경로는 정적; 속성 통지 누락만 합성 OS 실증 |
| 동기화 중 | bool true이면 cached-only, false이면 일반 요청. null/예외/timeout 가능성은 제공자 의존 | 새 스냅샷·재요청. 동기화가 진행 중이면 다시 실패할 수 있음 | 감지된 파일 이벤트는 재스캔. Attributes만 또는 셸 캐시만 바뀌면 보장 없음 | 정적 추정; 실제 제공자 미측정 |

재진입의 의미를 구분해야 한다. `FileListOverlay.Show`는 같은 폴더·필터면 재열거하지 않는다
(`Overlays/FileListOverlay.xaml.cs:160–175`). S4 재표시의 `ShowEntries` 시드도 마지막
목록을 사용할 수 있다(`MainWindow.xaml.cs:4026–4034`). **패널 숨김/재표시**가 곧 최신 파일
속성 재조회라는 뜻은 아니다. 위 표의 재진입은 실제 NavigateList로 재항해한 경우다.

명시 Refresh 연결은 다음과 같다. 중앙 `RefreshViaShell`(:983–985) →
`MainWindow.xaml.cs:2082–2085`(S1) 또는 :4087(S4) →
`FileListOverlay.NavigateList:234–266` → `ExplorerPane.NavigateToAsync` 재열거
(:739–740, 767–781) → `RefreshView`(:493–509) → 셸 중계(:206–219) →
`ThumbnailExplorer.ShowEntries`(:623–637). 같은 폴더라도 새 VM을 만들고 배치를 취소한다.
정렬/필터 변경도 RefreshView→새 VM은 수행하지만, 기존 Entry를 재사용하면 파일 상태 재열거는 아니다.

## 셸 요청·원본 보호 감사

- 실제 중앙 경로는 `IShellItemImageFactory`/SIIGBF 직접 호출이 아니라 WinRT
  `StorageFile.GetFileFromPathAsync` → `GetThumbnailAsync(SingleItem, 768, …)`다
  (`ThumbnailExplorer.xaml.cs:1530–1564`). placeholder만 `ReturnOnlyIfCached`를 지정한다.
  `ThumbPool.Run`(:1417–1418) 안에서 취득·속성·썸네일·WIC 변환을 실행한다.
  UI는 완성 BGRA 복사와 비트맵 배치만 한다(:1453–1465). UI에 COM/원본 파일 열기를 옮길 이유가 없다.
- A385의 768 요청, 16MiB 압축 입력 제한, 8192 입력 변 제한, 최대 768² BGRA 및 Fant 축소는
  `ThumbnailRaster.cs:8–10, 29–52`에 있다. 원본 파일을 이 디코더에서 열지 않는다.
  셸이 제공하지 않은 이미지를 직접 디코드하는 폴백을 추가하면 A175 보호를 깨뜨릴 수 있다.
- 온라인 전용 텍스트/오디오 직접 조회 차단은 :1172–1186에 있지만 **열거 당시 Entry**를 본다.
  `FetchTilePreview`는 받은 cachedOnly를 그대로 쓰고, 실제 요청 직전에 속성을 확인하지 않는다.
  텍스트 `ReadTextPreview`도 :1219 → :1265 `SharedRead.Open` 앞에 최신 상태 재검사가 없다.
- 구체적인 보호 공백: (1) 열거 때 로컬이라 false 저장 → (2) 화면 밖/게이트 대기 중 외부에서
  온라인 전용으로 변경 → (3) Attributes만 변경되어 현재 watcher가 놓침 → (4) 재실체화/워커
  실행 시 오래된 false로 일반 셸 요청 또는 텍스트 파일 열기. **이 interleaving이 코드상 허용되는
  것은 확정**이고, 실제 이 PC에서 자동 다운로드가 발생했다는 관측은 없다.
  역방향(true→다운로드 완료)은 계속 cached-only/텍스트 제외 상태로 남아 누락을 유지할 수 있다.
- 최소 보강은 워커에서 현재 속성을 확인하고, 기존 true 또는 현재 placeholder/상태 조회 실패이면
  보수적으로 cached-only/직접 읽기 생략을 선택하는 것이다. 새 스캔에서 로컬화가 확인된 뒤 일반
  경로로 승격한다. 속성 조회와 파일 열기 사이의 경쟁까지 원자적으로 제거하는 보장은 아니다.
  특히 RecallOnOpen은 공식 문서에서 디렉터리 열거의 비트로 설명하므로 최신 `GetAttributes`
  결과 하나만으로 기존 true를 false로 강등해서는 안 된다.

Microsoft의 [ThumbnailOptions 문서](https://learn.microsoft.com/en-us/uwp/api/windows.storage.fileproperties.thumbnailoptions?view=winrt-26100)는
ReturnOnlyIfCached를 **캐시 또는 파일 내부 내장 썸네일**의 반환으로 설명한다. 따라서 기존
주석의 “캐시·클라우드 제공 썸네일만, 하이드레이션 금지”는 앱의 정책 의도이며 이 옵션만으로
모든 제공자의 원본 접근이 없다고 증명한 결과가 아니다. 새 옵션 제거/일반 요청 재시도로 빈
썸네일을 강제로 메우지 않는다. 실제 다운로드 방지를 입증하려면 별도 관리된 테스트 동기화
환경에서 제공자/파일 상태/네트워크·hydration 관측이 필요하다.
[파일 속성 공식 계약](https://learn.microsoft.com/en-us/windows/win32/fileio/file-attribute-constants)은
RecallOnDataAccess가 부분 로컬 파일도 포함하고 접근 비용이 원격 콘텐츠 취득을 포함할 수
있음을 설명한다. 동기화 아이콘만으로 bool을 대체하지 않는다.

좌 ExplorerPane의 96px `FetchThumbnail`(:1599–1610)은 별도 경로지만 실제 셸은
`ConfigureListOnly`로 연결된다(`FileListOverlay:239–240`). 이를 중앙 제보의 주원인으로
잘못 지목하지 않는다. 해당 휴면 경로에는 중앙과 같은 늦은 썸네일 Dispose 콜백/압축 크기 제한이
없지만 이번 최소 구현의 중심은 아니다. 필요하면 보조 정리로 별도 범위를 잡는다.

## 캐시·세대·가상화 수명

| 항목 | 현재 계약과 줄번호 | 판정 |
| --- | --- | --- |
| 일반 실패/없음/Icon | `FillShellThumbnailAsync:1422–1438`, `MarkPreviewMiss:1499–1506`; VM `PreviewKnownEmpty`에 고정 | 순간 cache miss와 영구 미제공을 구분하지 못함. 같은 VM에서 TTL·예약 재시도 없음 |
| Timeout | 첫 번째는 `PreviewTimedOut=true`, 두 번째부터 KnownEmpty | 첫 timeout 뒤에도 자동 타이머는 없음. 재실체화/크기 변경 등 다른 진입이 있어야 재요청. 성공 때 표지를 지우지 않아 두 timeout은 연속일 필요가 없음 |
| 성공 이미지 | VM 캐시 없음. 현재 host에만 Image, `host.Tag`는 크기 | 스크롤 재실체화는 다시 셸 요청. 표시 중인 성공 이미지는 통지가 없으면 그대로 |
| 텍스트/오디오 정보 | VM PreviewText/AudioInfo (`ExplorerEntryVm.cs:103–119`) | VM 수명 동안 유지. 새 목록이면 초기화 |
| 재스캔/Refresh | `ShowEntries:623–637`, 새 VM·seq 증가·CancellableBatch.Restart | 실패와 성공 UI를 함께 무효화. 실패 캐시만 지우는 추가 코드 불필요 |
| 재활용 | 위상0 host clear(:735–736), KnownEmpty 검사(:752–757), `LivePreviewHostOf:783–788` | 현재 vm의 컨테이너를 재조회하고 ReferenceEquals 비교. 다른 파일 타일 덮기 정적 경로는 발견하지 못함 |
| 폴더/세대 경쟁 | :1398–1419, 1432 이후 캐시 기록, :1450 반영 전 재검사 | 옛 셸 결과가 새 VM의 캐시/화면을 덮는 경로는 발견하지 못함 |
| 크기 변경 | `QueuePreviewResize:808–822`, :1451, 1482–1484 | 현재 목표 크기 다르면 폐기 후 한 틱 예약, InFlight로 중복 억제. KnownEmpty이면 크기 변경도 재요청 안 함 |
| 해제·재표시 | :338–354, :334 | 세대/취소 후 풀 해제. 새 VM 없이 재표시하면 이전 VM의 miss가 자동 초기화되는 계약은 없음 |

셸 각 비동기 호출은 `ShellFetch.cs:26–37`의 5초 시한과 cancellation을 사용한다.
이는 **타일 전체 5초 상한이 아니다**. StorageFile·오디오 속성·썸네일을 순차로 기다릴 수 있고,
반환 스트림 `CopyTo` 및 WIC 내부 작업은 같은 5초 시한으로 감싸지지 않는다. 셸 호출 진입
자체가 동기로 막히는 상황 역시 이 대기 타이머의 입증 범위 밖이다. 네이티브 Cancel 성공은
보장하지 않으며 늦은 썸네일은 중앙 :1554의 discard로 Dispose된다.
`BoundedOperation`의 관련 소유권/늦은 완료 단위 시험은 통과했다. 실제 제공자 지연은 미측정이다.

## 감시·이벤트 병합 감사

`ExplorerPane.xaml.cs:2492–2494`는 FileName/DirectoryName/LastWrite/Size와 현재 폴더 한
단계만 감시한다. Attributes가 없다. 이 차이는 [FileSystemWatcher 공식 설명](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher?view=net-10.0)과
이번 로컬 OS 실증 모두에서 확인했다. 생성/삭제/이름변경/수정은 동일 핸들러(:2496–2500),
오류는 감시 재생성+전체 갱신(:2588–2594)으로 간다.

- 이벤트마다 UI 큐에는 콜백 하나가 올라가고(:2579–2580), 마지막 이벤트 기준 400ms 타이머를
  재시작한다(:2596–2613). **전체 이벤트 큐 자체를 하나로 합친 것은 아니다.** 타이머는 요청
  예약만 하고, 실제 경로 확인/열거는 워커에서 수행한다(:2646, :739–740).
- 이름변경 편집 중에는 bool 하나로 보류(:2632–2635). 경로 probe 실행 중에도 보류 bool 하나로
  병합(:2638, :2658–2659). A386 보호는 watch/load/folder/수명 비교(:2641–2649)다.
  `_watchProbeRunning`은 경로 확인을 합칠 뿐 이후 NavigateToAsync의 전체 스캔 완료까지
  감싸는 실행 가드는 아니다. 늦은 스캔 결과는 `_loadSeq`가 폐기한다.
- 매 갱신은 동일 목록이어도 좌/중앙 VM을 전량 교체(:498, 중앙 :630). Attributes를 단순히
  추가하면 추가 이벤트 수만큼 이 경로가 늘어난다. 400ms보다 촘촘한 연속 이벤트는 정숙 시점까지
  지연되고, 400ms보다 성긴 연속 이벤트는 반복 스캔·모든 미리보기 취소를 만들 수 있다.
  느린 썸네일 완료 전에 다음 세대가 계속 시작되면 화면에 성공을 못 올릴 수 있다(정적 후보).
- 일반 OnWatcherEvent에는 오류 핸들러와 달리 sender/세대 대조가 없다. 옛 감시자의 이미
  큐에 오른 이벤트가 현재 폴더의 불필요한 갱신을 예약할 수 있다. 현재 폴더를 재조회하므로
  이 사실이 곧 다른 폴더 결과 덮기라는 뜻은 아니다.
- FileSystemWatcher Changed 이벤트는 어느 NotifyFilters 비트 때문에 왔는지 알려주지 않는다.
  “Attributes 이벤트만 다르게 처리”를 하려면 별도 출처 또는 워커에서 비교한 상태 근거가 필요하다.
  길이/수정시각이 같다는 이유만으로 모든 갱신을 무시하면 셸 캐시 가용성 변화를 놓칠 수 있다.

## 후속 구현 배치와 통과 조건

아래는 구현 권고이며 이번 조사에서 수행하지 않았다. 메인이 A401 개선과의 선행 순서를 정한다.

| 배치 | 최소 범위 | 통과 조건 |
| --- | --- | --- |
| A399-a: 상태 변화 감지 | Attributes 감시 추가, 옛 watcher 이벤트 대조, 기존 400ms/편집 보류 보존. 순수 상태 변화로 새 Entry·VM이 만들어지는 회귀 추가 | 길이/수정시각 그대로 placeholder→로컬/반대 변화가 반영됨. 편집 중 갱신 0, 종료 후 보류 1. 폴더 교체/해제 뒤 옛 이벤트 무시. 없는 썸네일 폴백 유지 |
| A399-b: 요청 직전 보호 | worker-only 현재 속성 확인. 기존 placeholder 또는 현재 placeholder/조회 실패면 원본 텍스트·오디오 속성 차단, cached-only 유지. 상태 판정을 작은 테스트 가능한 함수/주입 경계로 분리 | 열거 false→게이트 대기→현재 true의 합성 interleaving에서 일반 요청/파일 읽기 0회. 조회 실패도 보수 처리. UI 파일 접근 0, 새 스캔 로컬화 뒤 일반 요청 복원 |
| A399-c: 지속 miss 회복 | 앞 배치 후에도 제공 지연이 남는 경우에 한해 transient miss/미제공/실패 분류·유한 재시도 정책 도입 | 가짜 시계/제공자에서 visible 항목만 유한 재시도, 성공 즉시 종료, 영구 미제공 폭주 없음, 폴더·파일 세대 변경 뒤 늦은 결과 폐기 |

A399-a는 감시 필터의 확인된 구멍을 메우는 작은 배치다. 다만 배포 통과 조건에 **연속 이벤트
100회·느린 썸네일·이름변경 편집** 조합의 스캔/요청/적용 횟수 측정을 포함해야 한다. 짧은 burst는
정숙 뒤 한 번으로 모이고, 지속 동기화 중에도 UI 큐·네이티브 요청이 무한히 쌓이지 않아야 한다.
여기서 반복 VM 교체 문제가 확인되면 별도 작은 후속 배치로 동일 항목 재사용/경로별 무효화를
도입한다. 단순 Attributes 한 줄 추가만으로 “동기화 중 성능도 해결”했다고 판정하지 않는다.

재사용 설계에서는 경로뿐 아니라 파일 메타데이터·placeholder·요청 세대를 대조하고, 명시 Refresh는
동일 목록이어도 재조회해야 한다. 셸 캐시만 바뀌는 경우가 있기 때문이다. 파일별 무효화 후에도
옛 in-flight 결과가 새 상태를 덮지 않게 요청 세대를 올려야 한다. 현재의 새 VM 방식이 제공하는
격리를 잃지 않는 것이 최대 함정이다.

A399-c는 TTL 값부터 정하는 배치가 아니다. 현재 “없음”이 null/Icon/예외를 합치므로 먼저 결과를
구분해 기록하고, 유한 예산·시간 간격·가시성·취소 경계를 정의한다. 네이티브 작업이 timeout 뒤
남을 수 있으므로 재시도 예산을 VM 재생성 때마다 무제한 초기화하는 설계도 피한다.
실제 제공자 자체가 썸네일을 주지 않는 파일은 확장자 타일을 유지한다.

## 검증 명령과 산출물

`eng/Test-A399ThumbnailInvestigation.ps1`은 현재 소스의 NotifyFilter/MarkPreviewMiss를
추출한다. VM은 두 bool 속성만 가진 시험 대역이며 실제 WinUI ExplorerEntryVm을 로드하지 않는다.
제품 통합 테스트가 아닌 조사 프로브이며, 소스 형태가 바뀌면 검토를 요구하고 멈춘다.
고유 TEMP 폴더의 합성 파일만 다루고 Cloud 속성 비트는 설정하지 않는다. 임시 파일과 빈 폴더는
시험 종료 시 정리한다. 이벤트 대기 시간은 회당 1초이므로 모든 파일시스템의 절대 무통지 증명은
아니며, 이번 실행은 양성 대조군을 함께 기록했다.

```powershell
pwsh -NoProfile -File eng/Test-A399ThumbnailInvestigation.ps1 |
    Tee-Object -FilePath artifacts/a399-investigation-probe.json
$env:DOTNET_CLI_HOME = Join-Path $PWD 'artifacts/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PWD 'artifacts/nuget'
$env:DOTNET_ROOT = Join-Path $PWD 'artifacts/dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
& artifacts/dotnet/dotnet.exe build tests/KOTU.Core.Tests/KOTU.Core.Tests.csproj --no-restore -c Release -p:Platform=x64 -p:TreatWarningsAsErrors=true
& artifacts/dotnet/dotnet.exe test tests/KOTU.Core.Tests/KOTU.Core.Tests.csproj --no-build --no-restore -c Release -p:Platform=x64 --filter 'FullyQualifiedName~ExplorerListingTests|FullyQualifiedName~BoundedOperationTests|FullyQualifiedName~CancellableBatchTests' --logger 'trx;LogFileName=a399-core.trx' --results-directory TestResults/a399-core
```

결과: `artifacts/a399-investigation-probe.json`, `artifacts/a399-core-build.log`,
`artifacts/a399-core-tests.log`, `TestResults/a399-core/a399-core.trx`.
앞 A401 배치에서 testhost 로컬 소켓 차단을 확인했으므로 이번 test는 자동 승인된 제한 밖
실행을 사용했다. 32개는 기존 Core 계약 회귀이고 신규 UI/OneDrive 재현 성공 건수가 아니다.
제품 전체 빌드·실제 UI·다운로드 상태별 셸 제공 결과는 이번 조사에서 추가 실행하지 않았다.
