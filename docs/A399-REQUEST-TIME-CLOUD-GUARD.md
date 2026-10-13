# A399-b 미리보기 요청 직전 클라우드 원본 보호

2026-10-14. 조사 기준 `29b5638` / v0.391.0에서 독립 구현. 제품 버전 v0.392.0.
이 배치는 OneDrive 썸네일 제보의 완전 해결이 아니라, 열거 후 상태 변경에 대한 보호 보강이다.
선행 근거는 `A399-ONEDRIVE-THUMBNAIL-INVESTIGATION.md`이며 해당 조사 산출물을 보존했다.

## 변경 내용

열거 때 로컬이었던 파일도 미리보기 워커가 실행될 때 온라인 전용일 수 있다. 이제 워커의
실제 요청 시작에서 파일 속성을 다시 확인한다. 열거 당시 placeholder=true였으면 그 보호를
유지한다. 새 속성 조회 실패도 원본 읽기 허가로 해석하지 않는다.

| 변경 파일 | 역할 |
| --- | --- |
| `src/KOTU.Core/Routing/CloudFileAccess.cs` | worker-only 속성 확인 및 직접 읽기 차단. 기존 `ExplorerListing.IsCloudPlaceholder` 재사용 |
| `src/KOTU.App/Controls/ThumbnailExplorer.xaml.cs` | 직접 텍스트 미리보기, 셸 썸네일, 동반 오디오 정보에 요청 직전 보호 적용 |
| `src/KOTU.App/ExplorerPane.xaml.cs` | 활성 좌 리스트 상세 정보 로더 및 휴면 그리드 `FetchThumbnail`에 같은 보호 적용 |
| `tests/KOTU.Core.Tests/CloudFileAccessTests.cs` | 작업 대기 중 상태 변경·속성 실패·취소·원본 callback 차단·새 스캔 복원 시험 |
| `Directory.Build.props` | 제품 버전 0.392.0 |

README는 특정 현재 버전 대신 latest 릴리스 링크를 사용하므로 수정하지 않았다. 공통 인수인계·
요구사항·세션 기록·진행표는 메인 담당이며 이 구현 배치에서 수정하지 않았다. 커밋·푸시·배포는
수행하지 않았다.

## 판정과 호출 경로

`CanReadContent`는 먼저 cancellation을 검사한다. 열거 당시 placeholder이면 false를 반환해
불필요한 재조회 없이 보호를 유지한다. 열거 당시 로컬인 경우에만 `File.GetAttributes`를 읽고
기존 세 비트 판정을 사용한다. 조회 후에도 취소를 검사한다. 속성 조회가 실패하면 false지만,
`OperationCanceledException`은 그대로 전파하고 다른 조회 예외와 동시에 발생한 요청 취소도
폴백으로 바꾸지 않는다.

`ReadLocal<T>`의 파일 읽기 callback은 이 검사 바깥에서 호출된다. 따라서 속성 조회 실패만
폴백으로 바뀌며, 실제 텍스트 읽기의 I/O 오류나 취소는 기존 호출자 처리로 전달된다.

| 경로 | 적용 위치와 결과 |
| --- | --- |
| 중앙 직접 텍스트 | `FillTextPreviewAsync` → 게이트 → `TextPool.Run` 안 `ReadLocal` → 허용 때만 `ReadTextPreview` → `SharedRead.Open` |
| 중앙 셸 썸네일 | 게이트 → `ThumbPool.Run` → `FetchTilePreview` 첫 부분의 `CanReadContent` → 제한 상태면 `cachedOnly=true` → 기존 `ReturnOnlyIfCached` 요청 |
| 중앙 오디오 속성 | 같은 `FetchTilePreview` 판정으로 `wantAudioInfo &= !cachedOnly`; 제한 상태에서는 `FetchAudioInfo` 호출을 생략 |
| 활성 좌 리스트 상세 정보 | `RequestDetail` → 게이트 → `FetchPool.Run` 안 `ReadLocal` → 허용 때만 `FetchDetailInfo`. 영상/오디오 길이·이미지 해상도·PDF 페이지·ZIP 압축률·텍스트 인코딩 모두 포함 |
| 좌 휴면 그리드 썸네일 | 기존 `FetchPool.Run` → `FetchThumbnail` 첫 부분에서 동일 판정 → 기존 96px 요청의 옵션 결정 |

