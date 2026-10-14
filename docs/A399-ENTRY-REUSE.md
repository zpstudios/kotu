# A399-a 후속 — 항목/미리보기 재사용

2026-10-14, 제품 버전0.394.0 구현. A399-c 재시도, A400, A401-b/c는 포함하지 않는다.
구현은 GPT-6 Astra/high에 직렬 위임했고 메인이 검수·자동 시험·실제 UI 검증을 수행했다.

## 재사용과 무효화 계약

- `ExplorerListing.Entry`는 열거한 전체 Attributes도 저장한다. 워커 열거가 이미 읽은
  속성을 재사용하며 UI에서 파일 I/O를 추가하지 않는다.
- 자동 재조회는 `ExplorerReuse.Entries`에서 경로(OrdinalIgnoreCase), 이름(표시 대소문자
  포함), 폴더 여부, 크기, 수정/생성시각, placeholder, Attributes를 비교한다. 같은 상태이고
  dirty 경로가 아니면 **이전 Entry 객체**를 돌려준다. 새 Entry 참조는 새 요청 세대다.
- 경로 dirty는 셸 캐시만 달라진 이벤트도 강제로 무효화한다. rename은 이전/새 경로 둘 다
  수집한다. 감시 병합과 UI 보류 집합은 각각1024개 상한이며 넘으면 경로를 비우고 전체
  무효화1비트로 접는다. 감시 Error도 전체 무효화한다. 단 보류 작업을 깨우는
  `RequestRefresh()`는 새 dirty 파일이 있다는 뜻이 아니다.
- 실행 중 snapshot을 따로 떼어 이후 통지가 섞이지 않게 한다. 편집 또는 같은 감시 세대의
  정렬/필터로 스캔이 무산되면 snapshot을 복원하고 보류1회를 실행한다. 열거 실패는
  반복 재시도를 예약하지 않는다. 감시 오류로 재생성하면 전체 무효화 신호를 재생성 뒤 합친다.
- 명시 Refresh는 재사용 비교를 건너뛰므로 같은 메타데이터도 새 Entry/VM이다.
  정렬/필터는 기존 Entry 참조를 그대로 정렬한다. 새 S4 표면은 자신의 VM을 만들고,
  기존 표면이 동일한 Entry seed를 다시 받으면 자신의 VM을 재사용한다.

## 표시와 요청 수명

좌/중앙은 독립된 VM/ObservableCollection을 유지한다. `ExplorerReuse.Items`는 Entry의
참조와 VM 요청 수명을 함께 확인한다. 살아 있고 같은 참조인 VM만 재사용하며 변경/삭제된
VM은 영구 취소한다. 취소된 VM을 Restart하지 않으므로 옛 완료가 새 세대 캐시/플래그를
덮을 수 없다. 신규 VM 초기화는 컬렉션에 연결하기 전에 끝내 A352 통지 규칙을 유지한다.

일반적인 같은 순서 목록은 O(n) 비교이며 변경된 위치만 Replace한다. 같은10,000개에는
컬렉션 통지0개, 한 위치 변경에는 Replace1개다. 삽입/삭제/정렬은 Add/Remove/Move를 쓰고
Reset하지 않는다. 대규모 순서 재배열의 Move 검색은 최악 O(n²)이며 일반 자동 갱신의
동일 순서 경로와 구분한다. 중앙의 동일 VM 재측정은 이미 표시한 bitmap/텍스트를 지우지 않는다.

- 중앙: 표면 배치 토큰과 VM 토큰을 묶어 게이트 대기/worker에 넘긴다. 변경 없는 요청은
  자동 스캔에 취소되지 않고, 파일 교체/폴더 전환/해제는 취소된다. 캐시 반영 전에도 검사한다.
- 좌 상세: 스캔의 `_loadSeq`와 분리해 VM 토큰으로 대기·완료를 보호한다. 따라서 자동 스캔이
  `DetailRequested=true`인 변경 없는 요청만 중간에 버리는 문제가 없다. 새 VM은 옛 경로의
  상세 캐시를 제거하므로 길이/수정시각 불변 이벤트와 명시 Refresh도 다시 조회한다.
- 폴더 전환/열거 오류: 표시 VM과 Entry snapshot을 함께 비운다. 좌 페인 해제 뒤 Loaded는
  이전 목록을 현재 폴더에 재표시하지 않고 현재 폴더를 새로 열거한다. 해제 전 스캔 결과는
  load/watch 세대에 의해 폐기한다. 중앙 재로드는 취소된 VM을 새 VM으로 교체한다.
- 게이트는 **실제 worker delegate가 반환할 때까지** 소유한다. 개별 취소는 게이트 대기자를
  제거하지만 실행 중 worker의 자리를 조기에 풀지 않는다. 100세대 변경도 중간 세대의
  worker 요청을 축적하지 않는다. `ShellFetch`의 기존5초 timeout 뒤 OS 네이티브 작업이
  영구 잔류하는 상한을 이번 관리 시험이 입증하는 것은 아니다.

