# A406/A407 — quick recording and tray lifetime

## Current status — 2026-10-28

A403 v0.411.0 is released. A406/A407 investigation is complete; no quick recording feature has
been implemented or released. Main accepted the smaller integration design: one process-wide
recording admission gate, ordinary Record ownership/close behavior preserved, and a future
quick-only owner whose worker/session/files survive view changes. A wholesale foreground rewrite
is unnecessary. Implementation remains one GPT-6 Astra/high delegate at a time.

The first sub-batch B1a is implemented and main-reviewed: production admission/finalization
primitives and36 portable tests, Core build0warnings/errors. Main independently parsed final TRX
and matched23 evidence hashes. Review corrected recovered Stop/cleanup handling and asynchronous
test synchronization. Main then reviewed B1b's actual constructor/cleanup boundaries and104-line
integration plan. B1b code is now implemented and main-reviewed for a planned v0.412.0 foundation
release: ordinary Record admission, typed creation/cleanup/output outcomes and paired close/restart
leases. Final focused222/222 and full991/991 across eight assemblies passed, with build0/0,
architecture20 and original release-contract7. Main independently checked54 evidence hashes and
22 product/test files. Normal package review also passed:26 package+19 evidence hashes, Program.Main,
non-Standalone, version0.412.0.0. The local 7z source path adaptation is inverse-equivalent to the
original package script and retains every check. Initial UI preflight stopped after Record Ready
because the sky window tool stalled; all Starts/captures were0. Official CUA restored both owned
windows/settings/TEMP, independently verified by main. The subsequent KOTU-only test proved the
synthetic Window target, microphone/system OFF, local TEMP output and other-view start exclusion.
Its first Video recording failed with encoder0xc00d36b5 after close→Stop and save; a328264-byte
partial lacks moov, so actual duration/track counts and playability are not established. UI showed45s;
Start→Stop request interval48.153s exceeded the short target during observation round trips.
The partial could not be hashed while KOTU lived but could after normal exit; the lock owner and
whether B1b introduced either symptom are unconfirmed. Capture2/reuse/playback were not attempted.
All owned windows closed and original settings were restored; the partial remains local for review.
v0.412.0 release is ON HOLD. Read-only investigation and main review are complete. The encoder
message originates from frame processing; the first failing native call and lock owner remain unknown.
The same SRL finalization/reporting limitations exist in v411, so neither regression nor vendor cause
is established. The verified v411 comparator succeeded once: owner close/save, live output read,
moov/video1/audio0/header58.255s. All owned windows/settings were restored. Duration and exact target
geometry differ or remain unmeasured, so this does not identify the regression cause. The unmodified
v412 repeat failed again with the same HRESULT. Live read sharing failure and a complete RM result
identified the same KOTU PID/start identity as the single resource user before exit. StopSave request
was59.695s, but its pre-click dialog already showed Saving/UI37s; actual duration equivalence and
Stop causation are not established. All windows/settings were restored and main independently
checked the250001-byte partial hash and RM identity. Release remains on HOLD. The concrete lifecycle
investigation and65-line first-error/release trace plan are complete and main-reviewed. No proven
early Stop/wrong-thread Dispose defect was found. The user selected ONE diagnostic batch on2026-10-28.
Its isolated normal-entry build and one silent Window run completed successfully:66 complete events,
same-worker Stop/Dispose, live readable output, Published, MP4 header61.8s/video1/audio0. The prior failure
was not reproduced; logging/timing/geometry/arrangement effects remain unseparated. All owned windows
and original settings were restored; main independently matched78 source/package hashes and trace
sequence1–66/dropped0. No original product edits, second recording, commit/push or follow-on batch.
Release HOLD remains. All delegates stopped; result is reported before further work. A managed-trace
control with native Debug disabled is only a proposed next batch. It concerns ordinary Record Video screen/window recording and Stop and save/close-save, not quick audio recording or tray behavior. The user requested handover before any further test; this proposal remains unselected and unexecuted. Prior media and new media/raw logs
remain locally in TEMP; see main/diagnostic-batch-20261028-review.md and the diagnostic evidence report.
Quick recording remains unimplemented and no product commit/release has occurred.

## Accepted user policy

- Holding the menu's Record item for about3 seconds starts quick recording using the last Record
  audio configuration. A short click keeps normal navigation. Releasing a completed long hold closes
  the menu without changing the current module or firing the ordinary click again.
- Ordinary close during quick recording hides to tray and keeps recording. Tray Stop and save is
  available; explicit Exit saves before exiting. Errors must remain visible and recoverable.
- Hardware's interval display/control is replaced while quick recording is active. Main's minimum
  implementation decision preserves existing sampling cadence/subscription behavior and only locks
  those controls; this is not attributed as a separate user confirmation.

## Implementation boundaries

Preparation reserves ownership before callbacks/await. Ordinary and quick starts share admission.
Stop requests/errors/timeouts do not release it: actual capture completion, confirmed native cleanup
and file disposition are required. Uncertain native cleanup stays blocked with error/path information.
Normal publication failure may retain a recoverable partial after native release is proved.
If native/stream release and file work are finished but access/existence cannot be verified, B1b may
end with an explicit known-path-preserved/unverified failure. It must not claim a saved/existing
partial or delete uncertain output. That file inspection issue alone need not retain global Busy;
uncertain native cleanup still blocks every finalization. Confirmed absence is a separate receipt.

The last audio configuration needs an explicit definition separating raw visual checkboxes from
the Audio button's effective microphone-only path. Phase1 recommends last successfully started
effective sources with clear visible explanation and migration provenance; that definition is not
yet approved for implementation. B1a is independent of that decision. No silent device substitution,
discarded requested system source, or automatic microphone use when no audio sources are selected.

Existing NAudio dependencies can support audio-only WAV; the current microphone backend alone
cannot satisfy system-only/mixed audio. Native endpoint adaptation and a bounded timestamp-aware
timeline require separate reviewed batches. ScreenRecorderLib video-off produces black video and
is not an audio-only substitute. Synthetic tests cannot establish actual device/driver behavior.

Quick start UI remains unconnected until stop/save, close/tray/Exit and error recovery are complete.
Future native recording needs a separately bounded test scope; no additional camera activation is
included in this run. A403's existing tests/UI/release require no repetition for this investigation.

Evidence: `artifacts/a406-a407/phase1/phase1-report.md`, `audio-backend.md`, `evidence-index.json`.
First implementation contract: `artifacts/a406-a407/main/b1a-contract.md`.
Accepted first batch: `artifacts/a406-a407/main/b1a-acceptance.md`; final report `b1a/b1a-report.md`.
Current integration contract: `artifacts/a406-a407/main/b1b-contract.md`; reviewed plan `b1b/implementation-plan.md`.
Accepted code/evidence: `artifacts/a406-a407/main/b1b-acceptance.md`; final report `b1b/b1b-report.md`.
