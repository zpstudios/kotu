using KOTU.Core.Contracts;

namespace KOTU.Module.Record;

internal enum RecordingPhase { Ready, Starting, Active, Stopping, Saving, Finished, Error }

/// <summary>세션 스냅샷을 UI와 트레이의 같은 상태로 바꾸는 순수 표시 규칙.</summary>
internal sealed record RecordingPresentation(RecordingPhase Phase, bool IsRecording, string Text, string Detail, TrayStatus Tray)
{
    public static RecordingPresentation Create(RecordingPhase phase, bool screen, bool recording,
        bool completed, bool previouslyRecorded, TimeSpan elapsed, string sources)
    {
        // 정지 요청/종료 상태가 늦게 도착한 녹화 스냅샷보다 항상 우선한다.
        if (phase == RecordingPhase.Active)
            phase = completed || previouslyRecorded && !recording ? RecordingPhase.Saving
                : recording ? RecordingPhase.Active : RecordingPhase.Starting;
        var active = phase == RecordingPhase.Active;
        var clock = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        var label = phase switch
        {
            RecordingPhase.Starting => screen ? "Starting screen recording" : "Starting microphone recording",
            RecordingPhase.Active => screen ? "Recording screen" : "Recording microphone",
            RecordingPhase.Stopping => "Stopping",
            RecordingPhase.Saving => "Saving",
            RecordingPhase.Finished => "Finished",
            RecordingPhase.Error => "Error",
            _ => "Ready",
        };
        var text = phase == RecordingPhase.Ready ? label : label + " · " + clock;
        var detail = string.IsNullOrEmpty(sources) ? text : text + " · " + sources;
        var tray = active ? TrayStatus.Open(screen ? "VRC" : "ARC", clock, 0xFFFF5050, 0xFFFF5050)
            : phase == RecordingPhase.Ready ? TrayStatus.Idle("REC")
            : TrayStatus.Open(phase switch
            {
                RecordingPhase.Starting => "PRE",
                RecordingPhase.Stopping => "STP",
                RecordingPhase.Saving => "SAV",
                RecordingPhase.Finished => "END",
                _ => "ERR",
            }, phase == RecordingPhase.Starting ? "Start" : label, 0xFFA0A0A0, 0xFFA0A0A0);
        return new(phase, active, text, detail, tray);
    }
}
