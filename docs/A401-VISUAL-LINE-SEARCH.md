# A401 — validated visual-line search hints

## Scope and status

This v0.403.0 batch reduces repeated native character-rectangle probes while preserving the
existing TextBox and decoration geometry. It follows the v0.400.0 input/scroll request coalescing
batch. Implementation, model tests and main's paired native trials are locally verified;
v0.403.0 is now published from product commit0a02530. Sustained-input, scroll-latency and
initial-layout improvement remain separate work; A401 as a whole is not complete.

The current native rectangle cost remains a justified target: the recorded v0.400.0 near1M
Enter10 case used 3,684 rect calls / 688.8973 ms inside 11 decoration passes / 711.7707 ms.
The long256Ki wrapped case used 4,481 / 908.276 ms inside 11 / 920.634 ms. These were ten separate
input calls, not an OS held-key stream. See [the prior measurement](A401-INPUT-SCROLL-COALESCING.md).
Lower call counts must not be presented as input-to-paint measurements.

## Implementation and correctness

EditorDecor still clears its native rectangle cache at the start of each RenderCore pass.
A small local value-type hint state starts empty in that same method. After two consecutive
observed visual-line spans have the same positive UTF-16 length, the next search tries that length.

The candidate is accepted only when both current-pass native coordinates establish the boundary:

- candidate Y is greater than current-row Y + the existing 0.5 DIP epsilon;
- candidate-minus-one Y is at or below that threshold.

Under the existing search's monotonic-Y contract, these adjacent predicate values prove the
first character of the next visual row. A matching span is only a search hint: no coordinates,
font widths, row heights, or line starts are inferred from it. If either check fails, the original
32-step exponential/binary search runs unchanged and can reuse any probes in the same rect cache.
An unsuccessful hint adds at most two native cache misses to that fallback for a stable layout.

Candidate addition is guarded by comparing the hint against length minus current index first.
The end-caret index is excluded, so EOF and a trailing newline's empty final row still use the
existing DrawEnd path. FirstVisibleIndex, per-pass rect correction/cache, trailing-edge calls,
render scheduling, and all drawing paths remain unchanged. Search, Undo, text caps and save guards
are untouched. Native geometry remains subject to the existing failure/disable behavior.

Hint state is never retained between render passes. Text edits, Undo/Redo, zoom, resize/wrap,
scroll, theme/mode/visibility changes and unload/reload therefore start with no old span assumption.
The TopOf instance delegate is created once in the EditorDecor constructor; neither the value-type
hint nor the search helper introduces a per-row allocation.

Files: EditorDecor.cs, new EditorDecorLineSearch.cs, source-linked EditorDecorLineSearchTests.cs,
the document-model test project link, and Directory.Build.props. No new WinUI API is used.
Source precedents: Func delegates and one-time callback storage in EditorDecorScheduler.cs;
Math.Min and the search arithmetic in the original NextLineStart; source-linked pure geometry
and policy tests in KOTU.DocumentModel.Tests.csproj.

## Model verification

The actual C# helper is source-linked into document-model tests. Its result is checked against
an independent linear first-boundary oracle, including all equal-Y monotone partitions of
lengths 1 through 12, every valid start index, and hints from 0 through length + 2:
638,976 comparisons. Additional tests cover fractional epsilon boundaries, negative/scrolled
origins, variable advances, wrong or overflowing hints, end-of-text, one-character rows,
same-pass activation/reset, and UTF-16 coordinate models for Korean, combining marks, emoji and CRLF.

Callback/cache-miss tests compare hint-disabled fallback with the enabled production helper.
They verify lower work on repeated 80/120-unit rows, no increase on selected short/alternating/
irregular models, and at most two additional unique probes per unsuccessful hint.
These are synthetic stable layouts, not native WinUI rendering or real input-latency tests.

Actual C# document-model tests: **155/155 passed**, including 13 new cases, zero failures/skips.
The source-linked Release/x64 build succeeded. The first restricted run could not connect to its
testhost within 90 seconds; the same built tests passed in the approved unrestricted run with
no rebuild. Logs and TRX files are under ignored artifacts/a401-next/agent/.

For 45 rows, the actual helper/cache model counted 372→114 unique probes at span80 and 463→119
at span120. The short-row case was 110→95; alternating1/160, alternating1/16000 and seeded
irregular cases were unchanged at 371, 621 and 395. Document lengths differ from the earlier
investigation-only JavaScript probe, so its first-visible-search totals are not identical.
Neither result is a native timing or UI smoothness claim.

Static review confirmed DrawEnd, FirstVisibleIndex, RectOf, RectOfTrailing and GetCharacterRect
method bodies unchanged; fallback code is identical after comment/parameter-name normalization.
Diff whitespace, changed XML parsing and escaped-Hangul checks passed, including new files.

## Main local validation — 2026-10-23

