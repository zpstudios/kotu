# A11 재생 목록 루프 옵션 — 구현 전 설계

> **A11 구현 전 설계 — v0.209.0 시점, 단일 원본.**
> 등재문: "목록 루프(기본 켬) / 현재 영상 루프(기본 끔) / 루프 횟수 1·3·무한(기본 무한).
> ※ 폴더 기반 연속 재생(목록 개념)부터 설계 필요." (docs/REQUIREMENTS.md:1077)
> 이 문서는 코드 변경 없이 실코드 정독으로 확정한 설계이며, 구현 배치(§7)가 이 문서를 사양으로 쓴다.
> 사용자 확정이 필요한 결정은 §6에 모았다 — §6이 닫히기 전에는 §7 배치를 착수하지 않는다.

---

## 1. 목록 개념 — 이미지 폴더 이웃 탐색의 이식

### 1.1 선례 판정: 이식 가능

`ImageFolderNavigator`는 **UI 비의존 순수 로직**이고 파일 시스템 접근이 생성자 주입
(`Func<string, IEnumerable<string>> enumerateFiles`)이라 그대로 영상·오디오에 이식된다.

| 근거 | 파일:라인 |
|---|---|
| UI 비의존·주입형 열거 선언 | src/KOTU.Module.Image/ImageFolderNavigator.cs:3-7, 22-25 |
| 확장자 필터(대소문자 무시) | ImageFolderNavigator.cs:32-34 |
| 처음 연 파일은 필터 밖이어도 포함 | ImageFolderNavigator.cs:36-38 |
| 자연 정렬(파일명 기준, 전체 경로로 안정화) | ImageFolderNavigator.cs:40-45, 112-147 (`NaturalStringComparer`) |
| `Create` = `Directory.EnumerateFiles` 기본 헬퍼 | ImageFolderNavigator.cs:51-52 |
| `MoveNext`/`MovePrevious` — **끝에서 순환 없음** | ImageFolderNavigator.cs:73-87 |
| `PeekNext`/`PeekPrevious` (A194 선읽기용) | ImageFolderNavigator.cs:67-71 |
| `Remove`(삭제 후 갱신) | ImageFolderNavigator.cs:93-102 |
| 단위 테스트 존재 | tests/KOTU.Module.Image.Tests/ImageFolderNavigatorTests.cs |

### 1.2 배치 위치: Core 신설 (복제 아님)

모듈끼리는 참조 금지(ARCHITECTURE.md:273-274 §11.3 — "모듈 → Core만")이므로 선택지는 둘:

- **ⓐ Core 승격(채택 제안)**: `KOTU.Core.Navigation.FolderPlaylist`(가칭) + `NaturalStringComparer`를
  Core에 신설. §11.3 "두 모듈 이상이 쓰는 표면은 공용으로 올린다"(ARCHITECTURE.md:279-280)에 부합.
  소비자는 영상·오디오 2개 + (장래) 이미지 3개.
- ⓑ 모듈별 복제: `TimeText`·`PlaybackResumeStore`가 이미 Video/Audio 두 벌 복제로 존재하는
  실선례(src/KOTU.Module.Video/TimeText.cs ↔ src/KOTU.Module.Audio/TimeText.cs,
  PlaybackResumeStore.cs 두 벌 — 키만 `video.resume`/`audio.resume`으로 다름:16/:18).
  관례 위반은 아니나 이번엔 로직이 크고(정렬 비교자 포함) 소비자가 3모듈이라 ⓐ가 낫다.

**이미지 모듈의 `ImageFolderNavigator` → Core 클래스 전환은 이번 범위에서 하지 않는다**
(중복 잔존 허용 — ⓑ 선례상 무방). 별도 등재 후보로 보고만 한다. 이유: A194 선읽기 캐시·삭제
갱신(`_preloadCache`, ImageViewerView.xaml.cs:1006-1009)이 맞물려 있어 전환 회귀 면적이 크다.

### 1.3 폴더 스캔·필터·정렬·숨김 파일

