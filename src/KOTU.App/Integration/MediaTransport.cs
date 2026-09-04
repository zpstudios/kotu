using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Media;
using Windows.Storage;
using KOTU.Core.Contracts;

namespace KOTU.App.Integration;

/// <summary>
/// 창당 1개 시스템 미디어 컨트롤(SMTC) 호스트 (A349 배치 3 — 사양 = docs/A349-media-keys-research.md).
/// 키보드 미디어 키(Play/Pause·Stop·Previous/Next Track)·헤드셋 버튼·볼륨 OSD 옆 미디어
/// 플라이아웃이 여기로 들어와 활성 재생 뷰(<see cref="IMediaTransportTarget"/>)를 조작한다.
///
/// 왜 SMTC인가(조사 §2): 미디어 키는 XAML KeyDown/가속기로 온다는 보장이 없고
/// (OS가 포그라운드 창의 WM_APPCOMMAND로 먼저 보낸다), WM_APPCOMMAND는 포그라운드 전용이라
/// 이 저장소의 실사용 경로인 <b>재생 중 트레이 숨김</b>(MainWindow HideToTray)을 못 덮는다.
/// SMTC만이 창 가시성·포커스와 무관하게 동작한다.
///
/// ⚠️ 저장소 SMTC API 선례 0 — 이 파일이 선례 0 API의 유일한 집결지다(PrintHost와 같은 위상).
/// CI가 여기서 깨지면 최소 복구 = ① 이 파일 삭제 ② MainWindow.xaml.cs의 "A349 배치 3" 표식
/// 3곳 제거(_mediaTransport 절 · ShowModule의 Attach/Detach 블록 · OnContentOpened 1줄).
/// Core 계약(IMediaTransportTarget)과 두 뷰의 구현은 BCL 전용이라 남아도 안전하다
/// (A211이 같은 구조로 정리해 둔 선례).
///
/// 수명(창 단위 1개):
/// - 생성 = 재생 뷰가 처음 올라올 때(MainWindow가 지연 생성) — 시작 경로에서 SMTC를 건드리지 않는다.
///   OS 배선(GetForWindow)은 한 걸음 더 늦다: 세션을 실제로 켜는 첫 순간에 한다.
/// - GetForWindow·ButtonPressed 구독은 창당 <b>1회</b>(_registered 가드). 뷰 교체마다 다시
///   부르지 않는다 — 대상 부착/해제는 <see cref="Attach"/>/<see cref="Detach"/>가 하고,
///   세션의 켬/끔(IsEnabled)은 <see cref="Refresh"/>가 대상의 HasMediaTransport로 정한다.
/// - <b>부착과 세션 켜짐은 다르다</b>: All Readable은 문서·사진 자식을 얹고도 계약을 구현하므로
///   부착은 되지만 세션은 꺼져 있어야 한다(안 그러면 PDF를 열어도 미디어 플라이아웃에 KOTU가
///   뜨고, Play/Pause는 먹통이며, 다른 플레이어의 미디어 키를 빼앗는다).
/// - 해제는 창 Closed에서 스스로(PrintHost와 같은 자기완결 — MainWindow에 해제 코드를 남기지 않는다).
///
/// 최대 함정 두 가지(조사 §3 — CI가 하나도 못 잡는다):
/// ① <b>버튼을 켜고 PlaybackStatus를 갱신하지 않으면 이벤트가 0건이다.</b> 특히 Play/Pause
///    토글 키가 Play로 올지 Pause로 올지를 PlaybackStatus가 정한다 — 그래서 부착 직후와
///    모든 재생 전이에서 <see cref="Refresh"/>를 부른다.
/// ② <b>해제 순서</b>(§3-⑤ — Firefox 실관측): PlaybackStatus = Closed → ClearAll →
///    IsEnabled = false 순서를 한 흐름에서 끝내지 않으면 플라이아웃에 실행 파일 이름이 남는다.
///
/// 스레드: 셸에서 오는 호출(Attach/Detach/OnContentOpened)은 전부 UI 스레드이고, SMTC 속성
/// 대입도 전부 UI 스레드에서만 한다. 예외는 <see cref="OnButtonPressed"/> 하나 —
/// SMTC 이벤트는 UI 스레드가 아니므로(§3-②) 거기서는 args만 읽고 디스패처로 넘긴다.
/// 뷰 계약 이벤트(PlaybackStateChanged·NeighborsChanged)도 UI 스레드 보장이 없어 같은 규칙이다.
///
/// 실패 흡수: GetForWindow·속성 대입이 던지면 전부 삼키고 _smtc를 null로 되돌린다
/// (PrintHost의 부분 롤백 관용구) — SMTC가 없는 환경에서도 앱은 그대로 돌아야 한다.
/// </summary>
internal sealed class MediaTransport
{
    private readonly Window _window;
    private readonly DispatcherQueue _dispatcher;
    private readonly IntPtr _hwnd; // 창 수명 동안 불변 — GetForWindow 전용(PrintHost와 같은 관용구)

