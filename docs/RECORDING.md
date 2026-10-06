# Record module (A14 / A15)

The Start menu's **Record** module has two modes. It does not own file associations;
saved MP4 and WAV files continue to open in Video and Audio.

## Behavior

- Modes, screens/windows, system outputs and microphones use square icon tiles
  that wrap into columns. Screens (entire displays) and windows (individual apps)
  have separate headings and different icons, with a single selection across both
  groups. Native selection marks and keyboard navigation remain available. Longer
  grids scroll in place; full device and window names are available in tooltips.
- Selected tiles have a 2 DIP accent border, tinted background and a top-right
  check badge. These decorations follow the actual container selection for mouse,
  keyboard, restored preferences and recycled items in all five source grids.
- **Screen recording**: choose one screen or open window and a save folder. Records
  MP4 with H.264 video at 30 fps and optional AAC stereo audio. System audio and
  microphone inclusion have separate checkboxes. Choose the exact playback endpoint:
  its other applications' sounds are included even when only one window is selected.
  Uncheck both audio choices for silent video. With both audio sources, each is set
  to 70% to leave mixing headroom. No microphone monitoring is enabled.
- **Microphone recording**: select an active input and save standard PCM WAV
  (48 kHz, 16-bit, mono). WASAPI shared-mode conversion avoids codec installation.
  This first implementation deliberately uses the allowed standard-file alternative
  to M4A: there is no AAC export pass or falsely labeled audio-only MP4.
- The bottom bar supplies compact 32 DIP Start, Stop and save, Discard, and Open
  result folder buttons, followed by elapsed time/status. Every icon button has an
  explicit tooltip and accessible name. The main panel focuses on source choices,
  save-folder selection and result/error paths. The bottom bar stretches with the
  shell; its status text trims before the four button columns. A worker timer updates
  the UI and recording tray status at most four times per second.
- A compact main banner and the bottom bar show the same capture state, elapsed
  time and the source labels captured when Start was pressed. A red dot accompanies
  **Recording screen** or **Recording microphone** only after the backend confirms
  active capture. Preparing/Starting, Stopping, Saving, Finished and Error have no
  red capture indicator. Stop removes the indicator immediately; native completion
  and finalization take precedence over queued progress. Source list refreshes do
  not rename the active source labels. Full bottom-bar text is available in its
  tooltip and accessible name.
- The tray uses the same cached state: red `VRC`/`ARC` while screen/audio capture is
  confirmed, and neutral `PRE`/`STP`/`SAV`/`END`/`ERR` during other phases. An idle
  Record view keeps the existing `REC` module label. Busy operations such as folder
  permission checks or publishing a file do not claim active recording.
- The initial save folders are the Windows Videos library's `KOTU` folder for MP4
  and the Music library's `KOTU` folder for WAV. **Change folder** remembers a separate
  location for each mode. Folder initialization creates missing directories and checks
  write access on the module worker; an error disables Start for that mode until a
  valid folder is chosen. No alternate save location is silently used.
- Start generates a timestamped filename with a full GUID suffix directly in the
  displayed folder. There is no per-recording Save dialog or empty destination file.
  **Open folder** opens the folder containing the last saved or recoverable partial
  recording, and remains disabled until a result path is available.
- Stop awaits the encoder's completion before publishing the result. Recording uses
  a unique sibling temporary file; the destination is created only after successful
  finalization, using a move that refuses overwrites. A concurrent filename collision
  preserves both the existing file and the temporary recording. Discard deletes only
  that session's temporary file. Failures retain
  any partial recording and show its path; partial MP4 recovery is not guaranteed.
- Closing the window, switching modules, or entering settings while recording uses
  the existing `ICloseGuard`: Stop and save / Discard / Keep recording. Unexpected
  view unload requests a stop and discards its temporary recording after completion.
- Source/device enumeration updates automatically about every two seconds and is
  capped at 32 screens, 200 windows, 64 microphones and 64 outputs. The selected
  available source stays included within each cap. Refresh requests an immediate
  observation. Stable display IDs, window handle+PID, and audio endpoint IDs preserve
  selections across label/default-device changes. Initial default selection happens
  only on the first successful observation without a remembered ID. Missing IDs
  stay unselected; another source is never silently substituted.