`ReadTextPreview`/`FetchTilePreview`/`FetchAudioInfo`/`FetchThumbnail` 호출자를 전수 검색해
자동 미리보기의 위 연결을 확인했다. 휴면 그리드는 기존 취소 인자가 없는 구조를 보존했으며
helper에는 default token을 전달한다. 해당 경로의 오래된 결과는 기존 `_loadSeq`가 폐기한다.
활성 좌 리스트 상세 정보 로더는 초기 검수 뒤 같은 보호 범위에 포함했다. 이 경로에서 제한된
결과는 `DetailInfo.Empty`가 아닌 null이다. `_infoCache` 대입 전에 return하므로 path+Modified가
같은 다운로드 완료 후 새 VM에서도 정상 정보를 다시 조회할 수 있다. 기존 캐시 히트는 원본
접근 없이 메모리 값을 사용하는 경로이므로 추가 파일 조회를 넣지 않았다. `DetailRequested`의
현재 VM 중복 방지와 게이트 finally/세대 비교는 보존했다.

열거 당시 true를 최신 `GetAttributes`만으로 false로 낮추지 않는 이유는 `RecallOnOpen`이
디렉터리 열거의 속성 비트일 수 있기 때문이다. 새 목록에서 로컬로 확인된 false이고 요청 시점도
로컬이면 정상 경로로 복원된다. helper는 상태를 전역 또는 파일별 캐시에 보관하지 않는다.

## 보존한 계약과 한계

- 속성 조회는 기존 워커 내부에만 추가했다. UI의 새 파일 접근·COM 호출·타이머·폴링은 없다.
  UI의 배치 취소/세대 비교/컨테이너 재조회 및 미리보기 크기 대조는 그대로다.
- 기존 768px 셸 요청·A385 Fant 축소, 원본 직접 디코드 금지, `ShellFetch` 5초 대기 및 중앙
  늦은 썸네일 Dispose 콜백을 보존했다. 새 패키지나 네이티브 API는 없다.
- `FileSystemWatcher`, `PreviewKnownEmpty`, TTL, 재시도 예산과 클라우드 배지 갱신은 변경하지
  않았다. 직접 텍스트 읽기가 차단되면 null로 기존 빈 미리보기 처리에 들어가므로, 현재 VM에서
  바로 회복하는 기능은 추가하지 않았다. 새 목록/명시 Refresh의 기존 초기화가 남아 있다.
- 속성 조회와 실제 파일 열기는 원자적이지 않다. 이 검사 뒤 StorageFile 취득/오디오 속성/셸
  썸네일 요청 사이에 발생하는 추가 상태 변경까지 차단한다고 보장하지 않는다. 속성 조회 자체도
  동기 파일시스템 질의이므로 해당 질의의 강제 중단이나 별도 시간 상한을 신설하지 않았다.
- `ReturnOnlyIfCached`는 공식적으로 캐시 또는 내장 썸네일을 반환하는 옵션이다. 이 변경은
  기존 보수 정책을 유지하며, 모든 클라우드 제공자에서 실제 다운로드가 전혀 없다는 검증으로
  과장하지 않는다. 공식 계약과 실제 제공자 검증 한계는 선행 조사 문서에 출처와 함께 있다.
- 실제 OneDrive 파일을 내려받거나 핀/동기화 상태를 바꾸지 않았다. WinUI 화면 및 실제
  WinRT 썸네일·오디오 속성 호출은 이번 자동 시험으로 실행하지 않았다.

## 검증

| 항목 | 결과/범위 |
| --- | --- |
| 신규 CloudFileAccess 시험 | 14개 통과. 실제 `ModuleWorker` 큐 뒤에서 로컬→온라인 전용 또는 조회 실패를 주입하고 직접 읽기 callback 0회를 검증 |
| 기존 true 유지/새 스캔 복원 | 기존 true에서는 속성 조회/읽기 0회. 새 false 스냅샷과 현재 로컬 속성이면 정상 읽기 1회 |
| 세 속성 비트/조회 실패 | 허용 판정 false와 직접 읽기 0회. 비트 정의는 제품에서 중복하지 않음 |
| 취소/예외 | 이미 취소된 true/false, 조회 도중 정상 반환/예외와 취소 경합, 제공자의 취소 전파, 실제 읽기 I/O/취소 예외 원형 유지 |
| 실제 로컬 파일 | TEMP 합성 파일 읽기 성공, 삭제 뒤 callback 미호출. OneDrive 파일 사용 없음 |
| 관련 집중 회귀 | 신규 14 + ExplorerListing/BoundedOperation/CancellableBatch 기존 32 = 46/46 통과 |
| 셸/오디오 분기 | helper가 내린 제한 판정이 `cachedOnly`와 `wantAudioInfo`에 함께 적용되는 것을 제품 호출부 정적으로 확인. 테스트 이름의 thumbnail은 정책 의미이며 실제 WinRT 호출 성공/호출 수 측정이 아님 |
| 활성 좌 리스트 차단/캐시 | 공통 ReadLocal의 null 계약은 단위 시험. 실제 RequestDetail의 null 조기 반환이 캐시 대입보다 앞서고 finally에서 게이트가 해제되는 것은 정적 검수. WinUI 상세 정보 캐시 통합 실행은 미수행 |
| 빌드 | 전체 Release/x64 `-m:1` 0경고·0오류. 활성 리스트 보강을 추가한 뒤 App 및 참조 프로젝트 영향 빌드도 0경고·0오류 |
| 최종 전체 회귀 | 8개 테스트 프로젝트 **580/580 통과**, 실패/건너뜀 0. Core262·Image37·Archive71·Video36·Hardware25·Audio22·DocumentModel91·FileOperations36 |
| 정적 검사 | 구조 20프로젝트, 릴리스 게이트 우회 7변형 검사 통과. 신규 파일 포함 diff 공백 검사·한글 이스케이프 검사·버전 XML 파싱 통과 |