    private SystemMediaTransportControls? _smtc;
    private bool _registered; // 창당 1회 배선 가드 — GetForWindow·ButtonPressed 재구독 금지
    private bool _disposed;

    /// <summary>지금 조작 대상인 재생 뷰. null = 부착된 세션 없음(디스패치된 콜백의 유일한 가드).</summary>
    private IMediaTransportTarget? _target;

    /// <summary>지금 표시 중인 파일 경로 — 비동기 메타데이터 갱신의 낡음 방어 기준.</summary>
    private string? _currentPath;

    /// <summary>
    /// 지금 SMTC 세션이 <b>켜져 있는가</b>(IsEnabled = true로 플라이아웃에 노출된 상태).
    /// 부착 여부(<c>_target</c>)와 다르다 — All Readable은 문서·사진 자식을 얹고도 계약을
    /// 구현해 부착되지만, 그 동안 세션은 꺼져 있어야 한다(<see cref="Refresh"/>가
    /// <see cref="IMediaTransportTarget.HasMediaTransport"/>로 정한다).
    /// 이 필드는 켜짐/꺼짐 <b>전이</b>에서만 SMTC 속성을 만지게 하는 문지기다 —
    /// Refresh는 재생 전이마다 불려서, 매번 IsEnabled·버튼 활성을 되풀이 대입하면 낭비다.
    /// </summary>
    private bool _sessionActive;

    internal MediaTransport(Window window, DispatcherQueue dispatcher)
    {
        _window = window;
        _dispatcher = dispatcher;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        // 수명 자기완결: 창이 닫히면 스스로 세션을 접는다(PrintHost와 같은 형).
        window.Closed += OnWindowClosed;
    }

    /// <summary>
    /// 재생 뷰가 화면에 올라왔다 — SMTC 세션 대상을 이 뷰로 삼는다(UI 스레드 전용).
    /// 이미 다른 뷰가 붙어 있으면 먼저 뗀다. 등록 실패(SMTC 미지원·예외)면 조용히 무동작이다.
    /// <para>
    /// <b>부착 ≠ 세션 켜짐</b>: 실제로 플라이아웃에 노출할지는 <see cref="Refresh"/>가
    /// <see cref="IMediaTransportTarget.HasMediaTransport"/>로 정한다. All Readable은 PDF·텍스트·
    /// 사진 자식을 얹고도 이 계약을 구현하므로, 여기서 무조건 켜면 문서를 열어도 미디어
    /// 플라이아웃에 KOTU가 뜨고 다른 플레이어의 미디어 키를 빼앗는다.
    /// 구독은 켜짐 여부와 무관하게 유지한다 — 자식이 오디오로 갈리면 NeighborsChanged가 와야
    /// 세션을 다시 켤 수 있다.
    /// </para>
    /// </summary>
    internal void Attach(IMediaTransportTarget target, string? filePath)
    {
        if (_disposed) return;
        if (_target is not null) Detach();
        // SMTC 배선(GetForWindow)은 여기서 하지 않는다 — 세션을 실제로 켤 때(Refresh의 활성 경로)
        // 처음 한다. 문서 자식을 얹은 All Readable로 들어온 경우 OS 쪽을 아예 건드리지 않는다.
        _target = target;
        _currentPath = filePath;
        target.PlaybackStateChanged += OnTargetStateChanged;
        target.NeighborsChanged += OnTargetStateChanged;
        Refresh(); // 켤지 말지·재생 상태·이전/다음 활성·첫 표시까지 전부 여기서 정한다
    }

