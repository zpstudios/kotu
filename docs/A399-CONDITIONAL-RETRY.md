# A399-c: 정상 완료 miss의 조건부 회복 (v0.395.0)

2026-10-15 KST. 실제 OneDrive 제공자 문제를 확정한 배치가 아니다. 사용자가 현재 재현 대상을
기억하지 못하므로, 사용자 파일이나 hydration 상태를 변경하지 않고 현재 정책의 누락 경로를
합성 제공자로 입증한 뒤 정상 완료 miss만 제한적으로 다시 조회한다.

## 원인 근거와 범위

`artifacts/a399-retry/observations.json`은 변경 전 제품의 `MarkPreviewMiss`를 추출하고 실제
Core의 Entry 재사용/취소/시간 제한 코드를 연결한 기준선이다. null, 빈 결과, Icon, 일반 예외
각 경우 첫 요청1회 후 제공자가 준비돼도 동일 상태 스캔100회에서는 같은 VM이 유지되어
회복0회였다. 명시 Refresh 대조군은 새 요청1회로 성공했다. 이는 실제 WinUI/OneDrive 실측이
아닌 정책 프로브다. 별도 지연 작업5회는 timeout5회 뒤 실제 작업5개가 미완료로 남았고,
나중에 풀어주면 늦은 결과5개가 폐기됐다. timeout은 실제 네이티브 완료의 증거가 아니다.

기준선 파일은 덮어쓰지 않는다. `eng/Test-A399ThumbnailInvestigation.ps1`도 구 정책 조사
도구로 보존하며, 새 소스에서 추출 모양이 달라지면 실패하는 것이 의도다.

## 결과와 재시도 계약

| 결과 | 판정 | 자동 예약 |
| --- | --- | --- |
| Available | 워커 디코드 성공 | 해당 후보 종료 |
| CacheMiss | 정상 반환 null 또는0바이트 | 허용 |
| IconFallback | 정상 반환 ThumbnailType.Icon | 허용 |
| InvalidOrOversized | 제한16MiB 초과·잘못된 크기·디코드 실패 | 종료 |
| AccessDeniedOrMissing | 접근 거부·파일/폴더 없음 | 종료 |
| OtherFailure | 그 외 예외 | 종료; transient로 추정하지 않음 |
| Timeout | 제한시간 초과 | 해당 경로 자동 조회를 프로세스 수명 동안 차단 |
| Cancelled | 실제 발사 이후 취소 | 해당 경로 자동 조회를 프로세스 수명 동안 차단 |

오디오 정보 조회 실패 후 최종 CacheMiss/Icon이어도 자동 예약하지 않고 해당 경로를 차단한다.
예전처럼 오디오 실패를 삼킨 뒤 썸네일만 보고 재시도 가능으로 오인하지 않는다. 정상 취득한
오디오 정보와 성공 픽셀은 기존 안전 캐시를 유지한다. `DiagTrace` 기본off를 유지하며 켜진
경우 결과 분류·자동 여부·오디오 실패 여부를 기록하여 집계할 수 있다.

첫 정상 miss 완료 후2초, 첫 자동 완료 후 다음 요청까지8초, 자동 최대2회다. 프로세스 전역
`ThumbnailRetryRegistry`가 Windows 경로 대소문자 무시 규칙으로 예산·활성 요청·다음 허용
시각을 공유한다. 두 표면이 같은 시각에 예약해도8초 간격을 우회하지 않는다. 한 표면의 성공은
그 후보만 종료한다. 픽셀을 공유하지 않는 다른 표면은 남은 예산으로 회복할 수 있다.

경로4096개까지 등록하고 퇴출·TTL·환불은 없다. dirty, Refresh, 폴더 전환, S4, Unload, VM
교체도 예산이나 차단 상태를 초기화하지 않는다. 용량을 넘은 새 경로는 자동 시도0회지만 기존
일반 첫 요청/명시 Refresh는 유지한다. 오래 실행한 앱에서 새 경로의 자동 회복이 제한되는 것은
예산 부활을 막기 위한 의도적인 보수 정책이다.

