# A349 — 미디어 키 연동(배치 3) 사전 조사 (2026-09-04)

> A349 배치 1(v0.341.0)이 `MoveToNeighbor`·⏮/⏭·가속기 4개를 두 재생 뷰에 심었다. 남은 것은
> **키보드 미디어 키(Previous/Next Track, Play/Pause, Stop)를 그 동작에 잇는 것**이다.
> 이 문서는 착수 전 조사의 원문(코드 변경 0)이며, 배치 계획의 정본은 `docs/REQUIREMENTS.md` A349 본문이다.
> 사용자 확정 = "미디어키에 연동도 당연" · 전역 훅(`RegisterHotKey`)은 **기각**(다른 플레이어와 충돌).

## 0. 요약

- **미디어 키는 XAML `KeyDown`/`KeyboardAccelerator`로 오지 않는다고 보는 것이 옳다.** OS는 미디어 키를
  포그라운드 창의 `WM_APPCOMMAND`로 먼저 보내고, `DefWindowProc`이 그것을 처리한 뒤에야(가상 키로 매핑
  가능한 경우) `WM_KEYDOWN`을 포스트한다. 즉 XAML 층에 도달하는지는 OS·드라이버 사정에 좌우되고,
  **다른 앱이 SMTC 세션을 갖고 있으면 KOTU가 포그라운드여도 키가 그쪽으로 간다.** 가속기 4개를 더 얹는
  "가장 싼 길"은 **동작 보장이 없다** — 단독으로는 채택 불가.
- **권장 = SMTC(`SystemMediaTransportControls`) 단일안.** `Windows.Media.SystemMediaTransportControlsInterop`은
  MS 공식 C# 상호운용 클래스 표에 올라 있고, **TFM `net8.0-windows10.0.19041.0`이면 그대로 쓸 수 있다**
  — 저장소가 이미 쓰는 `Windows.Graphics.Printing.PrintManagerInterop`(`PrintHost.cs:137`)과 **같은 표의
  같은 줄 방식**이다. 따라서 "선례 0건 API"이긴 하나 **패턴 선례는 1건 실존**한다(A211 인쇄 축).
- **비패키지(WindowsPackageType=None)에서 성립한다** — Firefox가 같은 방식(숨김 창 + `GetForWindow`)으로
  미디어 키를 받고 있고, WinUI 이슈 #2631에서도 "UWP/WinUI 전용 API가 아니다(Edge Chromium도 쓴다)"로
  정리됐다.
- **SMTC만이 트레이 숨김(`HideToTray` — `MainWindow.xaml.cs:518`)·백그라운드 재생 상태를 커버한다.**
  `WM_APPCOMMAND`는 포그라운드 창에만 오므로 창이 숨겨진 순간 무력하다. 이 저장소는 **재생 중 트레이
  숨김이 실제 경로**라 이 차이가 결정적이다.
- **최대 함정 = "버튼을 켜고 상태를 갱신하지 않으면 이벤트가 아예 오지 않는다."** `IsEnabled`·
  `IsPlayEnabled`/`IsPauseEnabled`/`IsNextEnabled`/`IsPreviousEnabled`를 켜고 **`PlaybackStatus`를 계속
  갱신**해야 한다. 특히 Play/Pause 토글 키가 Play로 올지 Pause로 올지를 `PlaybackStatus`가 결정한다.
  CI는 이 부류를 하나도 못 잡는다(컴파일은 통과하고 런타임에 조용히 아무 일도 안 일어난다).
- **부수 효과 = 볼륨 OSD 옆 미디어 플라이아웃에 KOTU가 뜬다**(사용자 확인 필요 ①). 원치 않으면
  `DisplayUpdater`를 최소로만 쓰되, 세션 자체는 뜬다 — 그것이 미디어 키를 받는 대가다.

## 1. 현재 구조 (전부 grep 확인)

### 1.1 이을 곳

