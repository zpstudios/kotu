# A401 — repeated Enter and scroll decor coalescing

## Scope

On 2026-10-20 the user selected A411 first, then A401 reinforcement, with the main coordinating
and reviewing and GPT-6 Astra/high implementing serial batches. A411 v0.399.0 was released before
this product batch. This document tracks the first A401 improvement, not a replacement for the
earlier investigation or a claim that all native TextBox performance costs are solved.

## Current baseline evidence

The main used normal-entry-point v0.399.0 production publish with existing opt-in text capture,
Wrap on and synthetic local copies. Standalone capture remains disabled. The installed empty
Image/Downloads window was normally closed under the user's existing authorization; settings
and restoration information were backed up in `artifacts/a401-enter/`; restoration is completed below.

| Case | Actual input and result | Stable work |
| --- | --- | --- |
| 256Ki ordinary text, decor on, exploratory | Enter1 then a separate Enter9 burst; lines3277→3287, characters262144→262154 | TextChanged10; Layout decor10 + ScrollFinal decor11 =21 passes; Rect6934/579.949ms; decor616.422ms, max37.216ms; getter22.488ms; line index4.343ms |
| Same text, decor off | One Enter10 burst; same final lines/characters | TextChanged10; decor0/rect0; getter13.512ms; line index4.333ms |
| Near1M ordinary text, decor on | One Enter10 burst; lines13104→13114, characters1048319→1048329 | TextChanged10; Layout10 + Final11 =21 passes; Rect6987/1254.235ms; decor1292.718ms, max83.905ms; getter20.676ms; line index14.986ms |

All input cases completed the existing120s stable window and preserved all ten newlines. Idle time
is included in probe distributions; probe p95 and computer-use call duration are not input-to-pixel
latency measurements. The first on case was exploratory and its split burst is recorded rather
than presented as a controlled paired timing trial. Its event/rect counts directly establish
duplicated layout/final-scroll work. Small managed getter/index costs do not prove they are zero,
but they are not the first optimization target in this batch.

Near1M uses1048319 characters, leaving256 characters below the loaded editor cap; original
fixture copies and hashes are preserved. Enter counts are checked by final lines and characters,
not assumed from asynchronous TextChanged event counts.

## First implementation batch

Coalesce ordinary layout/intermediate/final-scroll requests into one fixed50ms collection window.
Further requests must not postpone its deadline. Initial display once and explicit visibility,
mode, theme and diagnostic changes retain immediate response and consume pending work.
Time waits run on a worker; UI dispatch applies current text/layout with lifetime and generation
guards. Continuous activity must make progress and its final request must be rendered.

Do not change rect geometry, search, dirty tracking, underlying text, Undo or cap/save guards.
Tests cover scheduler timing/lifetime; actual native rendering and performance remain main gates.
Implementation: GPT-6 Astra/high; main review/build and actual UI verification. Product v0.400.0.

The worker uses a one-shot timer and at most one pending UI callback. Later requests update the
reason/current state but do not reset the deadline. Suspend, resume and permanent disable reject
old generations; explicit changes consume waiting work. The first scheduler request can run
immediately, but constructor visibility/mode calls may consume that opportunity before measurable
layout exists. This is not a guarantee of immediate first-frame presentation.

## Main validation, 2026-10-20

Release/x64 normal build: zero warnings/errors. Document model142/142, entire solution701/701
(eight TRX files), architecture20 and release gates7 passed. Normal production publish/package
and Standalone publish passed. Both use Program.Main without thumbnail harness seams; Core
metadata reports normal=false/KOTU-Main and Standalone=true/KOTU-Standalone-Main. Standalone
text capture remains prohibited. Application DLL SHA256 in both packages:
`18955C7FCA21CD9DF9985EE472359FCC918FA094407778D08378766BB21C4AA0`.

Same normal-entry-point fixture, Wrap/decor on, zoom100, editor1280x777.333 DIP; Enter10 at end.
Each completed the existing120s capture window. The idle portion is included in probe metrics.