| 축 | 설계 | 근거 |
|---|---|---|
| 스캔 | `Directory.EnumerateFiles(dir)` 1회, 생성 시점 스냅샷(감시 없음 — 이미지와 동일) | ImageFolderNavigator.cs:32, 51-52 |
| 필터 | 모듈 `SupportedExtensions` 그대로 — 영상 14종, 오디오 8종 | src/KOTU.Module.Video/VideoModule.cs:13-17, src/KOTU.Module.Audio/AudioModule.cs:13-16 |
| 정렬 | **파일명 자연 정렬 고정** (탐색기 정렬 `explorer.sort` 비연동 — 이미지 선례와 일치. 연동 여부는 §6-②) | ImageFolderNavigator.cs:40-45 |
| 숨김 | **숨김·시스템 파일 제외를 기본으로 신설**(이미지 낙수의 답습 거부 — §6-③). 판정 단일 원본 `ExplorerListing.ShouldShow` 재사용 — Core에 있어 모듈에서 접근 가능 | src/KOTU.Core/Routing/ExplorerListing.cs:9, 177 / 낙수 기록 docs/REQUIREMENTS.md:985-988 |

숨김 필터 주의 2건:
- 필터 주입은 열거 델리게이트와 같은 방식(생성자 인자 `Func<string, bool>? includeFile` 또는
  `bool includeHidden`)으로 두어 순수 로직·테스트 가능성을 유지한다. `File.GetAttributes` 호출이
  파일당 1회 늘어난다 — 생성은 재생 시작 경로(비-UI 워커행 가능)라 허용, 단 생성을 UI 스레드에서
  돌리지 않는다(§11.1 동기 IO 금지 — ARCHITECTURE.md:248-249. 자막 탐지처럼 `Worker.Run`으로).
- `explorer.showHidden` 설정 연동은 **불가**: 키 상수가 셸 internal이다
  (src/KOTU.App/ExplorerPane.xaml.cs:101 `internal const string ShowHiddenSettingKey`).
  연동하려면 키를 Core로 승격해야 한다(별도 결정 — §6-③).
- 처음 연 파일이 숨김이어도 목록에 포함한다(ImageFolderNavigator.cs:36-38의 "처음 연 파일 포함"
  규칙이 자연히 커버 — 명시 파일 열기는 의도된 접근. A175의 "명시적 열기는 허용" 판정과 동형).

---

## 2. EOF 훅 — libvlc 경로 판정 (최대 쟁점)

### 2.1 현재 Ended 처리 (정독 결과)

| 사실 | 근거 |
|---|---|
| `EndReached`는 libvlc 스레드에서 오고, **콜백 안 `Stop()` 금지(교착)** 주석 명시 | src/KOTU.Module.Video/VideoPlayerView.xaml.cs:569-571, 오디오 542 |
| A11 승계 주석: 루프가 EOF를 재정의하면 다음 재생을 잇는 자리 = 이 핸들러 | VideoPlayerView.xaml.cs:572-574 |
| EndReached가 이어보기 기록을 "끝까지 봄"으로 보고 → **기록 삭제**(97% 정책) | VideoPlayerView.xaml.cs:575-576 + PlaybackResumeStore.cs:51-56 |
| Ended에서는 `Play()`만으로 재시작 불가 → **`PlayCurrent()`로 미디어를 다시 건다**(실검증 선례) | VideoPlayerView.xaml.cs:773-779 (TogglePlayPause Ended 분기), 오디오 590-595 |
| `PlayCurrent` = `new Media(lib, new Uri(_filePath))` + `p.Play(media)` | VideoPlayerView.xaml.cs:359-361, 오디오 373-374 |
| A130 잔상 정리는 "Ended 상태 + 크기 변경" 조건 — 루프가 Ended에 머물지 않으면 자연 비활성 | VideoPlayerView.xaml.cs:603-607 (`ClearEndedFrameOnResize`), docs/REQUIREMENTS.md:1105-1106 |
| 재표시 펌프·Scale 불달의 원인 판독(EOF에서는 어떤 재적용도 화면을 못 그림) | VideoPlayerView.xaml.cs:589-601 주석 |

### 2.2 경로 후보 판정

