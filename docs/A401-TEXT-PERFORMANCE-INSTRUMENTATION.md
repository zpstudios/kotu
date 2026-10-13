# A401-a — TXT/코드 성능 opt-in 계측

2026-10-14. 제품 기본 TextBox·저장·편집 동작과 버전은 유지한다.
이 배치는 **원인 분리를 위한 계측**이며 UI 스크롤 성능 개선 완료를 뜻하지 않는다.
조사·다음 배치 판정의 정본은 `A401-DOCUMENT-PERFORMANCE-INVESTIGATION.md` §5다.

## 1. 실행 제어

설정 파일에 키를 추가하지 않는다. 아래 환경변수는 새 프로세스에만 전달한다.
이미 실행 중인 KOTU로 파일 열기 요청이 전달되면 그 프로세스의 환경을 사용하므로, 기존 창을
정상 종료하고 **실제로 실행된 exe 경로와 PID**를 확인한 다음 실험한다. 과거 `sky.launch_app`이
요청한 publish 경로 대신 설치본으로 연결된 적이 있으므로 launch 요청만으로 동일성을 판단하지 않는다.

| 변수 | 값 / 기본 | 의미 |
| --- | --- | --- |
| `KOTU_TEXT_PERF` | `1`만 켜짐 / 없음 = off | 문서 열기마다 숫자 계측 세션 생성 |
| `KOTU_TEXT_PERF_WRAP` | `off` / 그 외 = on | 해당 TextBox의 임시 줄바꿈. 영구 설정 쓰기 없음 |
| `KOTU_TEXT_PERF_DECOR` | `off`, `on`, 그 외 = default | off는 거터·가이드·마커·옛 진단판까지 완전 억제. on은 큰 문서의 자동 장식 해제만 우회하는 **시험 옵션** |
| `KOTU_TEXT_PERF_SECONDS` | 정수 5~120 / 기본 30 | 첫 Rendering 경계 후 2초 정착을 제외한 안정 측정 길이 |

`on`도 사용자 표시 토글·뷰 모드의 억제·장식 예외 폴백은 존중한다. 따라서 Options.Decor와
LastState의 Guides/Marks/ViewSuppressed/DecorDisabled, 실제 DecorPass/RectCall 수를 함께 본다.
상한 초과에서 on을 쓰더라도 `_capped`·`_truncated`·읽기 전용·더티/저장/인쇄 가드는 그대로다.
off에서도 기존 거터 **패딩**은 남겨 같은 파일 on/off 비교의 본문 폭을 맞춘다. 그림·좌표 조회는 없다.
상한 초과 문서는 제품이 원래 거터 패딩을 예약하지 않으므로 강제 on에서 거터는 공간 부족으로
숨을 수 있다. 글자 상한의 정책 자체를 바꾸는 실험은 아니다.

Standalone은 컴파일된 DistributionPolicy를 두 관문(세션 생성·출력)에서 검사한다.
환경변수가 있어도 계측·실험 옵션·결과 파일을 모두 만들지 않는다.

PowerShell 예시(이미 검증한 publish 출력과 합성 fixture를 사용):

```powershell
$env:KOTU_TEXT_PERF = '1'
$env:KOTU_TEXT_PERF_WRAP = 'on'
$env:KOTU_TEXT_PERF_DECOR = 'on'
$env:KOTU_TEXT_PERF_SECONDS = '30'
$taskExe = (Resolve-Path './artifacts/a401-a-publish/KOTU.exe').Path
$taskFixture = (Resolve-Path './artifacts/a401/model-baseline/fixtures/ascii-80-lf-262144.txt').Path
$taskProcess = Start-Process -FilePath $taskExe -ArgumentList ('"' + $taskFixture + '"') -WindowStyle Hidden -PassThru
$taskProcess.Id
# 각 시행 뒤 다음 프로세스에 실험 환경이 남지 않도록 해제한다.
Remove-Item Env:KOTU_TEXT_PERF, Env:KOTU_TEXT_PERF_WRAP, Env:KOTU_TEXT_PERF_DECOR, Env:KOTU_TEXT_PERF_SECONDS
```