자동 시도는 항상 `ReturnOnlyIfCached`이며 오디오 속성/텍스트/원본 읽기를 하지 않는다.
첫 요청은 A399-b의 열거 placeholder·현재 placeholder·속성 조회 실패 보호를 그대로 사용한다.
캐시 옵션은 Microsoft 문서상 캐시 또는 embedded 결과를 뜻하며, 모든 제공자에서 hydration이
절대 발생하지 않는다는 보장은 아니다. 이번 배치가 추가한 원문 접근은 없다.

## 수명과 최대 함정

활성 lease를 워커에서 실제 제공자 호출 직전에 등록한다. 따라서 게이트/워커 큐 진입 전 취소는
자동 예산을 쓰거나 경로를 차단하지 않는다. 발사 후 취소·timeout·오디오 정보 실패는 프로세스
수명의 차단으로 남긴다. 다음 VM의 정상 결과도 이를 해제하지 않는다. 이전 네이티브가 남은
상태에서 새 자동 시도가 겹치는 것을 보수적으로 피하지만, 기존 일반/수동 요청의 네이티브
잔류를 전면 해결한 것은 아니다. 실제 WinRT 작업 영구 잔류를 단위시험으로 입증하지 않는다.

lease/위험 latch는 중앙 `ThumbnailExplorer`의 일반·자동 요청들만 추적한다. 중앙 일반 요청이
이미 진행 중일 때 자동 요청은 겹치지 않지만, 자동 요청 중 새 일반 요청을 전역 차단하지는
않는다. 좌 `ExplorerPane`의 독립 상세/아이콘 조회도 기존 그대로다. 따라서 앱의 모든 셸
요청에 대한 경로별 절대 동시수나 네이티브 잔류 상한을 보장한다고 해석하면 안 된다.

표면마다 worker Timer1개와 병합된 UI due callback1개를 쓴다. 큐 상태는 표면 수명별 객체여서
옛 Timer 콜백이 Reload 이후 새 큐 플래그를 막거나 지울 수 없다. 후보는128개, 실제 자동 요청은
최대3개이며 기존 썸네일 게이트도 공유한다. 비가시 후보가128개를 차지하면 새로 보이는 후보가
그중 하나를 밀어낼 수 있다. 밀려난 VM의 due/경로 예산은 남아 재실체화 때 돌아온다.

UI due callback은 Low 우선순위다(DocumentView의 ApplyPendingZoom 선례). 실제 WinUI
편집 시험에서 기본 우선순위는 편집 종료 직후의 중간 캡션 높이를 보고308×281 결과를
308×260으로 다시 요청했다. Low로 최종 배치 뒤 반영하여 단순 편집 종료의 추가 요청을 없앴다.
보관한 픽셀은 원 요청 Target을 Tag로 기록하며 실제 크기가 달라지면 기존 resize 조회로 넘긴다.

발사/적용은 Loaded·Visible·실제 ScrollContentPresenter와 host의 교차·현재 VM/seq/token·
편집 중 아님을 대조한다. 컨테이너 존재만으로 가시성을 판단하지 않는다. 화면 밖이나 편집 중
대기는 멈추고 상태 변화 때 재개한다. UI callback은 상태 확인과 픽셀 복사만 하며 파일/셸 I/O는
worker에서 한다. viewport는 표면 수명 동안 캐시한다.

완료 결과의 예산/다음 due 처리는 UI 적용 가능 여부와 분리된다. 화면 밖/편집 중 도착한 성공
픽셀은 최대3개(최대 약7MiB) 보관하여 재등장/편집 종료 때 추가 요청 없이 적용한다. 보관 상한을
넘긴 성공은 `PreviewKnownEmpty=false`로 남겨 기존 재실체화 요청으로 복구한다. 이는 성공한
이미지를 화면 재활용 때 다시 읽던 기존 동작이며 자동 실패 예산을 환불하거나 즉시 반복하지
않는다. 폴더/항목 교체·해제된 VM의 늦은 결과는 표시/보관하지 않는다.