| 대상 | 위치 | 비고 |
|---|---|---|
| 이웃 파일 이동 | `VideoPlayerView.xaml.cs:710` `MoveToNeighbor(bool forward)` | private · 끝 = 무동작 · Loop list 되감기 |
| 이웃 파일 이동(오디오) | `AudioPlayerView.xaml.cs:804` 동형 | |
| 재생/일시정지 | `VideoPlayerView.xaml.cs:1487` `TogglePlayPause()` / `AudioPlayerView.xaml.cs:1229` | private |
| 이웃 유무 판정 | `UpdateNeighborButtons()`(영상 `:766` 위) — `PrevButton.IsEnabled`/`NextButton.IsEnabled` | SMTC `IsNextEnabled`에 그대로 재사용 가능 |
| 재생 여부 | 영상 `IsPlaying`(`:71`, public — `IPlaybackStateSource`) / 오디오 `IsPlaying`(`:74`, **private**) | |
| 상태 전이 통지 | 영상 `PlaybackStateChanged?.Invoke()` 4곳(`:967,1005,1149,1183`) | **오디오에는 없다**(`AudioPlayerView.xaml.cs:1099` 주석이 명시) |
| 파일 전환 통지 | `ICurrentPathSource.CurrentPathChanged`(로드 앞) · `IContentStateSource.ContentOpened`(로드 완료) | 둘 다 두 뷰 구현 |

### 1.2 셸 쪽 배선 관용구

`MainWindow.ShowModule`이 뷰 생성 직후 계약별로 구독한다 — `MainWindow.xaml.cs:1560~1650`.
모든 블록이 같은 3요소다: ① `if (view is I…)` ② `DispatcherQueue.TryEnqueue` ③
`if (!ReferenceEquals(ModuleHost.Content, view)) return;` 교체 가드. `IPlaybackStateSource` 블록은
`:1629-1634`. **SMTC 배선도 이 자리·이 관용구에 그대로 얹힌다.**
모듈은 셸을 참조하지 못하므로 계약은 `KOTU.Core/Contracts/`에 둔다(셸→뷰 호출 선례 =
`IBrowseOrderConsumer.SetBrowseOrder`, 셸 호출 지점 `MainWindow.xaml.cs:276,1606`).

### 1.3 창 메시지 축

`WindowMinSize`(`src/KOTU.App/WindowMinSize.cs`)가 **메인 창 HWND를 이미 서브클래싱**하고 있다 —
`SetWindowLongPtrW(GWLP_WNDPROC)` + `s_prevProcs` 체인 + `CallWindowProcW` 선행 호출,
`WM_NCDESTROY`에서 사전 제거. 파일 주석이 **"이 서브클래스는 HWND당 1회 규칙이라, 창 메시지 관찰이
필요한 다른 기능도 여기 얹는다"** 고 못 박아 두었다(A185의 `SC_MINIMIZE`가 그 선례였고 A218에서
제거). 부착 지점은 `MainWindow.xaml.cs:325` 한 곳(창 생성 경로가 생성자 하나뿐).
`TrayIcon.cs:265`의 `WndProc`은 **별도 메시지 전용 창**이라 미디어 키와 무관하다.

### 1.4 다중 창·트레이

`WindowManager`는 창마다 `MainWindow` 인스턴스를 만들고(`_windows` MRU · `_ordered`), 각 창이 독립적으로
재생 뷰를 가질 수 있다. `MainWindow.xaml.cs:518` `MinimizeToTrayRequested += HideToTray` — **재생 중 창을
숨기는 경로가 실재**한다. 즉 "포그라운드 창"에 의존하는 방식은 이 저장소의 실사용 형태를 못 덮는다.

### 1.5 LibVLCSharp

`VideoLAN.LibVLC.Windows 3.0.*` + LibVLCSharp — **SMTC 자동 등록은 없다**(WinRT `MediaPlayer`를 쓸 때만
자동 통합이고, 이 저장소는 libvlc를 직접 쓴다). `grep -rn "Windows.Media" src`도 0건. 따라서
SMTC를 쓰려면 **수동 제어(manual control)** 경로 전부를 직접 구현해야 한다.

## 2. 선택지 비교