실제 파일명·경로를 요약 내용으로 전달하는 옵션은 없다. 위 launch 경로는 실행 제어용이다.
합성 fixture manifest에서 정확한 이름·바이트 수·UTF-16 단위·논리 줄 모양을 시행별 기록에 적는다.

## 2. 결과와 측정 구간

자동 측정 완료 또는 뷰 해제 때 워커가 `%TEMP%/KOTU/text-performance/session-0.json`부터
`session-7.json`까지 순환 출력한다. 프로세스 첫 세션은 slot0이다. 결과에 Pid·Session·ModuleMvid·
ModuleVersion이 있으므로 파일 수정시각만으로 새 결과를 고르지 않는다. 메인은 매 시행 결과를
즉시 별도 artifact에 복사해 보존한다. 여러 프로세스의 동시 실험은 슬롯을 덮어쓸 수 있어 피한다.

파일은 각 128 KiB 이내, 보류 출력도 최대8개이며 워커1개가 직렬 기록한다. 출력 지연 중 빠른 파일
전환으로 큐가 차면 가장 오래된 보류 결과를 버린다. 종료 직후 프로세스를 강제 종료하면 결과가
기록되지 않을 수 있다. 자동 완료 파일의 PID/Session/EndReason을 확인한 뒤 앱을 정상 종료한다.
본문·파일명·경로·예외 메시지·문자 위치·키 내용은 결과에 없다. 문자열은 고정 단계/지표명과
빌드 식별자뿐이다. 기존 별도 진단 `diag.trace`/EOF 오버레이와 이 결과를 혼동하지 않는다.

| Phase | 범위 | 해석 |
| --- | --- | --- |
| Initial | opt-in 세션 생성부터 ApplyLoadedText 이후 첫 CompositionTarget.Rendering 콜백 | worker 읽기, 프로그램 대입, 최초 getter/정규화 등을 포함. Rendering은 **렌더 직전 경계**이며 화면 제출·GPU 완료 시각이 아님 |
| Settling | 그 경계부터 2초 | 초기 배치·장식 후속 작업을 별도 보존. 안정 스크롤 수치에 합치지 않음 |
| Stable | 정착 후 요청한 5~120초 | 같은 창 크기·줌·입력 조건으로 실제 휠/스크롤 시행. idle 표본도 포함됨 |

첫 Rendering이 없으면 최대180초 뒤 돌아온 probe에서 종료한다. 뷰를 먼저 해제하면 부분 결과다.
`FirstRenderingObserved`·`StableWindowCompleted`·`EndReason`을 확인한다. UI가 막히면 시간 창
종료 처리는 돌아온 probe까지 늦어질 수 있으나, 기한 뒤 새 이벤트/측정 시작은 통계에서 제외한다.
경계를 가로지른 probe의 delay/interval은 다음 phase의 표본으로 섞지 않는다. 따라서 각 phase의
첫 공백 일부가 통계에서 빠질 수 있으며 구간별 총계가 전체 실행의 연속 타임라인은 아니다.

## 3. 숫자의 의미와 한계