## 검증과 실행

Core `ThumbnailRetryTests`는 제품의 registry/candidate/access-plan/수명 큐를 직접 사용한다.
가짜 시계로2초/8초, 무이벤트 회복, 지속 miss3회, Icon, 종료 결과, 오디오 실패,100세대 공유
예산,4096 상한 규칙의 작은 용량 대조, 두 표면 활성 lease/cooldown/성공 분리, offscreen/편집,
늦은 취소와 Reload 큐 격리를 확인한다. 실제 `CloudFileAccessTests`는 기존 보호 시험이다.

실제 WinUI 통합 시험은 명시 빌드 속성에서만 컴파일하는 대체 StartupObject다. 기본 빌드에는
`HarnessProvider`, `ThumbnailRetryHarness`, 조건부 partial 시험 메서드가 존재하지 않는다.
App/Program의 제품 실행 경로를 바꾸지 않고 별도 Application으로 실제 ThumbnailExplorer를
올린다. AppSettings/Velopack/업데이트/인스턴스 리디렉션을 실행하지 않고 사용자 파일을 만들거나
읽지 않는다. 가짜 경로·제공자임을 JSON의 `FakeProvider=true`, `RealWinRtProviderTested=false`로
명시한다. 필요한 XamlControlsResources와 BottomBarButtonStyle만 별도 준비한다.
생성된 XAML metadata provider에는 별도로 위임하되 App 인스턴스는 만들지 않는다. 자원 사전은
OnLaunched에서 초기화한다. 첫 실행의 ctor Resources 조회는0x8000FFFF로 실패했으며, 이는
시험 실행기 초기화 실패여서 제품 회복 통과 수치에 포함하지 않는다. metadata 부재는 별도
예방 보완이며 그 실패의 확정 원인으로 치환하지 않는다.

```powershell
$env:DOTNET_CLI_HOME = "$PWD/artifacts/dotnet-home"
$env:NUGET_PACKAGES = "$PWD/artifacts/nuget"
$env:DOTNET_ROOT = "$PWD/artifacts/dotnet"
& artifacts/dotnet/dotnet.exe test tests/KOTU.Core.Tests/KOTU.Core.Tests.csproj -m:1 -p:TreatWarningsAsErrors=true --filter FullyQualifiedName~ThumbnailRetryTests
& artifacts/dotnet/dotnet.exe publish src/KOTU.App/KOTU.App.csproj -c Release -r win-x64 -m:1 -p:Platform=x64 -p:KotuThumbnailHarness=true -p:TreatWarningsAsErrors=true -o artifacts/a399-retry/current/harness
# 별도 시험 창은 사용자 요청에 따라 표시한다. 단독 실행 후 JSON 반환까지 기다린다.
& artifacts/a399-retry/current/harness/KOTU.exe "$PWD/artifacts/a399-retry/current/ui-results.json"
```