| 경로 | 판정 | 근거 |
|---|---|---|
| **A. 같은 미디어 리핏 = `EndReached` → `Dispatch`(UI 스레드) → 미디어 재장전 재생** | **성립 — 채택** | TogglePlayPause의 Ended 분기(773-779)가 "Ended에서 UI 스레드로 미디어를 다시 걸어 재생"을 이미 실검증. `EndReached` 핸들러는 이미 `Dispatch`로 UI 갱신을 하고 있어(578-586) 그 블록 끝에 전이 호출만 얹으면 된다. 신규 libvlc API 0건 |
| **B. 목록 진행 = `Dispatch` → `OpenPath(next)`** | **성립 — 채택** | `OpenPath`(397-425)는 이어보기 저장→Fit Contain 회귀(A30 사양 부합)→`PlayCurrent`→`ContentOpened` 셸 동기화까지 완결된 실사용 경로. 오디오 동형(384-402) |
| C. libvlc 옵션 `input-repeat`(인스턴스/미디어 옵션) | **기각 — 선례 0건, CI/런타임 위험 후보** | 저장소 전체 grep 0건. `Media.AddOption` 사용례도 0건(인스턴스 옵션은 `--no-video-title-show`·`--audio-visual` 형태만 — VideoPlayerView.xaml.cs:305, 오디오 290-291). 게다가 EndReached 자체가 안 와서 A130 잔상 체계·A186 상태 신호·횟수 1/3 카운트가 전부 재설계돼야 한다 |
| D. `p.Stop()` 후 `p.Play()` | **기각** | `Stop()`은 해제 워커 전용(UI 스레드 교착 위험 — VideoPlayerView.xaml.cs:441-443, 454-459). Ended에서 `Play()` 단독 재시작 불가는 773 주석의 실사례 |
| E. `p.Position = 0` / `p.Time = 0` 재감기 | **기각** | Ended 상태에서 시킹류 쓰기가 닿는다는 선례가 저장소에 없음(A130 판독: EOF에서는 활성 vout이 비어 쓰기가 닿지 않는 부류 — :589-601). 검증 불가 경로를 채택하지 않는다 |

### 2.3 리핏 전용 변형 `ReplayCurrent()` (경로 A의 세부)

`PlayCurrent()` 원형을 그대로 재사용하면 매 루프마다 부작용 3건이 난다. 리핏 전용 변형이 필요하다:

| `PlayCurrent`의 동작 | 리핏에서의 문제 | 처리 |
|---|---|---|
| `_pendingStartOverlay = true` → A12 "파일명 · 1080p" 3초 오버레이 | 매 루프 재표시(소음) | **생략** (VideoPlayerView.xaml.cs:360) |
| `LoadSubtitleList()` — 폴더 재스캔 + `FillSubtitleFlyout()`이 `_subtitleIndex`를 1로 **리셋**(:673) | 사용자가 고른 자막/끄기 선택이 매 루프 초기화 | **생략**하고 `_pendingAutoSubtitle = true`만 세워 Playing 핸들러(:545-549)가 현재 `_subtitleIndex`를 재적용하게 한다 |
| `ContentOpened` 발화(:363) — 셸 S4 종료·오버레이·아이콘 갱신 | 같은 파일이라 전부 무의미한 재계산 | **생략** |
| 이어보기 조회(`GetResumePositionMs`) | EndReached가 이미 기록을 지웠으므로(§2.1) null → 0부터 시작이 보장되지만, 조회 자체가 불필요 | `_pendingResumeMs = -1` 고정 |
| 배속 재적용·`ApplyFitMode` | 필요(미디어 교체 시 초기화) — Playing 핸들러가 이미 수행(:551-559) | 그대로 활용 |

즉 `ReplayCurrent()` = `_durationMs`/`_lastReportedMs` 리셋 + `_pendingAutoSubtitle = true` +
`new Media` + `p.Play(media)`. 나머지는 기존 Playing 핸들러가 잇는다.

**재진입 가드**: `Dispatch`된 전이 실행 시점에 ① `_tornDown` ② `_filePath` 불변
③ `p.State == VLCState.Ended` 유지(그새 사용자가 ▶로 이미 재시작했으면 무동작)를 재검사한다.
`Dispatch`는 이미 `_tornDown`을 이중 검사한다(:615-622) — ②③만 추가.

### 2.4 오디오 모듈 — 동일 축 성립

`AudioPlayerView`는 구조가 동형이라 같은 설계가 그대로 이식된다:
`OnPlayerEndReached`(AudioPlayerView.xaml.cs:540-556) / TogglePlayPause Ended 분기(:590-595) /
`PlayCurrent`(:362-380) / `OpenPath`(:384-402). 리핏 변형에서 뺄 것은 `TitleOverlay` 재대입과
`ContentOpened`뿐이고(자막·A12 오버레이 없음), EQ는 인스턴스 지속(:342-345 주석), 출력 장치는
Playing 핸들러 재적용(:518-526)이 이미 "같은 플레이어로 다음 곡"을 전제하고 있어 목록 진행과
정합. 트레이 1초 타이머는 Playing/Paused/EndReached 핸들러가 관리(:528, :536, :553)하므로 자동.