    /// <summary>
    /// 부착을 끊는다 — 뷰 교체·비재생 모듈 전환·창 닫힘 공용(UI 스레드 전용, 미부착이면 무동작).
    /// 세션 접기는 <see cref="DeactivateSession"/>(함정 ② 순서)에 맡기고, 여기서는 그 뒤의
    /// 구독 해제·필드 정리만 한다. Refresh의 "자격 없음" 접기와 달리 <b>구독까지</b> 끊는 쪽이다.
    /// </summary>
    internal void Detach()
    {
        if (_target is not { } target) return;
        DeactivateSession();
        target.PlaybackStateChanged -= OnTargetStateChanged;
        target.NeighborsChanged -= OnTargetStateChanged;
        _target = null;
        _currentPath = null;
    }

    /// <summary>
    /// 열려 있는 파일이 갈렸다(셸 OnContentOpened — 뷰 내부 ⏮/⏭·오토 넥스트·미디어 키 이동 포함).
    /// 플라이아웃 제목·아트만 갱신한다. 로드 완료 통지라 표시가 실제 재생보다 앞서지 않는다.
    /// 세션이 꺼져 있으면(All Readable이 문서·사진 자식을 얹은 동안) 통째로 버린다 — 그 파일은
    /// 미디어가 아니라 기억해 둘 값도 아니다. 세션이 켜지는 계기는 재생 자식의 등장이고,
    /// 그 자식이 로드를 마치며 보내는 ContentOpened가 곧 이 경로를 채운다.
    /// </summary>
    internal void OnContentOpened(string path)
    {
        if (_disposed || _target is null || path.Length == 0 || !_sessionActive) return;
        _currentPath = path;
        _ = UpdateDisplayAsync(path);
    }

    /// <summary>
    /// SMTC 배선 1회 수행(이미 됐으면 true 즉시). 실패 시 부분 배선을 되감아 다음 시도가
    /// 처음부터 다시 가게 한다(PrintHost.TryEnsureRegistered와 같은 형).
    /// </summary>
    private bool TryEnsureRegistered()
    {
        if (_registered) return true;
        try
        {
            // 창 핸들 기반 상호운용 — PrintManagerInterop.GetForWindow(PrintHost.cs)와 같은
            // MS 상호운용 클래스 표의 같은 방식이다(조사 §5. 비패키지에서도 성립).
            _smtc = SystemMediaTransportControlsInterop.GetForWindow(_hwnd);
            _smtc.ButtonPressed += OnButtonPressed;
            _registered = true;
            return true;
        }
        catch
        {
            _smtc = null;
            return false;
        }
    }