| 지표 | 측정하는 실제 경로 |
| --- | --- |
| OpenWorker | 기존 ReadTextSmart 전체. 파일 읽기·디코드·라운드트립 검사·상한 절단 포함, worker 대기열 시간 제외 |
| TextSet | 실제 EditorBox.Text 대입의 동기 구간. 뒤따르는 비동기 레이아웃은 제외 |
| TextGet | 공유 스냅샷이 없을 때 실제 Text getter. 캐시 재사용에는 새 getter 표본을 만들지 않음 |
| NormalizeUi / NormalizeWorker | 같은 실제 개행 정규화 함수의 UI/worker 비용 |
| CountLines | 최초 거터 자리 계산 구간. 큰 문서의 기존 생략 경로와 구분해 해석 |
| LineIndexBuild / LineIndexReuse | 같은 실제 TextLineIndex.Update의 새 색인/같은 참조 재사용 |
| DecorPass | 기존 장식 RenderCore 한 번의 동기 비용. 렌더 시도·조기 반환도 포함 |
| RectCall | 캐시 miss, trailing, 원점 확인을 포함한 실제 GetRectFromCharacterIndex 호출. 호출 수·누적·최대 시간, 실패한 호출도 포함 |
| FirstRendering | 세션 시작부터 첫 Rendering 경계까지 한 표본 |
| ProbeDelay | worker 요청부터 UI dispatcher callback 시작까지 대기. **입력 완료 지연·프레임 시간은 아님** |
| ProbeInterval | 같은 phase에서 연속 UI callback 시작 사이 간격. 명목 50ms 주기와 worker 스케줄링 영향도 포함 |

TotalMs·MaxMs·Count는 전체 기록 표본의 값이다. P50UpperMs/P95UpperMs는 1μs부터 1.25배씩
커지는 고정128칸 히스토그램의 **상계 근사값**이다(1μs 이하 최저칸). 정확한 표본 백분위수로
표기하지 않는다. RectCall은 패스 내에서 정수 tick만 누적하고 패스 끝에 한 번 합쳐 계측 잠금
비용을 줄였다. 그 지표는 HistogramSamples=0이며 p50/p95=0은 **측정 없음**이지 0ms라는 뜻이 아니다.

AllocatedBytes는 해당 동기 함수가 실행된 스레드의 관리 할당량이다. 중첩 지표를 더하면 중복된다.
UiAllocatedBytes는 같은 phase의 UI probe 사이에서 관측된 관리 할당량이며 첫/끝 경계의 할당과
네이티브 할당은 포함하지 않는다. 메모리는 worker의 1초 표본 중 WorkingSet/PrivateBytes 최대값이다.
이는 **1Hz 표본 최대**이며 프로세스의 진짜 순간 최고치가 아니다. 계측용 배열·타이머·콜백과
통계 연산 자체의 비용도 켠 상태 수치에 포함되므로 작은 차이를 제품 개선이라고 해석하지 않는다.

종료 때 probe가 아직 pending이면 ProbePendingAtStop/PendingProbeAgeMs에 그때까지의 경과를
남긴다. 기한 뒤 도착한 마지막 callback의 지연은 EndingProbeDelayMs에 별도로 남긴다. 이 값을
본문 통계에서 누락된 긴 지연이 없는지 보는 보조값으로 사용하며 완결 표본 백분위수에 합치지 않는다.

이벤트 종류는 TextChanged·SelectionChanged·SizeChanged·LayoutUpdated·PointerWheel·
ScrollIntermediate/Final·Zoom과 장식의 렌더 트리거를 phase별 센다. FirstScroll/LastScroll에는
hook 성공 여부·viewport/extent·두 축 offset이 있으며 ScrollOffsetChanged·MaxHorizontalMovement/
MaxVerticalMovement가 실제 이동을 보여 준다. **휠 입력과 실제 이동은 다르다.** 가로 scrollbar
기본은 바꾸지 않았다. 긴 한 줄+NoWrap에서 세로 range가 없다고 가정하지 않고 실제 extent와
화면을 확인한다. 이번 실험에서는 잘못된 세로 배치/빈 본문도 관측했으므로, 정상 표시가 없는
시행을 단순한 no-scroll 대조군이나 성능 개선으로 세지 않는다.

FirstState/LastState는 UTF-16 단위 문자 수, 이미 계산된 논리 줄 수, 줌, Wrap, 장식 토글/억제,
capped/truncated와 TextBox DIP 크기다. Lines=0은 아직 실제 색인을 관측하지 못했다는 뜻이다.
StableConfigurationChanges가 0보다 크면 크기·줌·문자·줄·모드 등이 바뀌었으므로 고정 조건 비교에서
그 시행을 제외하거나 변경 원인을 명시한다. 전체 문서가 아니라 **현재 편집기에 공급된 앞부분**의 수다.