---

## 3. 상태 기계 — (목록 루프) × (현재 루프) × (횟수)

### 3.1 상태 정의

- 설정 3축(등재문 1:1): `loopList`(기본 **on**) / `loopCurrent`(기본 **off**) /
  `loopCount` ∈ {1, 3, ∞}(기본 **∞**) — `loopCount`는 `loopCurrent`의 반복 횟수 축으로 해석
  (해석 확정 = §6-①).
- 런타임 1개: `_loopPlays`(현재 파일에서 소진한 리핏 횟수) — **파일이 바뀌면 0으로**
  (`PlayCurrent`/`OpenPath` 진입 시 리셋. `ReplayCurrent`만 증가시킨다).

### 3.2 충돌 우선순위 (제안)

**현재 루프(횟수 내) > 목록 진행 > 목록 루프(처음으로) > 정지.**
둘 다 켜져 있으면 현재 항목에 머무는 쪽이 우선 — 횟수가 소진되면 목록 규칙으로 폴백한다
(음악 플레이어 통례의 "repeat one이 repeat all을 가린다"와 동일).

### 3.3 EOF 전이표

| # | 조건 (위에서부터 첫 일치) | EOF 전이 | 경로 | A130 잔상 정리와의 관계 |
|---|---|---|---|---|
| 1 | `loopCurrent` on ∧ (`loopCount` = ∞ ∨ `_loopPlays` < `loopCount`) | 같은 파일 재시작, `_loopPlays++` | `ReplayCurrent()` (§2.3) | Ended에 머물지 않음 → `ClearEndedFrameOnResize` 자연 비활성(승계 주석 :572-574의 예언대로) |
| 2 | (1 불일치) ∧ `HasNext` | 다음 파일 | `OpenPath(PeekNext)` | 동상 |
| 3 | (2 불일치 = 목록 끝) ∧ `loopList` on ∧ `Count` > 1 | 첫 파일로 | 목록 첫 항목으로 이동 후 `OpenPath` (인덱스 리셋 API — `FolderPlaylist`에 `MoveFirst()` 신설) | 동상 |
| 4 | (3 불일치 = 목록 끝) ∧ `loopList` on ∧ `Count` == 1 | 같은 파일 재시작 (`_loopPlays` 무관 — 목록 루프의 축) | `ReplayCurrent()` | 동상 |
| 5 | 그 외 (`loopList` off) | **정지 — 현행 그대로** | 기존 EndReached UI 갱신만(▶·끝 위치·`PlaybackStateChanged`) | **유일하게 Ended에 머무는 경로** — A130 트레이드오프(잔상 검정 정리)가 그대로 유효 |

- 전이 1~4의 짧은 Ended 구간(EndReached → Dispatch 사이)에 SizeChanged가 오면 `Clear()`가 한 번
  돌 수 있으나, 이어지는 Playing의 `ApplyFitMode`(:559)가 재도장하므로 무해(A130 수리 주석
  :599-601과 같은 논리).
- 기존 EndReached의 UI 갱신(▶ 표기·시크바 끝·`PlaybackStateChanged`)은 전이 1~4에서 **생략**한다
  — 곧 Playing이 덮어쓰므로 깜빡임만 만든다. 단 `_resumeStore.Report(끝)`(기록 삭제)는 전이와
  무관하게 유지한다(다 본 파일의 이어보기 청소는 루프와 별개 사실).
- `EncounteredError`(재생 실패)는 목록 진행 트리거로 **삼지 않는다**(1차 범위 — 실패 파일에서
  자동으로 다음 파일로 넘어가는 스킵은 무한 실패 루프 위험이 있어 별도 설계 대상으로 보고만).

#### 개정 이력 (위 표는 A11 1차 설계 시점의 것 — 정본은 코드 주석)