    /// <summary>
    /// 함정 ①의 실행부이자 <b>세션 켬/끔의 유일한 판정점</b>(UI 스레드 전용).
    /// 부착 직후와 모든 재생 전이·이웃 변화(= 자식 교체 포함)에서 불린다.
    /// <list type="number">
    /// <item>대상이 지금 미디어 키를 받을 자격이 없으면(All Readable이 문서·사진 자식을 얹은
    /// 동안) 세션을 접는다 — 부착·구독은 그대로 두므로 자식이 오디오로 갈리는 순간
    /// NeighborsChanged가 와서 다시 켜진다.</item>
    /// <item>자격이 생겼는데 꺼져 있었으면 켠다(버튼 활성 + 지금 파일 표시 재적용).</item>
    /// <item>켜진 동안에는 재생 상태·이전/다음 활성만 갱신한다 — 저장소에 "정지" 상태가 없어
    /// 재생 중이 아니면 전부 Paused다(Stopped를 쓰면 플라이아웃이 조작 불가로 굳는다).</item>
    /// </list>
    /// </summary>
    private void Refresh()
    {
        if (_disposed || _target is not { } target) return;
        if (!target.HasMediaTransport)
        {
            DeactivateSession();
            // 꺼져 있는 동안의 경로는 미디어가 아니다(문서·사진 자식) — 남겨 두면 나중에 세션을
            // 켤 때 그 파일 이름이 플라이아웃 제목으로 잠깐 뜬다. 새 제목은 재생 자식이 로드를
            // 마치며 보내는 ContentOpened가 채운다.
            _currentPath = null;
            return;
        }
        // 여기부터가 "미디어 키를 받을 자격이 있는 대상" — 이 시점에 처음 SMTC를 배선한다.
        if (!TryEnsureRegistered() || _smtc is not { } smtc) return;
        if (!_sessionActive && !TryActivateSession(smtc)) return;
        try
        {
            smtc.PlaybackStatus = target.IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            smtc.IsNextEnabled = target.CanNext;
            smtc.IsPreviousEnabled = target.CanPrevious;
        }
        catch { /* 세션이 이미 접힌 뒤의 늦은 도착 — 버린다 */ }
    }

    /// <summary>
    /// 세션을 켠다(꺼져 있을 때만 불린다 — UI 스레드 전용). 함정 ①: 쓸 버튼을 전부 켜야
    /// 이벤트가 온다. 이전/다음 활성은 곧바로 <see cref="Refresh"/>가 갈 곳 유무로 다시 정한다.
    /// 켜자마자 아는 경로가 있으면 표시도 밀어 넣는다(접을 때 ClearAll로 비워 두기 때문).
    /// 자식 교체로 켜지는 경우에는 경로가 아직 없다 — 재생 자식의 ContentOpened가 곧 채운다.
    /// 실패하면 false를 돌려 세션을 꺼진 채로 둔다(부착은 유지 — 다음 전이에서 다시 시도한다).
    /// </summary>
    private bool TryActivateSession(SystemMediaTransportControls smtc)
    {
        try
        {
            smtc.IsEnabled = true;
            smtc.IsPlayEnabled = true;
            smtc.IsPauseEnabled = true;
            smtc.IsStopEnabled = true;
        }
        catch
        {
            return false; // 반쪽 켜짐 금지 — 꺼진 것으로 친다
        }
        _sessionActive = true;
        if (_currentPath is { Length: > 0 } path) _ = UpdateDisplayAsync(path);
        return true;
    }

    /// <summary>
    /// 세션을 접는다(이미 꺼져 있으면 무동작 — UI 스레드 전용). 뷰 교체·창 닫힘(<see cref="Detach"/>)과
    /// "자격 없는 자식으로 갈림"(<see cref="Refresh"/>)이 <b>같은 순서</b>를 쓰게 뽑아 둔 헬퍼다.
    /// 순서 엄수(함정 ② — 조사 §3-⑤): PlaybackStatus = Closed → ClearAll → Update → IsEnabled = false.
    /// 이 순서를 어기면 플라이아웃에 KOTU가 남는다. 구독은 여기서 건드리지 않는다.
    /// </summary>
    private void DeactivateSession()
    {
        if (!_sessionActive) return;
        _sessionActive = false;
        if (_smtc is not { } smtc) return;
        try
        {
            smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
            smtc.DisplayUpdater.ClearAll();
            smtc.DisplayUpdater.Update();
            smtc.IsEnabled = false;
        }
        catch { /* OS 쪽이 먼저 무너진 경우 — 필드 정리만으로 마감 */ }
    }

    /// <summary>
    /// 뷰의 재생 전이·이웃 변화 통지(UI 스레드 보장 없음 — 계약 규칙) → UI 스레드에서 Refresh.
    /// 이미 UI 스레드면 곧바로 반영해 키 조작과 같은 틱에 상태가 맞는다.
    /// </summary>
    private void OnTargetStateChanged()
    {
        if (_dispatcher.HasThreadAccess) Refresh();
        else _dispatcher.TryEnqueue(Refresh);
    }