Release/x64 solution build completed with zero warnings/errors; all **714/714** tests passed,
including document-model155/155. Architecture20 projects and seven release-gate regressions passed.
Normal self-contained publish succeeded. Package preparation lacked the runner-specific 7-Zip DLL,
so main reused the verified v0.402.0 package DLL (SHA256
65E4C1F855F9EF6E8F0F5DF8E3F27D9EB5F07311408639DA0A1CA0B8F4871B0D).
Required package files and VLC engine/plugins contracts passed; CI gates were unchanged.
This is local package validation, not remote installer/Standalone verification for v0.403.0.

Main compared normal production v0.402.0 and v0.403.0, checking the actual process path.
KOTU.dll SHA256 values were 7DC0DB16AAFDA84B91F1D0888C839C5B19DBFA22E61087081DC134A2A9691B72
and FD76A712E056BC7E7A9DDA4436644CCA462CA179A73D25707ADAFBFA7915B1F2, respectively.
Both trials used editor1280×729.333 DIP, zoom100, Wrap/decor on, and the same synthetic fixture.
Each complete120s capture included ten separate Return calls with accessibility refreshes,
not an OS held-key stream. The initial near1M capture expired after four recorded edits and
the first long-line capture had no edits; both exploratory captures were excluded from the pairs.

| Fixture | Decoration passes | Decoration total / max ms, before → after | Rect calls / total ms, before → after | Final state |
| --- | --- | --- | --- | --- |
| near1M | 11→12 | 640.2894 / 70.7826 → 275.7805 / 33.8536 | 3420 / 620.1483 → 1311 / 256.8008 | 1048329 characters / 13114 lines |
| long256Ki wrapped | 11→11 | 849.4877 / 89.4857 → 375.922 / 41.8653 | 4151 / 838.1775 → 1260 / 366.4571 | 262154 characters / 11 lines |

Each row is **one paired trial**. The near1M after trial had one additional DecorScrollFinal pass
(1→2); TextChanged10 and the recorded scroll/selection/layout event totals matched. Native
cumulative decoration work fell56.9% /55.7%, but these are not input-to-paint or held-key latency
improvement percentages. Idle time remains in the probe window. A single native rect call can
still force layout and stall despite fewer probes.

Main confirmed final line numbers, paragraph marks and EOF; one Ctrl+Z reverted each native
coalesced ten-newline group to the original line count and clean state. After the long-line capture,
one upward600 and one downward600 wheel gesture kept wrapped text/EOF aligned, as did
100→150→100% zoom. These discrete gestures do not demonstrate high-frequency scroll throughput.
The six-line UTF8-BOM/CRLF fixture combined Korean, emoji, combining marks, RTL, tabs, an empty
line and soft wraps. Restore/maximize retained line boundaries and EOF. Search END found two
matches; search navigation followed by drag/Shift selection visibly highlighted ranges.
Search-active Enter6→7, Undo→6, Redo→7, Undo→6 and Editing→Viewing→Editing decoration hiding/
restoration passed. **Viewing selection was not verified.** UIA's focused-element field lagged
visible FindBox focus, so the first Ctrl+F result remains inconclusive rather than a shortcut defect.

Mixed Korean wrapped glyphs appeared vertically clipped. Main subsequently reproduced the same
appearance in normal v0.402.0 production. Record this as a pre-existing main observation, not a
new user report or a regression introduced by the hint batch; cause and correction are unresolved.

Main normally closed the test windows, confirmed native inventory0 and host KOTUprocess0,
and restored this batch's settings backup with matching SHA256
F11284486319B6C38F988A3556CF6076B3CD2163F2156040E2109AE8242C73D9.
The near1M/long256Ki disk fixture hashes matched their originals. No installed app was restarted
because no KOTU window existed at batch start. Evidence: artifacts/a401-next/main/ comparison.json,
ui-baseline-notes.md, main-review.md, restoration.json, raw captures and build/test/package logs.

## Remaining work

Product commit0a025308a6fe5a5d500337a00a0386316f24405e was pushed once. Remote build39940553956
and release39940553943 attempt1 both passed714/714 tests with zero build warnings/errors.
Startup, Explorer COM, installer and Standalone gates passed. Tag matched the product commit;
all eight expected assets were uploaded and nonempty, and the nondraft/nonprerelease release was
Latest when checked. Published2026-10-23 19:46:33 KST:
[v0.403.0](https://github.com/zpstudios/kotu/releases/tag/v0.403.0).
Evidence: artifacts/a401-next/main/release-verification.json and remote-test-evidence.json.
A fresh ordinary256Ki performance pair, repeated trials, high-frequency continuous wheel input,
genuine held-key input, initial long-line layout delay and end-to-end input/paint latency were not
verified in this batch. The earlier incorrect NoWrap display remains outside this fix.
A401 remains partially complete; the approved sequence proceeds to A398, then A412 and the
remaining selected items while retaining these unresolved requirements.