| 축 | ⓐ SMTC | ⓑ WM_APPCOMMAND(WindowMinSize 체인) | ⓒ XAML 가속기(MediaNextTrack 등) |
|---|---|---|---|
| 포그라운드 필요 | 불필요 | **필수**(최상위 포그라운드 창) | 필수 + 뷰에 포커스 |
| 다른 앱이 활성일 때 | **동작**(세션 소유 시) | 무동작 | 무동작 |
| 트레이 숨김 상태 | **동작** | 무동작(숨은 창은 포그라운드가 될 수 없다) | 무동작 |
| 다른 플레이어와 충돌 | OS가 세션 단위로 중재(마지막에 재생 상태를 올린 세션 우선 — **추정**, 아래 §3-③) | 상대 앱이 SMTC 세션을 쥐면 KOTU엔 아예 안 온다 | 같음 |
| 구현 크기 | 중(새 파일 1 + 계약 1 + 뷰 2곳 상태 갱신) | 소(체인에 case 1개 + 뷰 호출 경로) | 극소(가속기 4줄) |
| CI 위험 | 선례 0 API 집결 — 단 패턴 선례 1건(PrintHost) | P/Invoke 없음(기존 시그니처 재사용) — 거의 0 | 0 |
| 런타임 위험(CI 미검출) | 상태 갱신 누락 → 이벤트 0건 · 창별 세션 충돌 · 해제 순서 버그 | 메시지가 안 올 수 있음(WinUI 이슈 #10711에서 일부 APPCOMMAND 미도달 보고) | 키 자체가 안 옴 |
| 부수 이득 | 미디어 플라이아웃에 제목·아트 표시 · 잠금화면 · 헤드셋 버튼 | 없음 | 없음 |

**결론: ⓐ 단독 채택.** ⓑ는 "SMTC가 실패했을 때의 폴백"으로는 값이 거의 없다(포그라운드에서만 되는데,
그 상황은 사용자가 Space·Ctrl+←/→를 쓰면 되는 상황과 정확히 겹친다). ⓒ는 **비용이 0에 가까우니
보험으로 같이 넣을 수는 있으나**, 넣는다면 SMTC 경로와 **이중 발화**하지 않게 막아야 한다(같은 키
한 번에 두 칸 이동) — 이중 발화 위험 때문에 **넣지 않는 쪽을 권장**한다(확인 필요 ②).

## 3. 위험 (CI가 못 잡는 것 위주)

① **버튼을 안 켜면·상태를 안 갱신하면 이벤트가 0건이다.** 공식 문서: 앱이 쓸 버튼의 `Is…Enabled`를
   켜야 하고, `PlaybackStatus`를 정확히 갱신해야 한다 — "재생/일시정지 토글 키가 Play로 갈지 Pause로
   갈지를 `PlaybackStatus`가 결정한다". Firefox 구현도 `put_IsEnabled(true)` + 버튼별 enable +
   `put_PlaybackStatus`를 모두 한다.
② **`ButtonPressed`는 UI 스레드가 아니다.** 공식 문서가 명시 — UI를 직접 만지면 예외.
   `DispatcherQueue.TryEnqueue`로 마샬해야 한다(저장소의 기존 관용구와 동일).
③ **다른 플레이어와의 우선순위 규칙은 문서로 확정하지 못했다.** MS 공식 문서에는 "마지막으로 재생
   상태를 갱신한 세션이 이긴다"는 규칙 진술이 **없다**. 확인된 것은 (ⅰ) SMTC UI에 세션별 탭이 생기고
   **선택된 세션**이 조작 대상이라는 것 (ⅱ) 커뮤니티 답변 수준에서 "포커스된 앱으로 간다"는 상반된
   설명도 돈다는 것뿐이다. **"마지막 재생 세션 우선"은 추정으로 취급하고, 실기기 확인 포인트로 남긴다.**
④ **창이 여럿이면 세션도 여럿이 된다.** `GetForWindow`는 창 단위다(문서: *appWindow*는 호출 프로세스
   소유의 최상위 창이어야 한다). 창 2개에서 각각 영상을 틀면 SMTC 세션 2개가 생기고, 키가 어디로
   갈지는 OS가 정한다. **완화안 = Firefox 방식(프로세스당 숨김 창 1개로 세션 1개만 만들고, 그 세션을
   "지금 재생 중인 뷰"에 라우팅)** — 다만 이는 저장소에 없는 새 창 클래스 등록이 필요해 규모가 커진다.
   **1차 권장 = 창당 1세션(단순)** + 실기기에서 다중 창 동작을 확인(확인 필요 ③).
⑤ **해제 순서 버그(Firefox 주석에 실제 관측으로 기록).** 메타데이터·버튼을 **먼저** 비활성 컨트롤보다
   앞서 만지고 그것이 같은 태스크에서 순차적으로 끝나지 않으면, SMTC가 완전히 정리되지 않고
   **실행 파일 이름이 그대로 남아 보인다**. 정리는 `PlaybackStatus = Closed/Stopped` → `ClearAll` →
   `IsEnabled = false` 순서로, 한 흐름에서 끝낼 것.
⑥ **비패키지 앱의 표시 이름·아이콘.** AUMID가 없으므로 플라이아웃에 무엇이 표시될지는 OS가 exe에서
   유추한다. 저장소에는 `TaskbarIdentity`(`src/KOTU.App/Integration/TaskbarIdentity.cs`)가 이미 있어
   AUMID 축이 존재한다 — SMTC 표시가 이상하면 여기와 함께 봐야 한다(실기기 확인).
⑦ **오디오 뷰에는 재생 상태 통지가 없다**(`AudioPlayerView.xaml.cs:1099`가 명시). SMTC `PlaybackStatus`
   갱신을 하려면 오디오에도 상태 전이 통지가 필요하다 — **A186 확대(오디오도 `IPlaybackStateSource`)**
   가 사실상 이 배치의 선행 조건이 된다(확인 필요 ④).
⑧ **`WM_APPCOMMAND`를 쓸 경우의 반환 규칙**(참고용): 처리했으면 **TRUE(=1)를 반환**해야 한다(문서 명시 —
   "다른 메시지와 달리"). `WindowMinSize.Hook`은 지금 `CallWindowProcW` 결과를 그대로 돌려주므로,
   이 메시지만 `return (IntPtr)1;`로 갈라야 한다. `GET_APPCOMMAND_LPARAM` = `(short)(HIWORD(lParam) & ~0xF000)`
   — 상수: NEXTTRACK 11 · PREVIOUSTRACK 12 · STOP 13 · PLAY_PAUSE 14 · PLAY 46 · PAUSE 47,
   `WM_APPCOMMAND` = 0x0319.

## 4. 배치 3 사양 초안 (권장안 = SMTC)

### 4.1 Core 계약 (신설 1개)

```
KOTU.Core/Contracts/IMediaTransportTarget.cs
    bool CanPrevious { get; }   // UpdateNeighborButtons와 같은 판정
    bool CanNext { get; }
    bool IsPlaying { get; }     // 오디오는 private IsPlaying을 public으로
    void Previous();            // MoveToNeighbor(false)
    void Next();                // MoveToNeighbor(true)
    void TogglePlay();          // TogglePlayPause()
    void Play();  void Pause(); // SMTC는 Play/Pause를 별개 버튼으로도 보낸다
    void Stop();                // 선택 — 없으면 Pause로 접는다
    event Action? TransportStateChanged;  // 상태·이웃 유무가 갈릴 때 1회
```
구현 = `VideoPlayerView`·`AudioPlayerView`(+ `AllReadableView`가 자식 중계 — `IPlaybackStateSource`와
같은 방식, `AllReadableView.xaml.cs:212,246,286` 관용구).

### 4.2 셸 쪽 (신규 파일 1개 · 창당 1개)

`src/KOTU.App/Integration/MediaTransport.cs` — `PrintHost`와 같은 위상(선례 0 API 집결지 · 창당 1개 ·
지연 생성 · `Closed`에서 전수 해제). 골자:

```
using Windows.Media;                       // SystemMediaTransportControls, MediaPlaybackStatus
_hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
_smtc = SystemMediaTransportControlsInterop.GetForWindow(_hwnd);   // 반환 = SystemMediaTransportControls
_smtc.IsEnabled = true;
_smtc.IsPlayEnabled = _smtc.IsPauseEnabled = _smtc.IsStopEnabled = true;
_smtc.IsNextEnabled = target.CanNext;  _smtc.IsPreviousEnabled = target.CanPrevious;
_smtc.ButtonPressed += OnButtonPressed;      // UI 스레드 아님 → TryEnqueue
_smtc.DisplayUpdater.Type = MediaPlaybackType.Video;  // 또는 Music
_smtc.DisplayUpdater.Update();
_smtc.PlaybackStatus = MediaPlaybackStatus.Playing / Paused / Stopped / Closed;
```
- **생성 시점** = 재생 뷰(`IMediaTransportTarget`)가 `ShowModule`로 올라올 때 지연 생성.
  재생 뷰가 아닌 모듈로 갈아타면 `PlaybackStatus = Closed` + `IsEnabled = false`로 세션을 접는다.
- **상태 갱신 시점** = `TransportStateChanged`(재생/일시정지/정지) + `ContentOpened`(파일 전환 —
  제목 갱신) + `CurrentPathChanged`는 쓰지 않는다(로드 앞 통지라 제목이 앞서 튄다).
- **`DisplayUpdater`** = 최소한 `Type`은 반드시 세운다(문서: 다른 메타데이터를 안 주더라도 `Type`은
  설정하라 — 화면 보호기 억제 등에 쓰인다). 제목은 파일 이름만(확인 필요 ①에 따라 결정).
- **해제** = 창 `Closed`(관용구는 `MainWindow.xaml.cs:342,520` 등) + 뷰 교체 시. 순서는 §3-⑤ 그대로.
- **UI 스레드** = `ButtonPressed` 핸들러는 값만 읽고 `DispatcherQueue.TryEnqueue`로 넘긴 뒤,
  넘긴 쪽에서 `ReferenceEquals(ModuleHost.Content, view)` 교체 가드를 다시 본다.

### 4.3 버튼 매핑

| `SystemMediaTransportControlsButton` | 동작 |
|---|---|
| `Next` | `MoveToNeighbor(true)` |
| `Previous` | `MoveToNeighbor(false)` |
| `Play` | 재생 중이 아니면 재생(`TogglePlay`의 재생 분기) |
| `Pause` | 재생 중이면 일시정지 |
| `Stop` | 일시정지로 접는다(저장소에 "정지" 개념이 따로 없다) |

### 4.4 실기기 확인 포인트 (CI가 못 잡는 전부)

1. KOTU가 포그라운드일 때 Next/Prev 키가 이웃 파일로 이동하는가.
2. **다른 앱을 활성화한 채로** 눌렀을 때 동작하는가(§3-③ 추정의 검증).
3. **브라우저·Spotify가 동시에 재생 중일 때** 키가 어디로 가는가 — 마지막 재생 세션 우선이 맞는가.
4. **트레이 숨김 상태**에서 동작하는가.
5. 볼륨 OSD 옆 미디어 플라이아웃에 KOTU가 뜨는가 · 표시 이름/아이콘이 어떻게 나오는가(§3-⑥).
6. 창 2개에서 각각 재생 중일 때 키가 어느 창으로 가는가(§3-④).
7. 재생 뷰를 닫고 다른 모듈로 간 뒤에도 플라이아웃에 KOTU가 남는가(§3-⑤ 해제 순서 버그).
8. Play/Pause 키가 상태에 맞게(재생 중이면 일시정지) 오는가(§3-① `PlaybackStatus` 검증).

## 5. 선례 0건 API 목록 (CI 1순위 후보) · 최소 복구법

| API | 선례 | 최소 복구법 |
|---|---|---|
| `Windows.Media.SystemMediaTransportControlsInterop.GetForWindow` | 0 — 단 **동형 패턴 1건**: `Windows.Graphics.Printing.PrintManagerInterop.GetForWindow`(`PrintHost.cs:137`). 같은 MS 상호운용 클래스 표·같은 TFM 조건 | 파일 삭제 |
| `Windows.Media.SystemMediaTransportControls` / `…ButtonPressedEventArgs` / `…Button` | 0 | 파일 삭제 |
| `Windows.Media.MediaPlaybackStatus` · `MediaPlaybackType` | 0 | 파일 삭제 |
| `SystemMediaTransportControlsDisplayUpdater` | 0 | `DisplayUpdater` 사용 줄만 제거(핵심 기능은 유지) |

**최소 복구 = `MediaTransport.cs` 1파일 삭제 + `MainWindow.ShowModule`의 배선 블록 1개 제거 + Core 계약
파일 유지**(계약은 BCL 전용이라 남아도 안전 — A211이 같은 구조로 정리해 둔 선례).
`using Windows.Media;`가 `net8.0-windows10.0.19041.0`에서 해석되지 않으면(가장 유력한 CI 실패 모드)
그때는 **경로 자체가 성립하지 않는 것**이므로 ⓑ/ⓒ 재검토로 돌아간다.

## 6. 착수 전 확인 필요

① **미디어 플라이아웃 표시** — SMTC를 쓰면 볼륨 OSD 옆 미디어 컨트롤에 KOTU가 뜨고, 잠금화면에도
   노출된다. 제목·아트를 채워 "제대로 보이게" 할 것인가, `Type`만 세우고 최소로 둘 것인가.
② **XAML 가속기(MediaNextTrack 등) 보험을 같이 넣을 것인가** — 비용은 4줄이지만 SMTC와 이중 발화
   위험이 있다. 권장 = 넣지 않음.
③ **다중 창 세션 정책** — 창당 1세션(단순·권장) vs 프로세스당 1세션(Firefox식 숨김 창, 규모 +중).
④ **오디오 뷰의 재생 상태 통지 신설**(A186 확대) — SMTC `PlaybackStatus` 갱신의 선행 조건.
   이번 배치에 포함할 것인가, 별도 항목으로 뺄 것인가.

## 출처

- WM_APPCOMMAND (상수·`GET_APPCOMMAND_LPARAM`·TRUE 반환 규칙) — https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-appcommand
- Call interop APIs from a .NET app (C# 상호운용 클래스 표 — `Windows.Media` **SystemMediaTransportControlsInterop**,
  `Windows.Graphics.Printing` **PrintManagerInterop**, TFM 조건) — https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-com-interop-csharp
- ISystemMediaTransportControlsInterop::GetForWindow (appWindow = 호출 프로세스의 최상위 창) — https://learn.microsoft.com/en-us/windows/win32/api/systemmediatransportcontrolsinterop/nf-systemmediatransportcontrolsinterop-isystemmediatransportcontrolsinterop-getforwindow
- Manual control of the System Media Transport Controls (버튼 enable · `PlaybackStatus` · `ButtonPressed`는
  UI 스레드 아님 · `DisplayUpdater` · `Type` 필수) — https://learn.microsoft.com/en-us/windows/apps/develop/media-playback/system-media-transport-controls
- Integrate with the SMTC (세션 탭 · 선택된 세션이 조작 대상) — https://learn.microsoft.com/en-us/windows/apps/develop/media-playback/integrate-with-systemmediatransportcontrols
- WinUI 3에서 WM_APPCOMMAND 일부 미도달 보고 — https://github.com/microsoft/microsoft-ui-xaml/issues/10711
- 비패키지에서 SMTC 사용 가능 여부(“UWP/WinUI 전용 API가 아니다 — Edge Chromium도 쓴다”) — https://github.com/microsoft/microsoft-ui-xaml/issues/2631
- Firefox `WindowsSMTCProvider.cpp` (숨김 창 + `GetForWindow` · `put_IsEnabled` · 버튼별 enable ·
  `put_PlaybackStatus` · **해제 순서를 어기면 실행 파일 이름이 남는 관측**) — https://hg-edge.mozilla.org/projects/elm/file/e32be078a857209848342ff1e7f497d5b832fd7a/widget/windows/WindowsSMTCProvider.cpp
- 미디어 키가 포그라운드 창의 WM_APPCOMMAND로 먼저 가고 그 뒤 WM_KEYDOWN이 포스트된다는 설명 — https://bugzilla.mozilla.org/show_bug.cgi?id=865561