## 계측과 시험 경계

기존 diag trace에 `[reuse] list/tiles full=0|1 patch=0|1 reused=N created=N removed=N`을
추가했다. full은 ItemsSource를 다시 연결한 횟수이고 patch는 기존 컬렉션을 유지한 갱신이다.
created/removed는 VM 세대 교체 수이며 patch1이 반드시 컬렉션 변경1을 뜻하지 않는다.
동일 목록은 patch1·created0·removed0일 수 있다. 기존 `ShowEntries` 수는 갱신 호출 수라
이제 **전량 교체 수로 해석하면 안 된다**. 기존 fill text/shell trace는 중복/취소 요청을
선검사한 다음 기록한다.

`ExplorerReuseTests`는 제품 Core 비교/컬렉션 조정/CancellableBatch/ModuleWorkerPool을
사용한다. WinUI VM/컨테이너와 실제 클라우드 제공자는 시험 대역 범위 밖이다.
시험 항목: 메타데이터/속성/경로 규칙, 셸 캐시 dirty, rename 양쪽,1024 overflow/error,
snapshot 복원,10,000개 통지, 정렬/필터/삭제/추가, 명시Refresh/재로드,
느린 취소무시 요청100세대와 변경 없는 in-flight 완료, 폴더 전환/해제의 늦은 결과.
`FolderWatchTests`에는 debounce 소비 중 다음 dirty와 rename 양쪽 전달 시험을 더했다.
기존 burst/편집/수명/CloudFileAccess 시험은 보존했다.

신규 API 선례: ObservableCollection은 ArchiveView.Rows, linked cancellation은
ArchiveView의 뷰 수명, Tag 소유자/ReferenceEquals는 기존 탐색기 컨테이너에 선례가 있다.
`ReferenceEqualityComparer`와 ObservableCollection.Move는 이 배치의 첫 사용이며 Core 빌드로
실재를 확인했다. WinUI Move/Replace의 실제 컨테이너 유지 동작은 UI 검증 지점이다.
문제가 있으면 Move가 필요한 순서 변경만 목록 재연결로 보수 복구하고, 자동 동일 위치
갱신의 Replace·VM 수명 보호는 유지하는 것이 최소 복구다.

## 구현 담당 실행 기록

- Core Release/x64 빌드: 성공, 경고0·오류0 (`artifacts/a399-reuse/core-build.log`).
- 관련 Core 시험: 제한 안 실행에서 testhost 연결90초 실패로 중단. 성공으로 세지 않으며
  권한 반복 재시도하지 않았다 (`core-tests.log`, `tests/reuse.trx`).
- 첫 App 기본 병렬 빌드: 진단 없이 실패/경고0·오류0 (`app-build.log`). 기존 정본의
  `-m:1` 직렬 명령은 `CurrentFolder`의 nullable 인수 오류CS8604를 발견했고, 미생성
  LocalAssembly에 따른 XAML 오류가 뒤따랐다 (`app-build-serial.log`). 해당 인수를
  패턴 검사로 보호하도록 수정했다. 수정 뒤 전체 빌드는 메인 검증으로 넘긴다.
- 정적 `git diff --check`, 수정 코드의 한글 escape 검사는 이상 없음.

실제 UI의100회 지속450ms 이벤트, 편집 보류, 폴더 이동, 명시Refresh, PDF 확장자 폴백과
최종 전체 시험/배포 결과는 아직 이 구현 담당 실행의 성공 근거에 포함하지 않는다.

## 메인 검수와 최종 자동 검증

- 메인은 변경 파일과 신규 파일을 별도로 검수했다. 폴더 전환 도중 해제/재로드의 이전 목록
  오표시와 정렬/필터가 감시 스캔을 무산시킬 때 dirty 재예약 누락을 찾아 구현 담당에게
  직렬 보완을 요청했다. 수정 뒤 현재 폴더 재열거와 보류1회 복원 경로를 대조했다.
- 수정 후 전체 Release/x64 직렬 빌드 **경고0·오류0**, 전체 **621/621**, 실패0·skip0.
  관련 재사용/감시/CloudFileAccess 집중 시험36/36도 통과했다. 전체621에는 신규12개가 포함된다.
- 구조20프로젝트·릴리스 게이트7개 통과. 근거는 `artifacts/a399-reuse/final-build.log`,
  `full-tests.log`, `full-tests/*.trx`, `full-tests-summary.json`, `verified-tests/`다.
- 제한 안에서 중단된 testhost 실행은 성공으로 계산하지 않았다. 같은 빌드의 시험을 허용된
  제한 밖 실행에서 검증했다. 최초 App nullable 경고는 수정 후 전체 빌드에서 해소됐다.

실제 UI 비교 및 최종 배포 결과는 아래 후속 기록으로 구분한다.

## 메인 실제 UI 검증