- **A255(v0.255.0)**: 2축 결합(`loopList` ∧ `loopCurrent`)을 폐기하고 **단일 루프 모드**
  (없음 · 목록 루프 · 한 파일 루프, 상호 배타)로 재작성했다. 표의 조건 열을 그대로 읽지 말 것 —
  현행 판정은 `AdvanceAfterEnd`의 전이 1~5 주석이 정본이다
  (VideoPlayerView.xaml.cs / AudioPlayerView.xaml.cs).
- **A258(v0.258.0)**: 전이 2(목록 진행 블록) 진입부에 설정 게이트 한 줄이 붙었다 —
  설정 → Playback → "Auto-play next file"(키 `player.autoNext`, bool, **기본 true**,
  영상·오디오 공용 한 벌 = `KOTU.Core.Settings.PlaybackSettings`). 게이트 식은
  `루프 모드 == 없음 ∧ !autoNext → 전이 5(정지)`다.
  - **루프 모드가 '없음'일 때만 유효**하다(확정) — 목록 루프·한 파일 루프가 켜져 있으면 옵션을
    무시하고 종전 전이 그대로 간다. 한 파일 루프의 **횟수 소진 낙하**도 게이트 지점에서
    `_loopMode`가 여전히 `File`이므로 통과 = 다음 파일로 계속 간다.
  - 끈 상태의 정지는 목록 중간 파일에서도 일어나며, 반드시 전이 5 블록(▶ 표기 · 시크바 끝 ·
    영상 `PlaybackStateChanged` / 오디오 트레이 타이머 정지)을 거친다 — A130 잔상 정리의
    "Ended에 머무는 것은 정지 전이뿐" 조건도 그대로다.
  - 값은 캐시하지 않고 EOF마다 라이브로 읽는다(변경 이벤트·구독 0). 플레이어 바에는 대응 버튼을
    두지 않는다(A255 루프 버튼과 뜻이 겹친다).

---

## 4. UI 배치 · 설정 키

### 4.1 하단 바 칸 — 영상 c9 · 오디오 c9 신설

칸 번호 체계·규격의 정본은 XAML 주석이다:

| 근거 | 파일:라인 |
|---|---|
| 영상 c0 재생 · c1 위치 · c2 시크(*) · c3 길이 · c4 음소거 · c5 볼륨 96 · c6 배속 84 · c7 자막 · c8 Fit 64 | src/KOTU.Module.Video/VideoPlayerView.xaml:86-195, 산식 주석 VideoPlayerView.xaml.cs:150-163 |
| 오디오 c7 EQ · c8 장치 (A163·A164 신설 — "비디오 자막 버튼+플라이아웃 규격") | src/KOTU.Module.Audio/AudioPlayerView.xaml:66-125, docs/REQUIREMENTS.md:1116-1117 |
| 1칸 버튼 = 32×32, 플라이아웃 = `MenuFlyout Placement="Top"` + `RadioMenuFlyoutItem` | VideoPlayerView.xaml:102-104, 126-132 / 코드 구성 선례 VideoPlayerView.xaml.cs:681-695 |

**제안: 양 모듈 모두 c9에 루프 버튼 1칸(32×32) + `MenuFlyout`(Top) 신설** — A163 EQ 칸 신설과
같은 규격 복제. 플라이아웃 구성(사용자 노출 문자열은 영어):

```
ToggleMenuFlyoutItem  "Loop list"                (체크 = loopList)
─────────────────────────────
RadioMenuFlyoutItem   "Repeat this file: Off"    (그룹 "loop-current")
RadioMenuFlyoutItem   "Repeat this file: 1×"
RadioMenuFlyoutItem   "Repeat this file: 3×"
RadioMenuFlyoutItem   "Repeat this file: Infinite"
```

- `ToggleMenuFlyoutItem` 선례 = A160 숨김 토글(src/KOTU.App/ExplorerPane.xaml.cs:462-484 — 단
  셸 코드라 모듈 XAML/코드에서의 사용은 이번이 모듈 첫 사례. `RadioMenuFlyoutItem`+구분선은
  자막·EQ 플라이아웃 선례 그대로).
- 버튼 아이콘은 상태형(끔/목록/현재)으로 갱신 — `UpdateFitButton` 관용구(VideoPlayerView.xaml.cs:854-871).
  글리프 후보 ``(RepeatAll)·``(RepeatOne)는 **저장소 첫 사용 글리프**다. `FontIcon Glyph`
  형태 자체는 선례 다수(E9A6·E190 등)라 CI 위험은 없고, 렌더 모양만 실기기 확인 포인트.
