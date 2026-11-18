# A405 — common recording folder

## Current status

v0.409.0 officially released on2026-10-26 at13:01:36 KST; product/tag125b0d2. Focused65/65 and full845/845
in8assemblies passed, Release x64 zero warnings/errors, architecture20/release7, normal package
and entry point verified. Build40266331584 and release40266331540 attempt1 each passed845/845 in8assemblies with zero compiler warnings/errors. Original package/startup/install/Standalone/COM gates, exact tag, eight uploaded assets and latest stable release verified. Native coverage is partial;
the limitations below remain open. Camera integration belongs to the subsequent A402 batch.

The user confirmed differing existing destinations switch directly to Windows Documents/KOTU,
without a one-time choice. Main includes differing custom/default combinations in that decision.
Existing recordings never move. A406 uses the last Record sound-source configuration; A407 normal
close retains capture in the tray, Stop and save ends it, and explicit Exit saves before exiting.
Those two policies are separate later implementations.

## Implemented contract

- One `record.saveFolder`: empty string resolves the current Windows Documents/KOTU default;
  normalized absolute string is custom. Existing valid common value is authoritative. Typed
  missing/null/wrong type migrates legacy values; invalid nonempty strings fail explicitly.
- Two legacy defaults transition to Documents. Otherwise compare actual normalized destinations:
  equal valid paths remain common; differing paths transition to Documents. A mixed implicit
  default that cannot be resolved fails explicitly. Historical redirects are not guessed.
- Resolve Windows Documents on a scoped worker for every accepted default session. Only the
  selected destination is created/probed. Legacy folders and existing recordings are not changed.
- One module-owned coordinator publishes immutable committed state. Serialize preparation and
  settings writes. Publish only after Save succeeds. On failure restore the prior typed setting
  and await one compensating Save before allowing the next change.
- Failed compensation retains a usable preceding runtime destination and a persistent restart
  uncertainty warning. Explicit successful save clears it; preparation-only Retry cannot. This
  does not promise global settings atomicity or exact original JSON shape for unresolved values.
- Shared initialization survives one view closing. Notifications isolate observer failures, use
  latest revision/lifetime checks and cannot let stale preparation errors poison newer state.
- Start captures committed state before callbacks/await, guards reentry and cleans up on error.
  Captured destination feeds temporary output, backend, FinishAsync and result-folder navigation.
  Existing non-overwrite publication, partial-file ownership and backend lifetime are unchanged.
- One Save folder card and Change action serve audio and screen; error-only Retry handles folder
  preparation failures. The shared hook is ready for camera, whose native path is not yet added.

## Verification and release decision

Main reviewed all10 product/test files including new files, real JsonSettingsService snapshot
behavior and one failed-save/unrelated-write/compensation race. Meaningful tests cover migration,
initialization/cancellation/reentry/stale errors, actual prepared output paths and typed rollback
with restart. Eight full-suite TRX results total845 passed, zero failed/skipped. Source10/package5
hashes match after native work. Early sandbox testhost/build failures remain separate evidence;
identical external validation passed. Local packaging used an identical previously verified7z.dll
through one artifact-local source-path substitution; the original remote release gate subsequently passed unchanged.

Normal native checks passed: screen/audio common card, picker open/cancel, two Record views with
the same initial path and survivor after closing one, explicit file-as-folder error, Start disabled
and Retry preserving error/sentinel. These do not prove successful Change or live shared updates.

In the initial native run, successful picker selection, live X-to-Y propagation, newly selected path after restart, recovery
through Change, captured X session while Y commits and actual output/save/playback were unobserved.
PickerHost was absent from documented automation targets; synthetic fixture native calls stalled.
Main stopped further attempts and accepted this implementation for release based on the reviewed
routing, existing unchanged capture/publication backend, isolated filesystem/persistence tests and
observed common UI behavior. This is a deliberate bounded release decision, not the originally
planned full native gate passing. The subsequent manual check below closes only the stated folder-selection gaps; camera belongs to A402.

No Start was pressed and no screen/audio/camera capture occurred. Original settings were restored
byte-for-byte; protected file/source/package hashes match and external owned process inventory is
empty. Final native inventory was not retried after provider stalls. No user app needed reopening.

## Manual picker supplement — 2026-10-26

After release, the user selected the prepared Y folder and explicitly reported completion.
Both real Record views changed from X to Y, displayed Ready and enabled Change/Start. The actual
common setting persisted Y. Closing one view preserved the other; after closing both and normal
restart of the same verified package, Record displayed Y with no folder error. Main inspected
the report, UI evidence/screenshot and persisted-key/cleanup records, and rehashed source10/package5.

The fresh manual-run settings backup was restored byte-for-byte. Test apps closed normally;
external processes0 and native owned windows[] were confirmed. X/Y remained empty and existing
protected sentinel/backup hashes matched. No recording Start, capture or fixture was used.

Successful normal picker commit, two-view propagation and restart persistence are now verified.
Recovery from folder/save failure, active-recording X while Y commits and actual media save/playback
remain unobserved. These results do not retroactively turn the initial limited run into a full pass.
Evidence: `artifacts/a405-implementation/manual-20261026/report-manual.md` and accompanying files;
main acceptance: `artifacts/a405-implementation/main/manual-review.md`. No product/release change.
## Evidence

- Design: `artifacts/a405-implementation/agent/phase1-report.md`.
- Implementation/tests/package: `agent/phase2-report.md` in the same artifact directory.
- Actual observations/tool limitations/cleanup: `agent/phase3-report.md`.
- Main review: `main/code-review.md`, `main/final-local-review.md`, independent TRX/hash summaries.
- Historical read-only18/18 proof: `artifacts/a405/agent/investigation.md` and `proof-result.json`.
  Its pre-answer preserve-one-custom/prompt proposals are superseded by the current decision.