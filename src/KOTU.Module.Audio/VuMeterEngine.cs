using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace KOTU.Module.Audio;

/// <summary>채널별 표시 레벨(0..1). RMS = 막대 채움, Peak = 피크 홀드 위치 — dBFS 매핑·감쇠를
/// 이미 거친 값이라 뷰는 트랙 폭 곱셈만 한다.</summary>
internal readonly record struct VuLevels(double RmsL, double RmsR, double PeakL, double PeakR);

/// <summary>
/// A304: 자체 렌더 VU 미터의 레벨 엔진 — WASAPI 루프백 캡처(NAudio.Wasapi) + 표시 타이머.
///
/// <b>경로 선택 근거(1단계 조사 확정)</b>:
///  · libvlc SetAudioCallbacks/SetAudioFormat(후보 ⓐ)은 기각 — libvlc 3의 오디오 콜백
///    (libvlc_audio_set_callbacks)은 aout 모듈 자체를 대체한다: 콜백을 걸면 libvlc가 장치로
///    소리를 내보내지 않고 디코드된 PCM을 앱에 넘길 뿐이라, 출력까지 앱이 WASAPI로 직접
///    책임져야 한다(청취 전용 탭 모드 없음). "소리 유지 최우선" 기준으로 탈락.
///  · WASAPI 루프백(후보 ⓑ)을 채택 — 렌더 장치의 출력 믹스를 그대로 캡처한다(재생 경로
///    무접촉 = 소리 유지). 의미상 "앱 소리"가 아니라 "시스템 소리"다: 다른 앱 소리도 미터에
///    섞인다(수용 — 구현 시 결정. 캡처는 재생 중에만 돌아 유휴 시 오검출은 없다).
///    P/Invoke 직접 구현 대신 NAudio.Wasapi 2.2.1(버전 고정)을 쓴다 — IAudioClient 루프백
///    COM 인터롭 수백 줄을 CI 한 방에 통과시키는 것보다 검증된 래퍼가 싸다.
///
/// <b>스레드 지도(주기 작업 UI 스레드 금지 — A278 관용구)</b>:
///  · NAudio 캡처 스레드: DataAvailable → 버퍼의 채널별 RMS·피크(선형)만 계산해 필드에 적는다.
///  · 스레드풀 타이머(40ms ≈ 25fps): dBFS 매핑·릴리스 감쇠·피크 홀드 계산 후 렌더 콜백 호출.
///  · UI 스레드: 뷰가 콜백을 Dispatch로 감싸 속성 대입만 한다(이 클래스는 UI를 모른다).
///  · 시작·정리(장치 열기·캡처 해제)는 Task.Run — 장치가 느려도 UI가 멎지 않는다.
///
/// <b>수명</b>: Start/Stop 몇 번이든 안전(세대 번호가 비동기 초기화와 정지의 경합을 정리),
/// Dispose = Stop + 타이머 해제. 캡처 실패(장치 없음·권한·루프백 미지원)는 전부 조용히 삼켜
/// 빈 미터로 남는다(크래시 금지 — 타이머는 0 레벨만 흘린다). 장치가 재생 중 뽑히면 NAudio가
/// RecordingStopped를 올려 캡처가 정리되고, 레벨은 신선도(StaleMs) 규칙으로 0에 수렴한다.
/// </summary>
internal sealed class VuMeterEngine : IDisposable
{
    private const int TickMs = 40;        // 25fps — 사양 20~30fps 구간(60fps 불요)
    private const double FloorDb = 48;    // 미터 하한 -48 dBFS(그 이하 = 0) — 바 미터 관례 범위
    private const double RmsRelease = 0.055;  // 틱당 릴리스(풀스케일 낙하 약 0.7초) — 어택은 즉시
    private const double PeakRelease = 0.045; // 피크 홀드 만료 후 틱당 감쇠
    private const int PeakHoldMs = 700;   // 피크 홀드 유지("짧게 유지 후 감쇠" — 구현 시 결정)
    private const int StaleMs = 250;      // 이보다 오래 데이터 없음 = 무음(WASAPI 루프백은 무음 구간에 패킷이 안 올 수 있다)

    private readonly Action<VuLevels> _render; // 타이머 스레드에서 호출 — 뷰가 디스패치로 감싼다
    private readonly object _sync = new();
    private readonly Timer _timer;             // System.Threading.Timer — 스레드풀 콜백
    private WasapiLoopbackCapture? _capture;
    private int _generation;      // Start마다 증가 — 뒤늦게 끝난 비동기 초기화가 스스로를 버리게
    private volatile bool _running;
    private int _inTick;          // 타이머 재진입 가드(0/1)