집중 시험 명령:

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD 'artifacts/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PWD 'artifacts/nuget'
$env:DOTNET_ROOT = Join-Path $PWD 'artifacts/dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
& artifacts/dotnet/dotnet.exe test tests/KOTU.Core.Tests/KOTU.Core.Tests.csproj --no-restore -c Release -p:Platform=x64 --filter 'FullyQualifiedName~CloudFileAccessTests|FullyQualifiedName~ExplorerListingTests|FullyQualifiedName~BoundedOperationTests|FullyQualifiedName~CancellableBatchTests' --logger 'trx;LogFileName=a399-b-focused.trx' --results-directory TestResults/a399-b-focused
& artifacts/dotnet/dotnet.exe build KOTU.sln --no-restore -c Release -p:Platform=x64 -p:TreatWarningsAsErrors=true -m:1
& artifacts/dotnet/dotnet.exe build src/KOTU.App/KOTU.App.csproj --no-restore -c Release -p:Platform=x64 -p:TreatWarningsAsErrors=true -m:1
& artifacts/dotnet/dotnet.exe test KOTU.sln --no-build --no-restore -c Release -p:Platform=x64 -m:1 --logger trx --results-directory TestResults/a399-b-final
pwsh -NoProfile -File eng/Test-Architecture.ps1
pwsh -NoProfile -File eng/Test-ReleaseWorkflow.ps1
```

집중 결과: `artifacts/a399-b-focused-tests.log`,
`TestResults/a399-b-focused/a399-b-focused.trx`.
전체 빌드 로그: `artifacts/a399-b-build.log`.
활성 리스트 최종 영향 빌드: `artifacts/a399-b-app-final-build.log`.
최종 전체 시험: `artifacts/a399-b-final-tests.log`, `TestResults/a399-b-final`(TRX 8개).
구조/릴리스 게이트: `artifacts/a399-b-architecture.log`, `artifacts/a399-b-release-gates.log`.
로컬 테스트 호스트 소켓 제한은 선행 배치에서 확인되어 test는 자동 승인된 제한 밖에서 실행했다.

남은 A399 범위는 Attributes 감시와 무의미한 목록 전량 교체 방지, 제공 지연의 유한 재시도다.
이번 보호 보강이 그 기능을 대신하거나 사용자 제보를 재현·해결했다고 판정하지 않는다.

## 메인 최종 검수

중앙 두 워커와 좌측 FetchPool의 호출자를 대조해 모든 추가 속성 조회가 워커 안에 있음을 확인했다.
기존 true·취소·조회 실패·callback 예외 보존, 활성 좌측 정보의 null 조기 반환과 게이트 finally를
검수했다. 8개 TRX의 실제 카운터 합계580/580, 구조20프로젝트·배포 게이트7변형도 확인했다.

Release/win-x64 self-contained publish 및 필수 파일 검사에 성공했다.
근거: `artifacts/a399-b-publish.log`, 출력 `artifacts/a399-b-publish/`, FileVersion0.392.0.0.
로컬에 설치된7-Zip이 없어 앞 배치와 같은 `artifacts/native/7zip/7z.dll`을 출력에 포함했다.
실행 파일·PRI·녹화 DLL·라이선스·샘플·VLC 엔진/플러그인 존재를 확인했다. 새 실행본의 직접 UI 및
설치본 시험은 수행하지 않았다. 이 검사는 원격 Setup/Standalone 배포 검사를 대신하지 않는다.

검사한 로컬 Core DLL SHA-256: `C21610093AFC1613D3BA6D016A05C8B85AD4807298BDEBADE5A42DA353C6D9DF`.
검사한 로컬 App DLL SHA-256: `D7B54C385E416BB031E273E40B54CB9B4B666BA997ED79478336CC8F84FB762D`.
