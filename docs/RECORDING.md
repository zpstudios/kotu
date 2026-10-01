# Record module (A14 / A15)

The Start menu's **Record** module has two modes. It does not own file associations;
saved MP4 and WAV files continue to open in Video and Audio.

## Behavior

- **Screen recording**: choose one screen or open window, then a destination. Records
  MP4 with H.264 video at 30 fps and AAC stereo audio. The default Windows playback
  endpoint is always captured: this includes other applications' sounds, even when
  only one window is selected. The optional microphone checkbox defaults to enabled
  when an active microphone is available. With both sources, each is set to 70% to
  leave mixing headroom. No microphone monitoring is enabled.
- **Microphone recording**: select an active input and save standard PCM WAV
  (48 kHz, 16-bit, mono). WASAPI shared-mode conversion avoids codec installation.
  This first implementation deliberately uses the allowed standard-file alternative
  to M4A: there is no AAC export pass or falsely labeled audio-only MP4.
- The bottom bar supplies Start, Stop and save, Discard, and elapsed time. The main
  panel shows the destination/result and can open its folder. A worker timer updates
  the UI and recording tray status at most four times per second.
- Stop awaits the encoder's completion before publishing the result. Recording uses
  a unique sibling temporary file; the destination is replaced only after successful
  finalization. Discard deletes only that session's temporary file. Failures retain
  any partial recording and show its path; partial MP4 recovery is not guaranteed.
- Closing the window, switching modules, or entering settings while recording uses
  the existing `ICloseGuard`: Stop and save / Discard / Keep recording. Unexpected
  view unload requests a stop and discards its temporary recording after completion.
- Source/device enumeration is capped at 32 screens, 200 windows and 64 microphones.
  Refresh re-enumerates devices. Selection is checked again when starting; there is
  no silent fallback to a different screen, window, or microphone.
- Settings: `record.mode` (`screen` by default), `record.includeMicrophone` (`true`),
  and `record.microphoneId`. These are saved on the module worker when starting.

## Windows and dependency evidence

[Microsoft screen capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture)
provides graphics frames. The separate
[screen capture to video guide](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture-video)
explains GraphicsCapture → MediaStreamSource → MediaTranscoder. Neither is a turnkey
system-audio/microphone mixer. [WASAPI loopback](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording)
captures the playback endpoint mix in shared mode.

Screen recording delegates the full capture/resample/mix/encode/mux pipeline to
[ScreenRecorderLib 7.0.1](https://www.nuget.org/packages/ScreenRecorderLib/7.0.1), pinned
to include the upstream fix for stalled silent WASAPI recordings. API calls were
checked against the same tag's
[Recorder.h](https://github.com/sskodje/ScreenRecorderLib/blob/v7.0.1/ScreenRecorderLib/Recorder.h),
[RecordingSources.h](https://github.com/sskodje/ScreenRecorderLib/blob/v7.0.1/ScreenRecorderLib/RecordingSources.h),
[AudioSources.h](https://github.com/sskodje/ScreenRecorderLib/blob/v7.0.1/ScreenRecorderLib/AudioSources.h),
[Options.h](https://github.com/sskodje/ScreenRecorderLib/blob/v7.0.1/ScreenRecorderLib/Options.h),
and [sample](https://github.com/sskodje/ScreenRecorderLib/blob/v7.0.1/TestApp/MainWindow.xaml.cs).
The upstream repository includes a
[net8.0 console sample](https://github.com/sskodje/ScreenRecorderLib/blob/v7.0.1/TestConsoleAppDotNetCore/TestConsoleAppDotNetCore.csproj).

The package injects `build/x64/ScreenRecorderLib.dll` through
[its build targets](https://github.com/sskodje/ScreenRecorderLib/blob/v7.0.1/Nuget/ScreenRecorderLib.targets).
Both Record and the executable declare the package so the final publish resolves
the reference. The unmodified package LICENSE is copied to
`licenses/ScreenRecorderLib-LICENSE.txt`. No source interop is introduced into the
module. The microphone backend reuses the repository's NAudio.Wasapi 2.2.1 dependency;
its [WasapiCapture source](https://github.com/naudio/NAudio/blob/v2.2.1/NAudio.Wasapi/WasapiCapture.cs)
confirms shared-mode PCM conversion and the RecordingStopped completion contract.

## Threads and lifetime

`KOTU record worker` serializes discovery, device construction, Stop, native disposal,
settings I/O and output publication. ScreenRecorderLib's native workers capture and
encode; NAudio's capture thread writes bounded WAV packets. Neither loop runs on the
UI dispatcher. The thread-pool timer only reads elapsed state and queues one bounded
UI update. Session callbacks complete tasks with asynchronous continuations.

Refresh has a sequence/lifetime guard. A native backend load is isolated behind
non-inlined factory calls so a screen dependency failure leaves microphone recording
available. Completion (not a Stop request) is the boundary for releasing device/file
handles. WAV automatically stops and saves below its 4 GiB container limit (3.5 GB
of sample data), with an explicit notice.

## Limits and verification

- Screen capture needs the Microsoft Visual C++ 2015–2022 Redistributable **x64** and
  Windows Media Foundation; Windows N/KN may need the Media Feature Pack. These
  system components are not downloaded or installed by KOTU. Missing components
  produce an actionable message. Microphone privacy access is controlled by Windows.
- Screen selection uses an application list, not the Windows GraphicsCapturePicker.
  Displays use Desktop Duplication; windows use Windows Graphics Capture. HWND
  capture is offered on Windows 10 1903 or newer. Keep the selected window restored
  and open. Protected content, secure desktop, display/device removal, and sleep may
  cause blank frames or a recording failure. There is no HDR color correction.
- Device/default-output changes during a recording do not retarget it. Refresh and
  start a new recording to use a changed endpoint. The displayed microphone list may
  include virtual or line-in endpoints that Windows classifies as capture devices.
- Recording is finalized on normal Stop/close. Forced process termination can leave
  the sibling temporary file; there is no crash-recovery scanner or guarantee that
  an unfinished MP4 is playable.
- The repository-bundled .NET SDK completed the full Release/x64 build with zero
  warnings and errors. The publish check included `ScreenRecorderLib.dll` and the
  package's original license. Roslyn syntax and pinned-package API checks, project XML
  and architecture checks, and the complete 465-test suite also passed.
  Equivalent output-preservation tests are linked into `KOTU.Core.Tests`.
- Device checks: screen and window MP4 with system sound, silent-system interval,
  optional microphone mix, microphone-only WAV, missing/denied microphone, source
  closed/minimized, device unplug, save failure, rapid start/stop, Discard, close/module
  guard, and background/tray recording. Check both the saved duration and audible
  tracks. These are pending real-device checks, not claimed results.