- 핫키 = `L`(Loop) 제안 — `HotkeySupport.Bind`로 플라이아웃 열기(자막 C 선례 —
  VideoPlayerView.xaml.cs:1223-1224). 영상 기사용 키 M·S·C·A·F(:1220-1227)·오디오 M·S(:938-940)와
  충돌 없음. docs/A86-keymap.md·A196-key-audit 대조는 구현 배치의 통과 조건으로.

**폭 산식 영향(축약 임계 640)**: A144 주석이 "A151이 전체화면 칸(버튼 32+간격 6 = 38)을 제거했을 때
임계를 안 내려 38이 여유분으로 남아 있다"고 명기(VideoPlayerView.xaml.cs:160-162). 새 c9(32+6 = 38)가
**그 여유를 정확히 소진**하므로 **임계 640 유지** — 단 해당 주석의 "여유분" 서술을 "A11 c9가 소진"으로
개정할 것. 오디오는 고정 폭 합 356(AudioPlayerView.xaml:69-72 산식) + 38 = 394로 여전히 영상 계수보다
작아 축약 로직 불요 유지(주석 산식만 갱신).

### 4.2 설정 키 — 전역 1벌 저장 (제안)

기존 `video.*`/`audio.*` 키는 전부 전역 1벌·즉시 저장이고 창별 저장 선례는 없다:

| 기존 키 | 근거 |
|---|---|
| `video.volume` / `audio.volume` (int) | VideoPlayerView.xaml.cs:226, 465 / AudioPlayerView.xaml.cs:228, 440 |
| `audio.equalizer`·`audio.outputDevice` (string, 즉시 `Set`+`Save`) | AudioPlayerView.xaml.cs:210-211, 656-657, 723-724 |
| `video.resume` / `audio.resume` (목록) | 각 PlaybackResumeStore.cs:16 / :18 |

**신설(모듈별 독립 — volume 선례와 동일하게 video/audio 따로):**

| 키 | 형 | 기본값 | 등재문 대응 |
|---|---|---|---|
| `video.loopList` / `audio.loopList` | bool | `true` | "목록 루프(기본 켬)" |
| `video.loopCurrent` / `audio.loopCurrent` | bool | `false` | "현재 영상 루프(기본 끔)" |
| `video.loopCount` / `audio.loopCount` | string `"1"`·`"3"`·`"infinite"` | `"infinite"` | "루프 횟수 1·3·무한(기본 무한)" |

- 문자열 enum 저장은 `explorer.sort`의 `"created"` 관례(docs/REQUIREMENTS.md:494-495)와 동형.
- 상태 로컬 소유(`_muted` 규칙 — VideoPlayerView.xaml.cs:807-810): 읽기는 생성자 1회, 변경 시
  즉시 `Set`+`Save`(EQ 선례 :654-658). 창 간 실시간 전파는 하지 않는다(`explorer.showHidden`과
  같은 성질 — 다음 창/재시작 반영).

---

## 5. A186 자동 숨김·셸 동기화 — 목록 진행 시 무엇이 따라오는가

### 5.1 채택: 모듈 내부 교체(`OpenPath`) + `ContentOpened` 통지

모듈은 셸을 못 부른다(ARCHITECTURE.md:273-278 §11.3 — 의존 방향, 셸 훅은 정적 델리게이트 선례뿐).
셸 라우터 재진입 대신 **모듈 내부 `OpenPath`**를 쓰면 기존 `IContentStateSource.ContentOpened`
배선이 셸 동기화를 전부 처리한다 — 이미지 ←/→(ImageViewerView.xaml.cs:990-995 → :508)와
동일한, 셸이 설계상 지원하는 "뷰 내부 탐색" 경로다(MainWindow.xaml.cs:1339-1341, 1450-1451 주석).

`OnContentOpened`(src/KOTU.App/MainWindow.xaml.cs:1520-1542)가 하는 일 = 목록 진행 시 자동으로
따라오는 것:

| 셸 동작 | 라인 | 판정 |
|---|---|---|
| S4 자동 종료·`ResetBarAutoHide`(A186 — 전환 경합 방지) | :1523-1524 | 원하는 동작 그대로 |
| `_currentFilePath` 갱신 → `VideoBarContext` 판정 입력 | :1527, 2188-2189 | 성립 |
| 최근 폴더(A174) `RememberLastFolder` — 같은 폴더면 조기 반환 | :1530, 1510-1517 | 같은 폴더 연속 재생이라 저장 0회(가드 :1512-1514) — 정확히 원하는 동작 |
| 드라이브 줄·오버레이·정보 캐시 갱신 | :1529-1533 | 성립 |
| 트레이·창 32px 아이콘 `RefreshShellIcons` | :1539-1541 | 성립 (+ 모듈 자체 `TrayStatusChanged` — VideoPlayerView.xaml.cs:364) |
| **창 제목은 무제 전이에서만 갱신**(`wasUntitled`) | :1525, 1538 | 목록 진행 시 제목이 이전 파일명으로 남는다 — **이미지 ←/→와 동일한 기존 거동**(FileTitle 갱신은 :1208처럼 셸 열기 경로뿐). 답습하고 낙수(등재 후보)로 보고 |

### 5.2 재생 상태 신호(A186)

- `OpenPath`/`ReplayCurrent` → libvlc Playing → `PlaybackStateChanged`(VideoPlayerView.xaml.cs:529)
  → 셸 `OnPlaybackStateChanged`(MainWindow.xaml.cs:2236-2240) → `ResetBarAutoHide` + `ArmBarAutoHide`
  — **기존 배선(:1352-1357)만으로 자동 숨김 카운트가 새 재생에서 다시 열린다. 신규 배선 0.**
- 전이 순간 바가 잠깐 복원됐다가 3초 후 다시 숨는 것은 수용(전이 가시화 — `ResetBarAutoHide`가
  콘텐츠 교체마다 도는 현행 규칙 :1524와 정합).
- §3.3에서 EndReached의 `PlaybackStateChanged` 발화를 전이 1~4에서 생략하므로 "Ended 신호 →
  즉시 Playing 신호"의 이중 재평가도 없다.
- All Readable 경유도 성립: `ContentOpened`·`PlaybackStateChanged` 둘 다 자식 중계가 이미 있다
  (src/KOTU.Module.AllReadable/AllReadableView.xaml.cs:128-134, 165-178, 51-57).
- **오디오는 `IPlaybackStateSource` 미구현**(AudioPlayerView.xaml.cs:28-29 — 계약 목록에 없음.
  A186 낙수 "오디오 모듈로 확대"가 등재 후보로 남아 있다 — docs/REQUIREMENTS.md:463).
  오디오의 목록 진행·루프는 자동 숨김과 무관하게 성립하므로 이번 범위에 그 낙수를 **포함하지
  않는다**(별건 유지 — §6-⑤와 무관).

---

## 6. 사용자 확정 질문 (부록 B "확인 필요" 등재용 문안)

1. **[A11] 루프 횟수의 의미** — "루프 횟수 1·3·무한"에서 "1"은 ⓐ 한 번 더 반복(총 2회 재생)인가
   ⓑ 총 1회 재생(= 반복 없음)인가? 설계 제안 = ⓐ(반복 횟수 — "Repeat 1×"가 자연스럽다).
   또한 이 횟수는 **현재 영상 루프 전용**이고 목록 루프는 무한 고정으로 해석했다 — 맞는지?
2. **[A11] 목록 정렬** — 폴더 연속 재생의 순서를 이미지 뷰어와 같은 **파일명 자연 정렬 고정**으로
   할지, 탐색기 정렬(`explorer.sort`) 연동으로 할지? 설계 제안 = 파일명 자연 정렬 고정(이미지
   선례 일치·모듈이 셸 정렬 상태를 모르는 구조 그대로).
3. **[A11] 숨김 파일** — 연속 재생 목록에서 숨김·시스템 파일을 **항상 제외**(제안)할지,
   `explorer.showHidden` 설정과 연동할지(연동하려면 키의 Core 승격 필요)? 이미지 뷰어의 같은
   문제(숨김 미필터 낙수 — A160 기록)를 이번에 같이 고칠지도 별도 답 필요.
4. **[A11] 이전/다음 키** — 영상·오디오에서 ←/→는 이미 5초 시킹이다. 이전/다음 파일 키를
   부여한다면 무엇으로 할지(후보: PgUp/PgDn)? 아니면 1차는 자동 진행만 두고 수동 이전/다음
   키·버튼은 생략할지? 설계 제안 = 1차는 자동 진행만(수동 탐색은 등재 후보로 분리).