    // 캡처 스레드가 쓰고 타이머가 읽는 원시 레벨(선형 0..1). 32비트 단일 쓰기라 원자적이고,
    // 최대 한 틱(40ms) 낡은 값은 표시상 무해하다 — 락 불요.
    private float _rawRmsL, _rawRmsR, _rawPeakL, _rawPeakR;
    private long _lastDataTicks;  // Environment.TickCount64 — 신선도 판정(Volatile 접근)

    // 표시 상태 — 타이머 스레드 전용(_inTick 게이트로 동시 틱 없음. Stop의 0 리셋과의 경합은
    // 마지막 틱 1회의 잔상뿐이고 그마저 뷰의 _vuActive 게이트가 걸러낸다. x64 전용 빌드라
    // double 쓰기도 원자적이다).
    private double _dispRmsL, _dispRmsR, _dispPeakL, _dispPeakR;
    private long _peakHoldL, _peakHoldR;

    public VuMeterEngine(Action<VuLevels> render)
    {
        _render = render;
        _timer = new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// 캡처·타이머 시작(이미 돌고 있으면 무동작). outputDeviceId = A164 저장값(libvlc mmdevice
    /// 장치 ID — WASAPI 엔드포인트 ID와 같은 표기라 그대로 조회에 쓴다). 빈 값·조회 실패는
    /// 시스템 기본 렌더 장치로 폴백. 장치 열기는 Task.Run — 실패는 조용히 빈 미터.
    /// </summary>
    public void Start(string outputDeviceId)
    {
        int generation;
        lock (_sync)
        {
            if (_running) return;
            _running = true;
            generation = ++_generation;
        }
        _timer.Change(TickMs, TickMs);
        _ = Task.Run(() => InitCapture(generation, outputDeviceId));
    }

    /// <summary>캡처·타이머 정지 + 레벨 0 리셋(이미 멎었으면 무동작). 캡처 해제는 스레드풀로 —
    /// 장치 정리가 느려도 UI(호출 스레드)를 멎게 하지 않는다.</summary>
    public void Stop()
    {
        WasapiLoopbackCapture? capture;
        lock (_sync)
        {
            if (!_running) return;
            _running = false;
            _generation++; // 진행 중이던 초기화 무효화
            capture = _capture;
            _capture = null;
        }
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _rawRmsL = _rawRmsR = _rawPeakL = _rawPeakR = 0;
        _dispRmsL = _dispRmsR = _dispPeakL = _dispPeakR = 0;
        if (capture is not null) _ = Task.Run(() => TearDown(capture));
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
    }

    private void InitCapture(int generation, string outputDeviceId)
    {
        WasapiLoopbackCapture capture;
        try
        {
            // MMDevice/열거자의 명시 해제는 생략한다(캡처가 장치를 물고 있는 동안 유효해야
            // 하고, 잔여 COM 래퍼는 GC가 정리한다 — Start 빈도가 낮아 축적 축이 없다).
            var enumerator = new MMDeviceEnumerator();
            MMDevice device;
            try
            {
                device = outputDeviceId.Length > 0
                    ? enumerator.GetDevice(outputDeviceId)
                    : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch
            {
                // 저장된 장치가 사라졌거나 ID 표기가 어긋난다 — 기본 장치로 폴백(A164 유실 규칙과 동형)
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            capture = new WasapiLoopbackCapture(device);
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped; // 정상 정지·장치 오류 공통의 단일 해제점
            capture.StartRecording();
        }
        catch
        {
            return; // 캡처 실패 = 빈 미터(크래시 금지 요건). 타이머는 0 레벨만 흘린다.
        }

        lock (_sync)
        {
            if (_running && generation == _generation)
            {
                _capture = capture;
                return;
            }
        }
        TearDown(capture); // 초기화가 끝나기 전에 Stop됨 — 방금 만든 캡처를 즉시 정리
    }

    /// <summary>캡처 정리 — 이벤트 해제 + 정지. Dispose는 RecordingStopped(캡처 스레드 종료
    /// 직전)에서 한다 — 캡처 스레드가 살아 있는 동안 클라이언트를 해제하지 않는 NAudio 관례.</summary>
    private void TearDown(WasapiLoopbackCapture capture)
    {
        try
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.StopRecording();
        }
        catch
        {
            // 이미 멎었거나 장치가 사라짐 — 해제는 RecordingStopped 또는 GC 몫으로 남는다
        }
    }

    private static void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (sender is IDisposable capture)
        {
            try { capture.Dispose(); }
            catch { /* 뒷정리 실패는 무시 — ModuleWorker.Post의 계약과 같은 성질 */ }
        }
    }

    /// <summary>
    /// NAudio 캡처 스레드: 버퍼의 채널별 RMS·피크(선형)만 계산한다 — UI·무거운 일 금지.
    /// 포맷은 공유 모드 믹스 포맷이라 사실상 IEEE float 32이고(BitsPerSample 32로 판정),
    /// 16비트 PCM만 예비로 받는다. 그 외 포맷은 계산 없이 무시(레벨 0 유지 — 빈 미터 규칙).
    /// 채널이 2개를 넘으면(5.1 등) 앞 두 채널(L·R)만 잰다. 모노는 양쪽에 복제.
    /// </summary>
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiLoopbackCapture capture) return;
        var format = capture.WaveFormat;
        var channels = format.Channels;
        var blockAlign = format.BlockAlign;
        if (channels < 1 || blockAlign < 1) return;
        var frames = e.BytesRecorded / blockAlign;
        if (frames < 1) return;