- A failed category preserves its last known rows and reports an independent source
  refresh error. It does not establish device removal. During recording, lists still
  update but selection is locked. Successfully observed loss of an active source
  requests Stop/save; successful output or a recoverable partial path retains the
  reason. Minimized/hidden windows omitted by enumeration remain present while the
  selected HWND still belongs to the same PID; starting still requires a restored
  available window. Native capture failures may finish first.
- Settings: `record.mode` (`screen` by default), `record.includeMicrophone` (`true`),
  `record.includeSystemAudio` (`true`), `record.microphoneId`, `record.outputId` and
  `record.sourceKey`. These are saved on the module worker when starting.
  `record.videoFolder` and `record.audioFolder` remember custom folders on the same
  worker after a successful folder choice. Canceling the picker preserves the current
  location. Switching mode displays its own folder. Selecting a folder or initializing
  folders locks selectors and Start; late picker/initialization results after unload
  are ignored.

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

`KOTU record worker` serializes known-library resolution, folder creation
and write probes, destination naming, device construction, Stop, native disposal,
settings I/O and output publication. FolderPicker UI stays on the dispatcher; only
the chosen path is sent to the worker for validation and persistence. Mode, source,
microphone, optional output and destination folder are snapshotted before recording starts.
ScreenRecorderLib's native workers capture and
encode; NAudio's capture thread writes bounded WAV packets. Neither loop runs on the
UI dispatcher. The thread-pool timer only reads elapsed state and queues one bounded
UI update. Session callbacks complete tasks with asynchronous continuations.
The timer snapshots only atomic elapsed/capture/completion state; the dispatcher
applies a pure presentation result to the banner, bottom bar and cached tray state.
No blinking timer or capture-state polling runs on the UI thread.

`KOTU record source discovery` is a separate BelowNormal worker. A thread-pool timer
queues one discovery at a time, including at most one waiting UI application. All
native discovery, audio enumeration, sorting and window identity probes run there,
so discovery cannot occupy the Stop queue. Unchanged snapshots do not mutate rows;
changed rows are diffed into ObservableCollections. Failed observations preserve
their category. A capped observation for an old selection cannot establish loss of
a newly selected recording source: mismatching observation preferences cause a fresh
poll instead. Disposal cancels work and closes the timer/discovery queue; late UI
applications check lifetime and sequence. Native enumeration itself has no timeout;
a stuck discovery delays source updates, but cannot delay capture Stop.

A native backend load is isolated behind
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
  select a new source after saving to use a changed endpoint. The displayed microphone list may
  include virtual or line-in endpoints that Windows classifies as capture devices.
- Recording is finalized on normal Stop/close. Forced process termination can leave
  the sibling temporary file; there is no crash-recovery scanner or guarantee that
  an unfinished MP4 is playable.
- The repository-bundled .NET SDK completed the full Release/x64 build with zero
  warnings and errors. The publish check included `ScreenRecorderLib.dll` and the
  package's original license. Roslyn syntax and pinned-package API checks, project XML
  and architecture checks, and the complete 465-test suite also passed.
  Equivalent output-preservation tests are linked into `KOTU.Core.Tests`.
- A391 added default-folder creation, custom-folder/no-fallback, invalid-folder,
  unique-name/no-empty-destination and non-overwrite collision tests. The targeted
  output suite passed all 12 cases and the Record Release/x64 build passed with zero
  warnings/errors. FolderPicker, redirected libraries and device capture need GUI
  checks; they were not exercised by these tests.
- A390's 11 presentation tests cover confirmed screen/audio capture, startup with
  no native capture, stop/finalization priority over late recording snapshots,
  completion/native finishing, retry reset and idle state. Record Release/x64 also
  built with zero warnings/errors. Visual markers and actual device transitions
  remain pending GUI checks.
- A388 Record Release/x64 build passed with zero warnings/errors. Nine pure catalog
  tests cover stable identity, no-fallback defaults, observation races, failed
  categories, silent video/microphone requirements and minimal row changes. Together
  with the 12 output tests, 21/21 targeted tests passed. These tests do not exercise
  native device discovery, unplug handling, the visible list UI or silent MP4 output.
- Device checks: screen and window MP4 with system sound, silent-system interval,
  optional microphone mix, microphone-only WAV, missing/denied microphone, source
  closed/minimized, device unplug, save failure, rapid start/stop, Discard, close/module
  guard, and background/tray recording. Check both the saved duration and audible
  tracks. These are pending real-device checks, not claimed results.
