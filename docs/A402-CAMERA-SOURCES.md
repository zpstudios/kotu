# A402 — camera recording sources

## Status and scope

2026-10-26: v0.410.0 implementation and local verification accepted by main. The sole
GPT-6 Astra/high delegate completed final focused105/105 and full874/874 tests across8 assemblies,
Release/x64 with0 warnings/0 errors, architecture20 and release-contract7 checks. Main independently
matched14 source and8 normal-package hashes; Program.Main, IsStandalone=false and no diagnostic
entry seams were confirmed. The first native Start failed before observed recording or any file.
Nonactivating investigation established a case-only WinRT/native ID mismatch rejected by the
pinned library's exact comparison; a separate2×2 preview resize defect was found. Balanced MF
startup and native spelling resolution plus32×32 preview corrected those implementation defects.
External access was Allowed; no OS privacy setting was changed. Historical raw HRESULT is unknown.

The corrected normal package passed one actual camera Start/readiness, Stop/save and KOTU playback.
The MP4 contains one640×360 H.264 video track,338 samples,11.266633 seconds,826,585 bytes and no
audio track. The delegate directly observed different video images; UI position advanced0:00→0:04.
The approximately5-second target overran during UI/tool round trips (UI11s; Start→Stop13.080s).
No second Start was made, so same-device reuse is explicitly unverified. Both windows closed
normally, exact test media was deleted from local non-OneDrive TEMP, and fresh settings were
byte-restored. Main independently rechecked settings/backup,22 source/package hashes, process0
and TEMP0; final native inventory is empty. No raw footage or frame screenshot is retained here.

Main accepts the implementation for release with these stated native limits, not an all-scenarios
pass. Reuse, physical disconnect/denial/busy failures, mixed audio, live folder changes and arbitrary
driver hangs remain unverified. No further camera capture is included in this run.

Add Cameras beside Screens and Windows in the existing video mode, with one source selection.
Reuse existing exact audio selections, automatic camera format, H.26430fps MP4 and A405's common
captured folder. No camera auto-default/fallback. Remember only the exact opaque selected ID.
The existing video-mode label may become Video recording; A403's three buttons remain later.
A402 keeps per-view recording/session scope; A406/A407 global quick/tray lifetime is separate.

## Reviewed decisions

- Discover cameras independently on the existing worker using WinRT device-interface enumeration.
  Null/error preserves prior rows; successful empty means no cameras. Cap64 retains the preferred
  identity. Stable camera keys never pass window-only HWND/PID logic. New devices never replace a
  selection. Enumeration/refresh does not activate cameras or query supported native formats.
- Revalidate the exact selected camera at explicit Start. Preserve captured labels/sound/folder
  before callbacks/await. Camera filenames use KOTU-camera with the existing unique/no-overwrite
  temporary-to-final publication. Never retarget to another device or a newly chosen folder.
- Use pinned ScreenRecorderLib7.0.1 camera source and current encoder/audio lifecycle. Resolve
  exactly one OrdinalIgnoreCase native device ID at Start and pass its original native spelling;
  preserve the WinRT selection key and reject absent/ambiguous matches without fallback.
  Camera-only MTA and MF startup are balanced on the same worker, including construction failures.
  Dispose native recorder, owned camera source, MF and COM in that order after completion.
  Do not send COM disposal through ModuleWorker.Post's post-disposal thread-pool fallback.
- A camera-only32×32 source-frame callback updates an atomic timestamp without inspecting/storing
  pixels or posting UI per frame. Recording is confirmed only after native Recording AND the
  first source frame. First-frame deadline10s, subsequent absence5s; background check250ms.
  Latch first failure/one automatic Stop. User Stop freezes displayed time but preserves raw
  evidence so a rejected Stop can resume accurately. Native Finishing, terminal and guard failure
  are irreversible; late frames/status cannot reactivate them. Pending camera termination and
  its reason are visible while actual completion is awaited, with explicit Stop retry available.
- Timeout does not manufacture native completion, release ownership, publish files, dispose an
  in-flight recorder or permit a new session. A wedged native driver remains a limitation; no
  process-isolation or library-upgrade expansion is silently included.
- Camera failure text is actionable without guessing permission/in-use/format causes from a
  localized native message. Existing screen/audio/discard/partial/collision behavior stays covered.

## Evidence and verification boundaries

API probe compiled against the installed exact package and repository Windows target with zero
warnings/errors; it is an uncalled class library, not a device test. Evidence and pinned primary
source links: `artifacts/a402-camera/agent/phase1-report.md`, `agent/api-probe/`, `agent/upstream/`.
Main read current Record lifecycle and pinned AcquireNextFrame/draw/source-callback code.

Native library enumeration can hide failure as empty; format lookup activates a camera. Native
sample errors do not always become terminal failure callbacks, motivating the camera-specific
liveness guard. First-frame readiness and WinRT-to-MF identity compatibility passed on this
device; physical frame-loss behavior and compatibility across devices remain unverified.

Main phase2 review and final local evidence are in `artifacts/a402-camera/main/phase2-review.md`
and `agent/` final logs/manifests. The original local packaging script lacked machine-wide7z.dll;
an artifact-only copy reused the existing verified engine and retained all other original checks.
No device was opened by compilation, unit tests or packaging.
The planned first native gate included exact-device selection/first frames, a short silent MP4,
Stop, playback and reuse. Actual selection/capture/save/playback passed once; reuse was omitted
after the timing overrun. Failure/removal/audio/lifetime/folder cases remain separate limits.

Native preparation evidence: `artifacts/a402-camera/native-preflight-20261026/preflight-report.md`,
main acceptance `main/phase3a-review.md`. A mistaken installed-app launch was excluded and safely
closed before exact reviewed executable identity was established. Main independently verified
fresh settings/backup equality, process0, output files0 and the selected/silent UI. Recording
files, if authorized, will remain in a non-OneDrive local TEMP directory and be deleted after
verification. Native privacy settings/permissions were not changed.

Actual failed attempt: `artifacts/a402-camera/native-capture-20261026/native-capture-report.md`,
main acceptance of failure/cleanup `main/phase3b-review.md`. A reserved partial path in the UI
does not prove a file exists. Native raw error/HRESULT was not preserved by the current fixed
camera message; the exact historical native error remains unknown. No OS prompt was observed,
and no privacy/device setting was changed. The proven ID-spelling incompatibility was the
strongest explanation; the reviewed correction and successful actual test are recorded below.

Correction contract: `artifacts/a402-camera/main/correction-contract.md`; nonactivating results
`failure-investigation/enumeration-result.json` and `enumeration-external-result.json`. Resolve
exactly one case-insensitive native symbolic link at Start, keep its original spelling for SRL,
preserve the WinRT key and never fallback to another device. Native enumeration requires balanced
MF startup/shutdown inside camera COM ownership. The32×32 Uniform callback target guarantees
nonzero even dimensions for aspect ratios1:16..16:1; it is not universal format support. The initial
2×2 implementation is superseded. Final acceptance is recorded in
`artifacts/a402-camera/main/correction-review.md`, with final package/manifests under
`correction-implementation/`. The original failed package remains preserved for comparison.

Corrected native evidence: `artifacts/a402-camera/corrected-native-20261026/native-report.md`.
Main acceptance: `artifacts/a402-camera/main/corrected-native-review.md`, including independent
settings/hash/process/output checks. Timing deviation and omitted second capture are preserved.