        double sumL = 0, sumR = 0, peakL = 0, peakR = 0;
        if (format.BitsPerSample == 32)
        {
            var samples = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, frames * blockAlign));
            for (var i = 0; i < frames; i++)
            {
                double l = samples[i * channels];
                var r = channels > 1 ? samples[i * channels + 1] : l;
                sumL += l * l;
                sumR += r * r;
                var absL = Math.Abs(l);
                var absR = Math.Abs(r);
                if (absL > peakL) peakL = absL;
                if (absR > peakR) peakR = absR;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            var samples = MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, frames * blockAlign));
            for (var i = 0; i < frames; i++)
            {
                var l = samples[i * channels] / 32768.0;
                var r = channels > 1 ? samples[i * channels + 1] / 32768.0 : l;
                sumL += l * l;
                sumR += r * r;
                var absL = Math.Abs(l);
                var absR = Math.Abs(r);
                if (absL > peakL) peakL = absL;
                if (absR > peakR) peakR = absR;
            }
        }
        else
        {
            return; // 24비트 등 예상 밖 포맷 — 잘못 잰 값보다 빈 미터가 낫다
        }

        _rawRmsL = (float)Math.Sqrt(sumL / frames);
        _rawRmsR = (float)Math.Sqrt(sumR / frames);
        _rawPeakL = (float)peakL;
        _rawPeakR = (float)peakR;
        Volatile.Write(ref _lastDataTicks, Environment.TickCount64);
    }

    /// <summary>선형 진폭(0..1) → 미터 눈금(0..1): dBFS로 바꿔 -FloorDb..0 구간을 펼친다 —
    /// 선형 그대로면 일반 청취 음량이 왼쪽 구석에 뭉친다(미터 관례).</summary>
    private static double MapDb(double linear) =>
        linear <= 0 ? 0 : Math.Clamp(1 + 20 * Math.Log10(linear) / FloorDb, 0, 1);

    /// <summary>표시 타이머(스레드풀): 신선도 판정 → dB 매핑 → 어택 즉시·릴리스 감쇠 →
    /// 피크 홀드 → 렌더 콜백 1회. 계산 전부가 여기(워커)다 — UI 스레드 금지 규칙.</summary>
    private void OnTick(object? state)
    {
        if (!_running) return;
        if (Interlocked.Exchange(ref _inTick, 1) == 1) return; // 밀린 틱 중첩 방지
        try
        {
            var now = Environment.TickCount64;
            var stale = now - Volatile.Read(ref _lastDataTicks) > StaleMs;
            var rmsL = stale ? 0 : MapDb(_rawRmsL);
            var rmsR = stale ? 0 : MapDb(_rawRmsR);
            var peakL = stale ? 0 : MapDb(_rawPeakL);
            var peakR = stale ? 0 : MapDb(_rawPeakR);

            _dispRmsL = rmsL >= _dispRmsL ? rmsL : Math.Max(rmsL, _dispRmsL - RmsRelease);
            _dispRmsR = rmsR >= _dispRmsR ? rmsR : Math.Max(rmsR, _dispRmsR - RmsRelease);

            if (peakL >= _dispPeakL)
            {
                _dispPeakL = peakL;
                _peakHoldL = now + PeakHoldMs;
            }
            else if (now >= _peakHoldL)
            {
                _dispPeakL = Math.Max(peakL, _dispPeakL - PeakRelease);
            }
            if (peakR >= _dispPeakR)
            {
                _dispPeakR = peakR;
                _peakHoldR = now + PeakHoldMs;
            }
            else if (now >= _peakHoldR)
            {
                _dispPeakR = Math.Max(peakR, _dispPeakR - PeakRelease);
            }

            _render(new VuLevels(_dispRmsL, _dispRmsR, _dispPeakL, _dispPeakR));
        }
        finally
        {
            Volatile.Write(ref _inTick, 0);
        }
    }
}