## 4. 재현 매트릭스와 수명 확인

1. 합성 일반 TXT/코드, 긴 한 줄, 매우 많은 짧은 줄, 한글·서로게이트·CRLF를 준비한다.
   UTF-16 1,048,576 바로 아래/정확히/바로 위와 4MiB 바이트 상한 양쪽을 별도 구분한다.
2. 같은 빌드 MVID·창 크기·줌에서 Wrap on/off × Decor on/off 네 조합을 시행한다.
   auto 정책 확인은 Decor=default로 별도 시행한다. 표시 토글은 시행 중 바꾸지 않는다.
3. initial/settling을 지나 일정 시간 같은 위치·동작으로 휠을 입력한다. 휠/스크롤 이벤트 수,
   viewport/extent·offset 변화로 실제 시행을 확인한다. 동일성/입력 미확인이면 수치 비교를 보류한다.
4. 결과 PID·MVID·phase 완결 상태를 확인해 artifact로 보존한다. 모델 벤치마크·도구 호출 소요시간·
   스크린샷 지연을 이 UI 결과의 대체값으로 쓰지 않는다. 반복 시행과 조건 차이를 기록한다.
5. 작은 문서에서 선택/캐럿·한글·이모지·CRLF·Ctrl+C·편집/undo/더티 복귀·원래 인코딩 저장·
   외부 변경 보호를 확인한다. capped/truncated에서는 읽기 전용과 저장 차단을 확인한다.
6. 열기 중 빠르게 뷰 전환/닫기, 측정 중 전환, 자동 완료 뒤 전환을 시험한다. 옛 worker/callback이
   새 세션을 멈추거나 닫힌 UI를 건드리지 않아야 한다. env 없음/Standalone에서도 실험 파일을 열어
   새 결과 파일·계측 타이머·실험 Wrap 변경이 없음을 확인한다.

추가 observer 구독은 TextBox 5종(휠 포함)·내부 ScrollViewer 1종·최초 Rendering 1종이며,
자동 완료/해제/열기 실패/취소/표면 전환 모두 Stop에서 해제한다. Rendering은 첫 콜백에서 먼저
해제한다. 주기 Timer는 워커에서만 50ms마다 돌며 미완료 probe1개를 넘기지 않는다. Dispose는
Timer와 probe의 enqueue/acknowledge 참조를 끊는다. 이미 dispatcher에 들어간 콜백 자체는
취소 API가 없어 남을 수 있지만, 대상 참조가 제거돼 **무효 콜백**으로 끝난다. 그 콜백이 있는 동안
새 probe를 넣지 않으며 해제 뒤 활성 예약은0이다. 제품 장식의 기존 스크롤 예약도 뷰 Unloaded에서
정지·구독 해제하고 다시 Loaded된 경우에만 재개한다.

## 5. 구현·검증

제품 변경: `DocumentView.xaml.cs`의 실제 경로 측정과 임시 Wrap,
`EditorDecor.cs`의 한 패스/rect 누적·실험 억제 및 해제 정리.
신규 `TextPerformanceCapture.cs`는 UI 관찰/worker/출력,
`TextPerformanceData.cs`는 UI 비의존 유계 통계·수명 gate다.
`TextPerformanceTests.cs`는 후자를 source link해 실제 정책과 수명을 검증한다.