5. **[A11] 오디오 적용 범위** — 목록 루프·현재 루프·횟수 UI를 오디오 모듈에도 동일하게(같은
   기본값 포함) 넣는지? 설계 제안 = 동일 적용(코드 구조 동형 — §2.4. 단 배치는 분리, §7).

---

## 7. 구현 배치 분할 제안 (직렬 3배치)

§6 확정 후 착수. 모든 배치 공통: dotnet 없음 → CI가 유일한 컴파일러, `TreatWarningsAsErrors`,
신규 파일은 `git status` `??` 별도 검수, 커밋·푸시는 오케스트레이터.

### 배치 1 — Core `FolderPlaylist` 신설 + 단위 테스트 [권장 모델: Opus]

- 내용: `src/KOTU.Core/Navigation/FolderPlaylist.cs`(가칭) — `ImageFolderNavigator`를 일반화 복제
  (생성자 주입 열거·확장자 필터·자연 정렬·Move/Peek/Remove) + `MoveFirst()`(§3.3 전이 3) +
  숨김 필터 인자(§1.3). `NaturalStringComparer` 동반 이동판 + `tests/KOTU.Core.Tests`에
  `ImageFolderNavigatorTests` 이식판. **이미지 모듈은 건드리지 않는다**(§1.2).
- 뒤 배치가 얹힐 훅: 클래스 시그니처(생성자 인자·`MoveFirst`)를 이 배치에서 동결.
- 최대 함정: 신규 파일이라 `git diff`에 안 보인다(`??` 검수). 이미지 쪽 원본과 이름·네임스페이스가
  달라야 하며(중복 정의 혼동 방지), 테스트 프로젝트 참조(`KOTU.Core.Tests`가 이미 Core 참조)
  외 새 참조를 만들지 말 것.

### 배치 2 — 영상 모듈: 상태 기계 + EOF 훅 + c9 UI + 설정 키 [권장 모델: Fable]

- 내용: `FolderPlaylist` 소비(생성은 `Worker.Run` — §1.3), §3.3 전이표, `ReplayCurrent()`(§2.3),
  EndReached 분기(§2.1 승계 주석 자리), c9 루프 버튼+플라이아웃+L 키(§4.1), `video.loop*` 3키(§4.2),
  A144 폭 주석 개정(§4.1).
- 최대 함정: ① EndReached 콜백 안에서 재생 API 직접 호출 금지 — 반드시 `Dispatch` 경유 + 재진입
  가드(§2.3) ② `PlayCurrent` 원형 재사용 시 A12 오버레이·자막 인덱스 리셋·`ContentOpened` 재발화
  3종 부작용(§2.3 표) ③ 전이 1~4에서 기존 EndReached UI 갱신을 생략하되 이어보기 끝 보고는 유지
  (§3.3) ④ `FillSubtitleFlyout`이 `_subtitleIndex`를 리셋한다(:673) — 리핏 경로에서 호출 금지.
- 통과 조건 표(요구): 전이표 5행 × 구현 지점 대조표 / 신설 키 3개 grep 표 / EndReached 경로에서
  `Stop()`·직접 `Play()` 0건 grep.

### 배치 3 — 오디오 모듈 동형 이식 [권장 모델: Opus]

- 내용: 배치 2의 사양을 오디오에 복제(§2.4 차이 반영 — A12·자막 없음, TitleOverlay·트레이 타이머는
  기존 핸들러 재사용), c9 루프 버튼, `audio.loop*` 3키, 폭 산식 주석 갱신(§4.1).
- 최대 함정: 오디오에는 `IPlaybackStateSource`·`PlaybackStateChanged`가 **없다**(§5.2) — 비디오
  코드를 그대로 베끼면 존재하지 않는 이벤트 발화로 컴파일이 깨진다. A186 확대는 이번 범위 밖
  (별건 낙수 유지).
- 실기기 확인 포인트(3배치 공통, HANDOVER 이관용): 루프 글리프(E8EE/E8ED) 렌더 / EOF 리핏 시
  검은 프레임·소리 끊김 체감 / 목록 진행 시 바 잠깐 복원 후 재숨김 체감 / 단일 파일 폴더 +
  목록 루프의 연속 재생.