검증한 실행본은 `artifacts/a399-reuse-publish/KOTU.exe`의 v0.394.0이다. 빌드 당시 정보 버전의
커밋 접미사는 이전 HEAD `b888d05`이며, 검증한 변경 소스의 SHA-256 목록과 실행본/어셈블리 해시는
`artifacts/a399-reuse/verified-source-hashes.json`, `package-identity.json`에 따로 보존했다.
UI 검증 뒤 제품 소스가 같은 해시임을 다시 확인했다. 로컬 패키지는 시스템7-Zip 부재 때문에
Prepare-Package의 자동 복사를 완료하지 못해 기존 `artifacts/native/7zip/7z.dll`과 라이선스를
복사하고 필수 DLL·VLC 엔진/플러그인을 확인했다. 자동 준비 성공으로 기록하지 않는다.

실제 WinUI Document S1에서 합성 폴더의 텍스트2개·손상 PDF1개·하위 폴더1개를 표시했다.
기존과 같은 보호된 TEMP fixture 스크립트로 rename.txt의 본문/길이/수정시각을 유지하며
속성을 바꿨다. trace의 ShowEntries 호출과 전체 VM 교체를 분리해 집계했다.

| 조건 | 재검색 | 전체 VM 교체 | 새 VM | 텍스트 요청(변경/불변) |
| --- | ---: | ---: | ---: | ---: |
| 짧은10ms 이벤트100회 | 1 | 0 | 1 | 1 / 0 |
| 지속450ms 이벤트100회 | 69 | 0 | 69 | 69 / 0 |
| 지속450ms 추가100회, 이동 전 완료 | 86 | 0 | 86 | 86 / 0 |
| 이름 편집 중 짧은100회 | 0 | 0 | 0 | 0 / 0 |
| 편집 취소 뒤 보류 반영 | 1 | 0 | 1 | 1 / 0 |
| 명시 Refresh | 1 | 1 | 4 | 1 / 1 |
| 하위 폴더 이동 완료 뒤 이전 폴더100회 | 0 | 0 | 0 | 0 / 0 |

- 자동 갱신과 명시 Refresh 모두 기존 ItemsSource를 유지했다. 명시 Refresh는 Entry/VM4개를
  새로 만들고 텍스트2개와 PDF 셸 미리보기1개를 다시 요청했다. 전체 VM 교체와 ItemsSource
  재연결은 서로 다른 지표다. 손상 PDF의 확장자 폴백도 유지됐다.
- 지속 이벤트의 재검색 수69/86은 이벤트 전달·스케줄링에 좌우된다. 이전 v0.393.0 기준선
  92검색·92전체 VM 교체·184텍스트 요청과 비교할 때, 입증한 개선은 **불변 요청0과 전체 VM 교체0**이다.
  재검색 수 차이나 입력 지연·프레임 성능 개선을 재사용만의 효과로 주장하지 않는다.
- 이름 편집기는100회 이벤트 뒤에도 같은 편집 상태/값을 유지했고 Escape 뒤 보류1회를 처리했다.
  폴더 이동 시험은 이동 완료 뒤 이전 폴더 이벤트를 보낸 것이다. 이동 도중 경합 실측으로 세지 않는다.
- Image S1에서 임시 합성 PNG의 실제 bitmap을 확인했다. 필터 밖 rename.txt100회 이벤트 뒤
  재검색1·VM2개 재사용·생성0·셸 재요청0이었고 같은 bitmap이 계속 표시됐다.
- UI 수치는 opt-in trace를 켠 로컬 합성 시험이다. 실제 OneDrive 제공자/다운로드 상태,
  S4 반복 재개방·대규모 가상화·네이티브 작업 영구 잔류 상한의 실기기 검증은 포함하지 않는다.
  취소를 무시하는 관리 worker와 늦은 결과 보호는 Core 시험 범위로 구분한다.

근거: `artifacts/a399-reuse/ui-*.json`, `ui-trace.log`. 사용자가 기존 앱의 잠시 종료·검증·복원을
승인했다. 검증 후 시험 앱 정상 종료, 설정 백업 SHA-256 일치 복원, 원래 fixture5개 본문/길이/
수정시각 불변, 임시 PNG 제거를 확인했다. 설치본을 다시 실행해 원래 ZIP의 루트 내용을 열었다.
시험을 위해 사용자의 설치본을 교체하거나 ZIP을 다시 추출하지 않았다. 복원 기록은 `ui-cleanup.json`.

## 배포 상태

제품 v0.394.0 커밋은 `4e60cf7`이다. 첫 master push는 자동 승인 검토가 원격 대상과 자동 정식
릴리스에 대한 이번 명시적 승인 근거 부족으로 차단했다. 원격 전송·새 release 성공으로 세지 않는다.
사용자에게 `github.com/zpstudios/kotu` master 푸시와 v0.394.0 자동 릴리스 승인을 요청했다.
승인되면 검증된 제품 커밋과 후속 기록을 전송하고 원격 build/release를 확인한다.
A399 전체 제보의 실제 OneDrive 원인 해결이나 A399-c 재시도 완료는 선언하지 않는다.