저장소 선례: `TryEnqueue`(MainWindow/RecordView), `System.Threading.Timer`(RecordView),
`Stopwatch.GetTimestamp`(ScreenRecordingSession), `CompositionTarget.Rendering`(DocumentView),
`AddHandler/RemoveHandler` 및 `ViewChanged`(기존 문서/뷰 구현).
새 계측 API 의미는 [.NET 관리 할당 문서](https://learn.microsoft.com/en-us/dotnet/api/system.gc.getallocatedbytesforcurrentthread?view=net-8.0),
[Timer.Dispose 문서](https://learn.microsoft.com/en-us/dotnet/api/system.threading.timer.dispose?view=net-8.0),
[프로세스 WorkingSet64](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.workingset64?view=net-8.0),
[PrivateMemorySize64](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.privatememorysize64?view=net-8.0)를 따른다.
Timer.Dispose 뒤 이미 대기 중이던 콜백이 실행될 수 있으므로 자체 gate의 수명 검사도 필요하다.

초기 검증: Document 모듈 Release/x64 빌드 성공(0경고·0오류).
기본 병렬 빌드는 이 환경에서 오류 설명 없이 실패했으며 `-m:1` 직렬 실행은 통과했다.

| 이름 붙인 통과 조건 | 구현 에이전트 결과 / 근거 |
| --- | --- |
| 기본 off | 환경 `1`만 허용, override만 주면 비활성. 비활성 Parse 1,000회 관리 할당0 시험 통과. observer/타이머는 opt-in 분기 안에서만 생성 |
| 측정 분리 | 초기/정착/안정, worker/UI, 색인 build/reuse, rect aggregate 분리 시험 통과. phase 경계를 가로지른 probe가 안정 표본에 섞이지 않음 |
| 유계·본문 보호 | 고정128칸·enum 배열, 결과8슬롯/128KiB·보류8개. 합성 canary 본문 미포함과 직렬화 크기 시험 통과 |
| 단일 pending·해제 | 동시 요청1,000회에 큐1개. 거절/예외 뒤 pending0, dispose 후 늦은 콜백 무효, 이전 세션 콜백이 새 세션에 영향 없음 시험 통과 |
| 불완전 종료 | first Rendering 전 중단 시간, pending age, 완료 여부를 보존하고 늦은 측정은 버림. 시험 통과 |
| 실험/원문 분리 | Wrap·장식 표시만 임시 변경. 기존 capped/truncated/read-only/dirty/save 가드 diff 불변. Standalone opt-in 강제 차단 시험 통과 |
| 실제 UI 지연·해제 | 메인의 실행본 대조·합성 UI 시행 대상. 위 단위 시험으로 실기기 성능/이벤트 해제를 확인했다고 판정하지 않음 |

`KOTU.DocumentModel.Tests` **110/110 통과**(기존91 + 신규19), 실패0·skip0.
첫 제한 환경 실행은 빌드 성공 뒤 host의 localhost 연결 `SocketException(10013)`으로 중단됐다.
30초 timeout과 오류는 `artifacts/a401/instrumentation-tests.log` 및
`instrumentation-testhost.host.26-10-14_02-16-09_52647_5.log`에 있다. 같은 빌드의
`--no-build --no-restore` 실행을 허용된 제한 밖 환경에서 수행해 통과했다.
성공 근거는 `artifacts/a401/instrumentation-tests-unrestricted.log`,
`artifacts/a401/instrumentation-tests-unrestricted/a401-instrumentation-unrestricted.trx`다.
새 시험으로 UI 프레임·입력 지연·프로세스 순간 메모리 최고치를 검증했다는 뜻은 아니다.

구현 에이전트 정적 검수: diff 공백 검사 통과, 신규 파일 포함 한글 이스케이프0,
변경된 메서드 호출/해제 경로 전수 및 신규 필드 사용처 확인. 경고를 오류로 처리하는 모듈 빌드 통과로
미사용 필드·WinUI API 서명 오류가 없음을 확인했다. 제품 버전·저장 설정·커밋·push는 변경하지 않았다.
메인의 최종 App 빌드/실제 UI 결과는 다음 절에 별도로 기록한다.

## 6. 메인 실제 UI 1차 측정 — 2026-10-14

**계측 구현과 1차 관측 완료, A401 성능 개선은 미완료**다. 제품 버전은 조사 기준 v0.392.0을
유지했다. 이 수치로 입력 지연/프레임 개선이나 장식 off 제품 정책을 확정하지 않는다.

### 실행 동일성과 조건

- `artifacts/a401-a-publish/KOTU.exe`를 명시적으로 실행하고, 매번 실제 창의 exe 경로와 PID를
  대조했다. 설치본으로 전달된 과거 launch 결과를 이번 실행 증거로 쓰지 않았다.
- 측정14개 모두 Document MVID `70407c3d-c02d-4ee2-9987-156a05ad5a0b`, 버전 `0.392.0.0`.
  측정용 Document DLL SHA-256은
  `272763E2D60B6812932B852CEBD88500712B546245C79A4879C63D6E7FB09B82`다.
- 같은 PC, 최대화1280×750 캡처, 앱 배율150%, 문서 줌100%, 기본 Consolas14, 어두운 테마,
  가이드/마커 on, 기존 진단판 off. 정상 본문의 마지막 TextBox 크기는1280×729.333 DIP다.
  상한 초과는 안내 줄 때문에 본문 높이/거터 폭이 다르므로 직접 성능 개선율을 계산하지 않는다.
- `artifacts/a401/model-baseline/fixtures/`의 기존 합성 입력만 사용했다. 아래 크기는 바이트나
  화면 줄 수가 아니라 **원본 UTF-16 단위**다. 일반 문서는80단위마다 LF, many는 `x\n` 반복,
  long은 LF 없는 한 줄이다. 정확한 fixture와 해시는 이전 manifest/이번 백업에 보존했다.
- worker50ms probe, 정착2초 뒤 안정 창. 일반 시행은30초이며 아래에 예외 길이를 적었다.
  `sky.scroll`의 +2400, +2400, -2400을 화면에서 확인하며 개별 입력했다. 연속5초 드래그나
  동일 시각 재생은 아니다. 기한 뒤 입력은 제외되므로 **표의 실제 기록 횟수**를 따른다.
- 모든 요약은 first Rendering 관측·stable 완료, `StableConfigurationChanges=0`.
  `ActiveProbesAfterStop=0`은 Stop/Dispose 계약에 따라 직렬화하는 값이지 구독/타이머를 외부에서
  독립 실측한 수가 아니다. 실제 해제 검증은 gate 시험과 소스 수명 감사로 구분한다.

### 수치

시간은 ms. 초기 = 세션 시작부터 첫 Rendering 콜백이며 화면 제출 완료가 아니다.
장식/rect는 **안정 창 전체 누적**, 휠/이동은 실제 이벤트 수다. p95는 유휴 표본을 포함한
dispatcher 대기 상계 근사값이며 스크롤 응답 p95가 아니다. p50·할당·메모리·PID를 포함한
14행 정본은 [수치 CSV](A401-UI-BASELINE-2026-10-14.csv), 원시 JSON은 로컬
`artifacts/a401-a/<CSV Label>.json`, 집계 사본은 같은 폴더 `ui-results.csv`에 있다.

| 입력 | Wrap / Decor | 안정 초 | 초기 / 대입 | 휠 / 이동 | 장식 ms / 패스 | rect 수 / ms | probe p95 상계 / 최대 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 일반262,144 | on / on | 30 | 387.25 / 40.83 | 3 / 3 | 95.19 / 3 | 1,158 / 84.45 | 87.58 / 123.47 |
| 일반262,144 | on / off | 30 | 275.67 / 32.50 | 3 / 3 | 0 / 0 | 0 / 0 | 0.41 / 108.70 |
| 일반262,144 | off / on | 30 | 300.17 / 33.46 | 2 / 2 | 53.36 / 2 | 772 / 47.56 | 87.58 / 136.65 |
| 일반262,144 | off / off | 30 | 268.51 / 32.08 | 3 / 3 | 0 / 0 | 0 / 0 | 0.41 / 108.68 |
| 긴 한 줄1,048,576 | on / on | 30 | 3,989.67 / 87.49 | 3 / 3 | 290.09 / 3 | 1,441 / 286.67 | 0.52 / 124.38 |
| 긴 한 줄1,048,576 | on / off | 30 | 3,876.32 / 88.10 | 3 / 3 | 0 / 0 | 0 / 0 | 0.41 / 123.55 |
| 긴 한 줄1,048,576† | off / on | 30 | 272.83 / 87.70 | 0 / 0 | 0 / 0 | 0 / 0 | 1.01 / 123.69 |
| 긴 한 줄1,048,576† | off / off | 30 | 281.99 / 92.02 | 2 / 2 | 0 / 0 | 0 / 0 | 0.41 / 124.33 |
| 많은 줄1,048,576 | on / on | 30 | 15,745.61 / 1,086.97 | 1 / 1 | 17.16 / 1 | 124 / 14.93 | 87.58 / 138.05 |
| 많은 줄1,048,576 | on / off | 45 | 14,525.34 / 1,068.82 | 3 / 3 | 0 / 0 | 0 / 0 | 0.41 / 93.51 |
| 일반1,048,575 | on / default | 20 | 843.27 / 124.21 | 2 / 2 | 45.62 / 2 | 776 / 40.80 | 70.06 / 124.28 |
| 일반1,048,577 capped | on / default | 30 | 857.72 / 134.41 | 3 / 3 | 0 / 0 | 0 / 0 | 0.52 / 123.67 |
| 긴 한 줄262,144† | off / off | 15 | 118.59 / 21.99 | 1 / 1 | 0 / 0 | 0 / 0 | 0.52 / 125.34 |
| 일반1,048,576 | off / off | 25 | 787.87 / 123.53 | 3 / 3 | 0 / 0 | 0 / 0 | 0.52 / 109.15 |

† 정상 표시가 없어 성능 비교에서 제외한다. 1,048,576 한 줄은 장식 on/off 모두 본문이 비어
보였고, off에서 세로578 DIP·가로1,204 DIP 이동 뒤에도 같았다. on은 창 관찰/회복 도중 안정 창이
끝나 휠0이며 이후 입력을 수치에 추가하지 않았다. 262,144 한 줄은 글자가 y≈334, 캐럿은 y≈49에
있었고 가로 이동1,204 DIP 뒤에도 어긋났다. 반면 같은1,048,576을13,108개 짧은 논리 줄로 나눈
NoWrap 문서는 정상 표시/스크롤했다. 내부 getter 문자 수가 정상이어도 렌더 성공의 증거는 아니다.

일반256Ki on/on은 최초 파일럿으로 시험 host 작업과 일부 시간이 겹쳤다. 각 조건은1회이고
많은 줄의 안정 창/입력 횟수도 다르므로 작은 시간 차이·메모리 차이의 통계적 유의성을 주장하지 않는다.
안정 구간 UI 관리 할당은0~9.2KiB, working set 1Hz 표본 최대166.5~222.0MiB,
private bytes 표본 최대61.1~119.7MiB였으며 네이티브 순간 최고치/전체 할당량은 아니다.

### 관측과 다음 판단

1. 장식 on인 정상 시행에서 rect가 장식 동기 비용의 대부분을 차지했다. 긴 한 줄+Wrap on은
   3패스290.09ms 중286.67ms가 rect다. 장식의 가시 범위 좌표 작업을 줄일 근거는 확보했다.
   다만 유휴 포함 probe p95는 긴 줄 on/off 모두 작아, 이를 입력 반응 개선 수치로 해석할 수 없다.
2. 장식 off에서도 긴 한 줄 약3.88초, 524,289줄 문서 약14.53초의 초기 구간이 남는다.
   setter는 각각88.10ms/1,068.82ms이므로 그 차이를 Text setter 하나로 설명할 수 없다.
   네이티브 텍스트 배치/첫 프레임 경로가 별도 후보이며 이 계측만으로 호출 스택의 원인을 확정하지 않는다.
3. **NoWrap을 제품 해결책으로 채택하지 않는다.** 본문 소실/세로 위치 불일치를 먼저 독립 최소
   재현으로 분리해야 한다. 앱 XAML/WinUI 내부 레이아웃/렌더러 중 어느 층이 원인인지는 미확정이다.
   SDK 기본 HorizontalScrollMode=Auto·HorizontalScrollBarVisibility=Hidden이라는 정독 결과와
   실제 가로 이동을 확인했으므로, 단순히 가로 스크롤이 없어서라고 설명하지 않는다.
4. 상한 아래는 장식 유지, 바로 위는 capped=true·표시 문자1,048,576·자동 장식0·View only 안내를
   확인했다. 합성 문서에 입력을 시도해도 본문/더티가 바뀌지 않았고 Ctrl+S도 무동작이었다.
   상한 자체를 낮추거나 저장 정책을 바꾸지 않았다.
5. A396은 기존 스냅샷/revision 어댑터 설계를 유지한다. A401-b 착수 시 일반 문서와 긴 줄을
   각각 반복하고 **연속 스크롤 구간**의 지연을 별도로 측정한다. 프레임/입력 지연과 probe를
   구분하며 최종 위치의 가이드·마커·거터 정합을 통과 조건으로 둔다. A401-c는 별도 초기 배치 조사다.

### 기본 동작·보존과 남은 검증

계측 환경변수 없이 PID22088로 일반64Ki 문서를 열어 가이드/마커 표시, 한글·이모지·개행 입력,
더티 표시, Ctrl+Z 원복·더티 해제를 확인했다. 결과 slot0은 이전 PID16512 그대로여서 새 계측 출력을
만들지 않았다. 시험 종료 후 KOTU 프로세스0, 기존 합성 fixture15개 모두 사전 SHA-256과 같았다.
앱 설정은 백업으로 복원한 뒤 SHA-256
`9C683168BCC5039A085C24678B8E4CE646241C9F73F027A36CECE331AFE775B1` 일치를 확인했다.
사용자 문서 열기/저장, OneDrive 상태 변경은 하지 않았다.

이번 UI 시행은 전체 회귀 행렬이 아니다. 연속 스크롤 반복, 다양한 테마/배율, IME 조합 중 동작,
실제 인코딩 저장/외부 변경, 계측 중 빠른 파일 교체/초기 로드 해제, Standalone 실제 실행은 남는다.
부분 종료/옛 probe/새 세션 격리는 단위 시험과 정적 감사 통과이며 실제 UI 스트레스 완료로 세지 않는다.

App self-contained publish 성공, 구조20프로젝트·릴리스 게이트7개 통과.
최종 전체 솔루션 Release/x64 빌드 **0경고·0오류**, 전체 **599/599 통과**(8개 TRX,
실패0·skip0, 문서 모델110 포함). 빌드는 `-m:1 -nr:false --no-restore`, 시험은 같은 빌드의
`--no-build --no-restore`를 확인된 testhost 연결 제한 밖에서 실행했다.
근거는 `artifacts/a401-a/solution-build.log`, `full-tests.log`, `full-tests/*.trx`, `publish.log`.
최종 diff 공백·신규 파일 포함 한글 이스케이프 검사 통과. 계측 소스는 UI 시행 뒤 변경하지 않았다.

### 원격 검증

계측 커밋 `2a4a0e1cfde4e65124ba22aa0dde794ec1d1d696`을 master에 push했다.
[build38441378047](https://github.com/zpstudios/kotu/actions/runs/38441378047)은
2026-10-14 02:50:09 KST 완료·성공. 원격 로그에서 전체 빌드0경고·0오류, 8개 시험 실행
**599/599**, 실제 Explorer COM3동사·32K 초과 선택·일회 전달/nonce 검사 통과를 확인했다.
[release38441377995](https://github.com/zpstudios/kotu/actions/runs/38441377995)도 성공했으며
기존 v0.392.0 태그 때문에 패키징·설치·Standalone·게시 단계를 건너뛰었다. 새 제품 릴리스나
이번 계측 빌드의 실제 Standalone 검증 완료로 해석하지 않는다. 정식 배포 태그는 `e311bcf` 유지.
요약 로그는 `artifacts/a401-a/remote-verification.log`. 최종 기록만의 추가 push는 보류한다.
