# A405 — common recording folder investigation

## Current decision

The user confirmed one Windows Documents/KOTU default and one shared folder-change UI for
audio, screen and future camera recordings. Existing files must not move. A running session
keeps its destination; later sessions use the newly committed common setting.

Read-only investigation is complete on2026-10-23, after A412v0.405.0. Main reviewed the
GPT-6 Astra/high report and artifact proof. No product/version/settings/UI changes were made.
The emailed choice for two different legacy custom folders remains unanswered. Neither the
recommended one-time common choice nor immediate Documents default is adopted by this report.

## Findings and proposed boundary

- Only RecordView reads/writes `record.audioFolder` and `record.videoFolder`; SettingsView has
  no recording folder control. A single Record card is sufficient; no duplicate Settings picker.
- Each RecordView caches its folders. Replacing two keys with one would leave other open views
  stale. A narrow RecordModule-owned coordinator should publish the committed common folder
  specification, and each accepted start should capture an immutable snapshot from it.
- Existing backends already receive a captured sibling temporary path. FinishAsync retains
  the captured destination and publishes without overwriting, preserving partial output on
  failure. Keep that transaction and actual result-folder behavior intact.
- Proposed new setting: an empty value means the current Windows Documents/KOTU default;
  an absolute value means custom. Resolve the known folder on the worker for each new default
  session, preserving Windows redirects. Do not create or inspect legacy folders to classify them.
- Missing/default legacy values, one custom, equal customs and conflicting customs require
  separate classification. Current settings do not record whether an explicit old-default path
  was deliberately chosen; historical redirected defaults cannot safely be guessed from names.
- The conflicting-custom branch is the policy-dependent boundary. Preparing pure classification,
  folder resolution and shared-state tests is possible; shipping a partially migrated UI or
  dormant helper is not the selected next batch. A402 camera and A403/A406/A407 recording work
  still need coordinated integration and their own actual capture gates.

The proposed coordinator serializes its own writes and notifications, not every unrelated
application settings write. Implementation review must account for the existing global
Set/Save/rollback behavior and avoid claiming a new global atomic persistence guarantee.

## Evidence and next step

Artifact proof18/18 passed using unchanged RecordingOutput helpers with synthetic artifact
folders. The read-only Documents lookup matches the Windows known-folder API and is redirected
under OneDrive on this machine. No actual personal directory path is recorded in the proof;
no Documents/KOTU folder, user setting, device session or app window was created.
The initial probe's separator expectation was corrected without changing product code.

This does not validate new migration, coordinator/UI, native recording, disk-full/ACL behavior
or camera support. Detailed file/line map, alternatives and test gates are in
`artifacts/a405/agent/investigation.md`; proof results are `proof-result.json` beside it.
Policy-dependent product implementation waits for the answer. Meanwhile the main proceeds
with independent authorized A408 work, keeping actual implementation delegated serially.