    /// <summary>
    /// 플라이아웃 표시 갱신(제목·아트 — 사용자 확정 ①). 태그·앨범아트를 파일에서 그대로 읽고
    /// (CopyFromFileAsync), 실패하거나 제목이 비면 파일 이름으로 채운다. Type은 어떤 경로로든
    /// 반드시 선다(문서 규칙 — 화면 보호기 억제 등에 쓰인다).
    /// 낡음 방어: 이 await 사이에 다음 파일로 넘어갔을 수 있어 완료 시점에 경로를 다시 대조한다.
    /// </summary>
    private async Task UpdateDisplayAsync(string path)
    {
        if (_smtc is not { } smtc || _target is not { } target) return;
        // 영상 표면이면 Video, 그 밖(오디오·All Readable의 비영상 자식)은 Music.
        var type = target.HasPlaybackSurface ? MediaPlaybackType.Video : MediaPlaybackType.Music;
        var updater = smtc.DisplayUpdater;
        var copied = false;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            copied = await updater.CopyFromFileAsync(type, file);
        }
        catch
        {
            copied = false; // 접근 불가·형식 미지원 — 아래 파일 이름 폴백
        }
        if (!ReferenceEquals(_target, target) || _currentPath != path) return; // 그새 갈렸다

        var name = Path.GetFileName(path);
        try
        {
            if (!copied)
            {
                updater.ClearAll();
                updater.Type = type;
            }
            if (type == MediaPlaybackType.Video)
            {
                if (!copied || string.IsNullOrWhiteSpace(updater.VideoProperties.Title))
                    updater.VideoProperties.Title = name;
            }
            else
            {
                if (!copied || string.IsNullOrWhiteSpace(updater.MusicProperties.Title))
                    updater.MusicProperties.Title = name;
            }
            updater.Update();
        }
        catch { /* 표시는 최선 노력 — 실패해도 미디어 키는 그대로 동작한다 */ }
    }

    /// <summary>
    /// 미디어 키·플라이아웃 버튼 — <b>UI 스레드가 아니다</b>(조사 §3-②). 여기서는 args만 읽고
    /// 디스패처로 넘긴다(SMTC 속성도, 뷰도 만지지 않는다 — 만지면 예외로 프로세스가 죽는다).
    /// 넘어간 쪽은 <c>_target</c> 필드를 그 시점에 다시 읽는다 — Attach 시점 값을 캡처하면
    /// 이미 내려간 옛 뷰를 조작하게 된다(Detach가 null로 만드는 것이 유일한 교체 가드다).
    /// </summary>
    private void OnButtonPressed(SystemMediaTransportControls sender,
        SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        try
        {
            var button = args.Button;
            _dispatcher.TryEnqueue(() =>
            {
                if (_disposed || _target is not { } target) return;
                switch (button)
                {
                    case SystemMediaTransportControlsButton.Next:
                        target.Next();
                        break;
                    case SystemMediaTransportControlsButton.Previous:
                        target.Previous();
                        break;
                    case SystemMediaTransportControlsButton.Play:
                        target.Play();
                        break;
                    // 정지는 일시정지로 접는다(저장소에 "정지" 개념이 따로 없다 — 조사 §4.3).
                    case SystemMediaTransportControlsButton.Pause:
                    case SystemMediaTransportControlsButton.Stop:
                        target.Pause();
                        break;
                    default:
                        return; // 그 밖(Record·FastForward·Rewind 등)은 켜지 않았다
                }
                Refresh();
            });
        }
        catch { /* 비UI 스레드 — 새면 프로세스가 죽는다 */ }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_disposed) return;
        _disposed = true;
        _window.Closed -= OnWindowClosed;
        Detach();
        try { if (_smtc is { } smtc) smtc.ButtonPressed -= OnButtonPressed; }
        catch { /* 프로세스 종료 경로 — 무시 */ }
        _smtc = null;
        _registered = false;
    }
}
