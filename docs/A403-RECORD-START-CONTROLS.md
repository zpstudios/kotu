# A403 — explicit recording start controls

## Status and accepted scope

2026-10-26: v0.411.0 implementation and local checks accepted by main. One GPT-6 Astra/high
delegate completed150 focused and919 full tests across8 assemblies,0 failed/not-executed,
Release/x64 with0 warnings/errors, architecture20/release-contract7 and normal static package
checks. Main independently matched9 source/8 package/7 protected-backend hashes and parsed the
final TRX results. Program.Main,IsStandalone=false,KOTU-Main and0.411.0.0 identity confirmed.
2026-10-27: actual noncapturing UI checks passed and main accepted this implementation batch
for release; remote release is pending. A402v0.410.0's earlier actual silent camera
capture/save/playback does not prove the new A403 button path.

Replace the lower Record bar's single Start with Audio record, Video record and Camera record,
using existing compact icon styling and exact accessible names. Retain one shared Stop and save,
Discard and Open result folder. This is the in-app Record bar, not Windows taskbar controls.

The two existing source-settings views remain. They control which settings are shown; each start
button explicitly requests its own RecordingKind. Audio record remains the selected-microphone
PCM WAV path. Video and Camera retain optional microphone/system audio and MP4. Explain these
meanings visibly, including when the visual microphone checkbox is off. No mixer/codec expansion.

## Contract

- One dispatcher-owned StartAsync(kind) captures immutable effective source/audio IDs, labels,
  preferences and A405 committed folder. Shared production policy drives both readiness and start.
  Set both starting/busy before any callback or await, so close/unsaved state also sees preparation.
- Keep existing per-view ownership through native completion, disposal and file publication or
  cleanup. A stop request, native failure timeout or failed Stop does not permit a new start.
  Guard the old-Start-awaiting-Stop finally against changing a newly accepted attempt.
- Screen/window selection stays mutually exclusive; camera selection is independent. Camera has
  no automatic default or substitute. Preserve remembered missing exact identities for reappearance.
- Add only record.cameraKey. Reuse record.sourceKey for non-camera visual selection. An absent,
  null or wrong-typed cameraKey can migrate a legacy camera: sourceKey; an explicit string,
  including empty, takes precedence. Save both preferences through the existing worker Save.
- Discovery carries independent video/camera preferences, overlaid with the active request's
  relevant source/audio IDs. Preserve category-failure versus successful-empty semantics and
  coverage checks before treating capped/stale observations as active-source loss.
- All starts are disabled while preparing/starting/recording/stopping/saving. Kind-specific
  source and optional audio prerequisites control idle availability. Visible reason/source rows
  supplement tooltips, including unavailable-camera guidance. Preserve existing final error/result.
- Keep six32-DIP actions, existing spacing and a shrinking/trimmed status area. Check actual
  minimum-width and representative app-scale layout after exact package review.

## Verification boundaries

Planned tests link actual production readiness/request/preferences/discovery helpers. They cover
wrong-kind requests, silent visual versus microphone-only Audio, immutable snapshots, setting
migration/restart, independent preferences, failed/empty discovery and stale-source coverage.
They do not instantiate RecordView; main separately audits actual callback/await/release ordering.

Build/full tests, architecture/release gates and normal package identity precede native UI checks.
No further actual camera activation is included in this run after A402's duration overrun.
Nonactivating camera selection/readiness UI may be checked; a real new Camera button Start/save
must remain unverified. Any controlled Audio/Video native test needs its own explicit test scope.

Current ownership remains per-view. Cross-window and future quick-record exclusion belong to
A406/A407; A403 cannot claim that integration is already complete. A406 must reconcile the last
Record sound configuration (possibly visual system-only/mixed sound) with its eventual audio
backend, without silently discarding the selected audio sources.

Phase1 evidence: `artifacts/a403-start-controls/agent/phase1-report.md`; main acceptance and
implementation contract: `artifacts/a403-start-controls/main/phase1-review.md`.

Final implementation evidence: `artifacts/a403-start-controls/agent/phase2-report.md` and
`artifacts/a403-start-controls/main/phase2-review.md`. Final callback-order correction attaches
the finish task and temporary-file ownership before presentation callbacks, then respects a
reentrant Stop. Old-attempt catch/finally guards preserve a new request. The final919 suite and
normal package include this correction; focused150 is also included in that full run.

The first restricted focused run stalled at testhost startup and was interrupted once; an
identical approved external run passed. Its exact environment cause remains unknown. Local
packaging uses an artifact-only replacement of the absent machine7z source path; inverse
equivalence and all original checks were confirmed. Remote packaging remains unchanged.

An inherited limitation remains: a remembered source outside the first capped discovery result
may not restore on restart, because cap preference starts from selected objects. Both current
selected visual types now retain their independent cap priorities; no identity is substituted.

2026-10-27 fresh native preflight found one existing installed KOTU window with an unsaved
Untitled indicator. Latest computer-use runtime initialized successfully. The delegate stopped
before any UI input, settings backup/change, test launch or capture; no fixture or cleanup was
needed. This is not a product/UI failure. Main requested that the user save and close the app
before resuming. Evidence: `artifacts/a403-start-controls/native-20261027/native-report.md`.
The user subsequently confirmed saving and closing. Fresh inventories showed no KOTU instances;
the same sole delegate tested the reviewed normal package with a new settings byte backup.

## Actual noncapturing UI and acceptance — 2026-10-27

The delegate directly observed all six distinct bar actions, visible unavailable-camera guidance,
microphone-only Audio/WAV semantics when visual microphone mixing was off, independent camera/video
selection, screen/window exclusion, and preservation through Refresh and source-settings panes.
At 707-wide and application scales150%/125%, controls and wrapped guidance remained usable.
Opening application Settings recreates Record and restores saved preferences; unsaved choices
across that recreation were not claimed persistent. Screenshots were inspected through the official
tool display by the delegate, not retained; main reviewed report/accessibility evidence.

All RecordStart and camera activation counts were zero. Both owned windows closed normally;
native windows/processes/output were zero and the exact owned TEMP root was removed. Main
independently matched restored settings to the fresh backup, confirmed process0/TEMP absence,
and rechecked all24 unchanged hashes. The earlier unsaved-document preflight remains preserved.

Main accepts release of this implementation with these limits: new native three-button start/
stop/save/discard paths, active-session races and cross-window ownership were not exercised.
Portable production-policy tests and callback-order review support those code paths without
claiming actual capture coverage. Remote original build/package/release gates remain required.
Evidence: `artifacts/a403-start-controls/native-20261027/resumed/native-report.md`,
`artifacts/a403-start-controls/main/native-review.md` and `native-independent-check.json`.