| Case | v0.399.0 before | v0.400.0 after | Preserved result |
| --- | --- | --- | --- |
| 256Ki ordinary (before exploratory split1+9) | decor21/616.422ms; rect6934/579.949ms | decor11/352.691ms; rect3655/331.246ms | characters262154, lines3287; Undo10 restored3277 lines and clean state |
| Near1M ordinary | decor21/1292.718ms; rect6987/1254.235ms | decor11/711.771ms; rect3684/688.897ms | characters1048329, lines13114; Undo10 restored13104 lines and clean state |
| 256Ki one long wrapped line | decor21/1738.857ms; rect8499/1719.582ms | decor11/920.634ms; rect4481/908.276ms | characters262154, lines11; Undo10 restored1 line and clean state |

Native rect cost per pass remains substantial (after maximum decor77.691ms/96.168ms). Fewer
passes are demonstrated, not a general input-to-paint latency guarantee. Actual inputs were ten
separate computer-use key calls with focus checks, not a held-key OS repeat stream. No original
reporter file was available. Geometry, caches, text, search, Undo, dirty tracking and save guards
were not changed. Full A401 completion, continuous wheel stress, initial long-line layout and
other document renderers remain separate work.

### Scroll and functional controls

Six sparse wheel calls (three down6000, three up6000) each followed by an observed screenshot:
both builds used six decor passes and2444 rect calls. Before decor190.395ms/rect171.232ms;
after decor145.691ms/rect132.392ms. Equal work counts at this sparse rate do not demonstrate
scroll throughput improvement. Both ended at vertical offset0 with unchanged262144 characters,
3277 lines, viewport1180x693.333 and extent height53718; visible row numbers aligned.

After quantitative capture ended, main maximized the test window and verified guide/number
and paragraph-mark toggles off/on without needing another edit, viewing/editing round trip,
and UTF-8 BOM/CRLF text with Korean, Greek and an emoji. Active search found120 occurrences;
appending alpha and two Return keys yielded121 matches and123 lines, with aligned last blank
line/EOF. Undo3/Redo3 and Backspace/Undo preserved expected text, counts and dirty state.
Ctrl+S produced the exact expected BOM/CRLF bytes. Undo3 and save restored the original file
SHA256 `81837F7A63C49B6E56F802704633BEE032FB3716E1800D1D5100710387285675` and120 matches.
Some immediate accessibility captures lagged actual input; final settled values and file bytes
were used, not those transient snapshots. Search focus was confirmed by visible caret/value.

Document→Image→Document explorer→tile double-click reopened clean121-line text with guides;
an externally delivered open also created a second clean document window. This is not proof of
every reattachment or rapid cancellation race: stale timer/queued work is covered by scheduler
tests, not an invented native stress run. Cap/truncation/save-guard paths were unchanged and
covered by the existing suite; no new native cap fixture was run in this batch.

All test windows were normally closed. Original three performance fixtures were hash-identical;
user settings were restored to SHA256
`53637A39DA626118171CCF392C86FDE10033EC39C33DD74E5BCF039BF8CC5C1E` before restarting the
installed app. Main restored Image explorer at Downloads with no file open. Evidence lives in
`artifacts/a401-enter/main/` (before/after JSON, eight TRX files, build/publish/package logs,
entry-point/policy metadata and restoration records).

## Release verified

Product/tag `fb9b531f8a0b4add29ec22eb16b789939a9554a6`; public build39390704159 and
release39390704230 completed successfully. Job step metadata confirms solution tests and Explorer
COM checks, plus release startup/integrity, installer and Standalone verification. Exact remote
test counts and warning totals were not collected from logs;701/701 and zero warnings/errors
above refer to the local run. Automatic approval review rejected credential extraction for
detailed logs; public job/release metadata was used without authentication or a bypass.

[v0.400.0](https://github.com/zpstudios/kotu/releases/tag/v0.400.0) was published
**2026-10-20 17:11:48 KST**, nondraft/nonprerelease, Latest. All eight expected assets, including
delta/full packages, setup/portable/Standalone and feeds, were uploaded with nonzero sizes.
Tag commit matches the product. See `remote-verification.json`, `release.json` and
`final-release-verification.json` in the local evidence directory. Final documentation is kept
in a local commit without an additional push. No other backlog item is automatically started.