| 통과 항목 | 근거 | 상태 |
| --- | --- | --- |
| 기존 고정 miss의 정책 증명 | 기준선 observations.json, 메인 독립 재실행 | 완료 |
| Core 가짜 시계/제공자 계약 | ThumbnailRetryTests, current/main-focused.log, focused/*.trx | 메인 독립24/24 통과·skip0 |
| 실제 WinUI 무이벤트 회복/편집/viewport/늦은 완료/Reload | current/ui-results-6.json | 메인 실제 데스크톱 최종 실행 통과 |
| 실제 열 수 변경의 재조회 보존 | current/ui-results-6.json의 real column resize | 일반 추가1회·자동 추가0회·현재 Target 표시 통과 |
| 전체 솔루션/자동 시험 | current/main/build.log, test-summary.json | Release/x64 0경고·0오류,645/645·skip0 |
| 구조/릴리스 게이트 | current/main/architecture.log, release-gates.log | 프로젝트20개·우회 회귀7개 통과 |
| 기본 산출물 시험 seam 부재 | current/main/production-metadata.json | Program.Main·시험 형식/메서드/필드0 확인 |
| 실행본 패키지 | current/main/publish.log, package-local.log | self-contained x64 publish·리소스/엔진 검사 통과 |
| 실제 OneDrive 제공자·hydration 상태 | 현재 재현 파일 없음 | 미검증 |
| 기존 일반 요청의 실제 네이티브 잔류 해소 | 범위 밖 | 주장하지 않음 |

초기 Core 시험 프로젝트 빌드는0경고·0오류(17.02초)였다. 제한 실행의 testhost는90초 연결
실패로 중단되어 성공으로 세지 않는다(`current/core-test.log`, `retry.trx`). 그 뒤 추가한
다중 표면 cooldown/성공 분리 시험까지 메인의 허용된 집중 검증에서24/24 통과했다. 첫 harness publish는
명령의 Platform 누락으로 ScreenRecorderLib AnyCPU 오류를 냈고, x64를 명시한 후 publish가
exit0으로 완료됐다(`current/harness-build-x64.log`, warning/error 없음). 기본 제품 산출물의
시작점/시험 seam 부재는 별도 기본 publish에서 검증한다.

최종6차 실제 WinUI 결과는 `FakeProvider=true`, `Passed=true`다. recover/Icon은 총2회(자동1),
지속 miss는 총3회(자동2), timeout/오디오 실패/잘못된 결과는 각각 총1회(자동0)였다. 편집 중
성공 보관 후 종료 시 이미지가 표시됐고 총2회로 추가 요청0회였다. 실제 viewport 밖에서는
자동0회, 다시 들어온 뒤 자동1회였다. 늦은 폴더 결과 격리와5회 Reload·VM 교체 뒤 자동2회
상한도 통과했다. 자동 오디오 요청은0회다. 메인이 합성 이미지와 확장자 폴백을 실제 창에서도
관찰했다. 이 수치는 WinRT/OneDrive 제공자 자체의 성공률이 아니다.

실제3열→2열 변경 대조에서는308×281에서471×444로 요청 크기가 바뀌었고, 일반 추가1회·
자동 추가0회로 현재 Target의 이미지가 표시됐다. 편집 종료의 불필요한 resize만 억제하고 실제
크기 변경 재조회는 유지한 대조다. 최종 harness DLL SHA-256은
`E8051DAA2CED5826A3F08806C32503266F71C6044523F809739FFC836A0FDA1D`이며
`harness-build-final.log`의 publish는 exit0, warning/error 없음이다. 기본 제품의 전체
빌드/시험/정상 publish와 시험 seam 부재도 이후 메인이 직렬 검증했다.

메인의 첫 일반 빌드에서 `result =`가 시험용 전처리 조건 안에 있던 CS0165를 발견했다.
실패 로그는 `current/main/build-first-failure.log`로 보존했다. 대입만 조건 밖으로 옮겨
일반 경로에서도 반환값을 저장하게 수정했다. 시험용 전처리 결과의 조건식·호출 동작은 같으며,
수정 뒤 일반 빌드0경고·0오류, 전체8개 TRX 합계645/645·skip0, 구조20·릴리스 게이트7이 통과했다.

정상 self-contained x64 publish도 통과했다. 기본 DLL은 `KOTU.App.Program.Main`으로 시작하며
harness 형식·메서드·필드가 모두0이다. SHA-256은
`23AFABD1B1462EFA9ECEEDB8485C22AB04A857E040A376A972BE4916AAB75BA8`이다.
표준 패키지 스크립트는 이 PC에 `C:/Program Files/7-Zip/7z.dll`이 없어 중단됐다. 저장소의 기존
`artifacts/native/7zip/7z.dll`을 원본으로 바꾼 로컬 사본에서 동일 패키지 검사를 통과했다.
이 DLL26.03의 해시65E4C1F855F9EF6E8F0F5DF8E3F27D9EB5F07311408639DA0A1CA0B8F4871B0D는
설치본의 DLL과 같았다. 제품 패키지 스크립트를 변경하거나 시스템 폴더에 쓰지 않았다.

최종 소스 해시는 `current/final-source-hashes.json`, 사용자 상태는 `current/user-state-after.json`이다.
설정 해시가 기준과 같고 데스크톱 목록에서 원래 설치본 ZIP 창1개·시험 창0개를 확인했다.
제한 실행의 프로세스 목록만으로 실제 데스크톱 앱 부재를 추정하지 않았다. 설치/시작/Standalone과
원격 정식 발행은 아래와 같이 확인했다.

## 원격 배포 검증

제품 커밋 `7cdbb0db7269eb4d8f8f457ccdeb5cb4e62f8acc`를 master에 전송했다.
[build38714341045](https://github.com/zpstudios/kotu/actions/runs/38714341045)와
[release38714341047](https://github.com/zpstudios/kotu/actions/runs/38714341047) 모두 성공했다.
두 작업 각각 전체645/645·0경고·0오류·Explorer COM3동사가 통과했다. 릴리스의 실행본 시작·
설치 및 설치본 정합성·Standalone 회귀/실제 시작·설정/레지스트리 보존·종료 정리 검사가 성공했다.

2026-10-15 **18:46:01 KST** [v0.395.0 정식 게시](https://github.com/zpstudios/kotu/releases/tag/v0.395.0).
태그/원격 master/제품 커밋이 일치하며 Latest·draft=false·prerelease=false를 확인했다.
자산8개(델타967,601bytes 포함) 모두 존재한다. 로컬 증거는 `current/remote-build-summary.json`과
`current/remote-release-summary.json`이다. 최종 확인 문서는 로컬 커밋으로 보존하고 추가 push는 보류한다.

## API 근거

- [GetThumbnailAsync](https://learn.microsoft.com/en-us/uwp/api/windows.storage.istorageitemproperties.getthumbnailasync): null은 현재 결과 없음이며 영구 미제공 판정이 아니다.
- [ThumbnailType](https://learn.microsoft.com/en-us/uwp/api/windows.storage.fileproperties.thumbnailtype): Icon은 반환 유형이다.
- [ThumbnailOptions](https://learn.microsoft.com/en-us/uwp/api/windows.storage.fileproperties.thumbnailoptions): ReturnOnlyIfCached는 캐시 또는 embedded 조회다.
- [IAsyncInfo.Cancel](https://learn.microsoft.com/en-us/windows/win32/api/asyncinfo/nf-asyncinfo-iasyncinfo-cancel): 취소 요청과 실제 완료는 구분된다.
- [TransformToVisual](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.uielement.transformtovisual): 실제 좌표 변환. 저장소 선례는 PdfPane의 viewport 교차 계산이다.
- [Microsoft 자원 사전 문서](https://github.com/MicrosoftDocs/windows-dev-docs/blob/docs/hub/apps/develop/platform/xaml/xaml-resource-dictionary.md): 코드로 App 자원을 추가할 때 ctor 대신 OnLaunched를 사용하는 근거다.

Timer의 저장소 선례는 `KOTU.Module.Document/TextPerformanceCapture.cs`다. .NET 기본 타이머의
Dispose만으로 이미 큐에 들어간 콜백을 없앤다고 가정하지 않는다.
세대별 큐 객체가 그 잔여 콜백을 차단한다. API 복구가 필요하면 자동 예약을 끄는 것이 최소
롤백이며 기존 첫 요청/확장자 타일/수동 Refresh는 유지된다.
