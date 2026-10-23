# A396 — Always-visible find and replace

## 2026-10-23 reinforcement — v0.401.0

Removed the idle `Enter text to find` helper in both initialization XAML and runtime presentation.
The empty result label collapses, including its minimum width; the keyword input remains visible.
The options detail retains `Literal, ignore case` or `Loaded preview only` without a leading separator.
Searching, no-match, result count and unavailable labels keep their existing behavior.

GPT-6 Astra/high implemented one batch. Main reviewed the diff and verified Release/x64 build
(zero warnings/errors), all701/701 tests, architecture20, release gates7 and normal publish.
Package validation used the existing local `artifacts/native/7zip/7z.dll` in place of the CI-only
Program Files path; all other package checks were unchanged. No dependency change was made.
Actual WinUI: empty input/idle options, `Searching...`→`1 / 2`, `No matches`, clearing the query,
and retained keyword field/selection highlighting passed. Responsive thresholds were unchanged;
no new narrow-width, preview-cap or inaccessible-document runtime test was performed.

The same build reproduced A409: with search input focused, moving the pointer into the document
(zero scroll delta) changed its black background to dark gray without editing the document.
A409 is the next separate batch. Evidence/logs: `artifacts/a396-hint/main/`.
Automatic approval review initially rejected the combined master commit/push before execution.
The user explicitly approved sequential v0.401.0 and v0.402.0 push/releases on2026-10-23.
v0.401.0 was published first as9bd38f7: build39894524860 and release39894524827 succeeded.
Startup, installation, Standalone and Explorer COM checks passed; eight uploaded release assets,
the matching lightweight tag and Latest were confirmed. Published2026-10-23 12:28:14 KST.
Remote test counts were not extracted;701/701 above describes local results.
The exact product batch patch is retained at `artifacts/a396-hint/main/approved-batch.patch`.

Implementation date: 2026-10-19. Starting source: `bbc62bf`, product `0.395.0`.
This change sets product version `0.396.0`. No commit, push, deployment, application launch,
user document edit, or settings change was performed by the implementation agent.

## Behavior

- The 32-DIP keyword TextBox is always present in the document bottom bar, immediately after
  `N changed`. Hiding a zero change count does not hide the keyword field. Empty/PDF/HTML view
  disables the field with an English placeholder and explanation. HTML source is searchable.
- Ctrl+F focuses the field and selects its contents. Typing searches after a worker-side 150ms
  debounce. Matching is non-overlapping, literal `OrdinalIgnoreCase`; backslash sequences are
  literal, and original UTF-16 positions are used without newline normalization.
- Enter/F3 and Shift+Enter/Shift+F3 navigate and wrap. Enter only navigates from a search input;
  body Enter remains a newline. Escape from search controls returns focus to the document.
  Ctrl+H or the search options button opens navigation and replacement controls.
- The native current selection provides highlighting, including while the keyword field has
  focus. Originally the previous unfocused brush was restored when no current match remained;
  A409 v0.402.0 removes that restore because it hid manual selections after searching, and
  keeps the editor's inactive selection bound to the same theme resource as active selection.
  Bringing a match into view queries at most two rectangles: current match and coordinate origin.
- Ordinary body edits and mode refreshes update search results without moving the caret.
  Keyword changes, including completed IME input, and explicit navigation request selection.
  A user selection change while a search is pending cancels that movement intention.
- Markdown find exposes source while retaining read-only viewing. The existing Edit action is
  required before replacement. Capped/truncated documents show “Loaded preview only” and every
  replacement path rejects them. Existing save/encoding/newline/external-change paths remain.
- Replacement is computed on a worker. A single replacement edits its range; Replace all edits
  one contiguous range from the first through the last match, preserving text between matches.
  Both use one `SelectedText` assignment, never `Text = ...`. Native one-step undo was verified
  for current and all replacements in the actual editor. Growth above 16,777,216 UTF-16 units is
  rejected with a persistent English error instead of allocating an unbounded repeated string.

## Layout and key routing

The bar has 12 columns, with filename star space at column 4, change count at 5, search at 6.
The count is capped at 90 DIP with ellipsis to prevent extreme numbers from pushing out input.
Print and Fit remain directly available at every supported width.

| Module bar width | Keyword width | Navigation/count | Existing guide/page/zoom controls |
| --- | ---: | --- | --- |
| Below 700 DIP | 120 DIP | Search options flyout | More document tools flyout |
| 700–899 DIP | 180 DIP | Search options flyout | More document tools flyout |
| 900–1099 DIP | 180 DIP | Search options flyout | Inline |
| At least 1100 DIP | 180 DIP | Inline and flyout | Inline |

The editing/viewing text label is shown from 1100 DIP. Existing view-toggle, save, new, print,
and Fit button behavior is preserved. Controls retain 6-DIP spacing. With the smallest documented
module bar of approximately 594 DIP, fixed controls plus maximum 90-DIP change label need about
526 DIP, leaving roughly 68 DIP for filename space. Actual minimum-width layout passed at
100% and 150% application scaling in the default dark theme.
The new icon buttons have English tooltips and automation names. All overflow operations remain
keyboard reachable. Search options tooltip includes result count and preview scope at compact widths.

`ITextSearchTarget` is the minimal UI-independent shell contract. `MainWindow.PreviewKeyDown`
forwards only relevant commands, excludes Jobs/S4, and respects handled keys. All Readable forwards
to its current child. The document checks its editor/bar/search popup ownership, other input fields,
PassThroughTag ancestors, unrelated popups, IME composition, and auto-repeat. The search flyout has
its own equivalent key route because popup events need not traverse the main root. Ctrl+C, Tab,
ordinary characters, and other shell shortcuts have no new binding.

## State and lifetime audit

| Resource or transition | Invariant and cleanup |
| --- | --- |
| Snapshot | Cached `EditorText`, `_openSeq`, independent `_textRevision`, completeness, current replacement permission |
| Synchronous edit invalidation | `TextChanging` advances revision and cancels searches/replacement before asynchronous `TextChanged`; it does not mutate the visual tree |
| Document open | Generation changes immediately disable/invalidate search; readiness is only set after the current buffer is applied |
| UI result application | Requires current loaded generation/revision/query/completeness; a permission change while computation was pending triggers a new calculation |
| Replacement application | Rechecks current snapshot, query, full-buffer state, editability, save state, composition and cancellation after the worker returns; current replacement also rechecks native selection |
| Search work | One loop task, at most one running request and one latest mailbox request; superseded work is cancelled |
| UI delivery | One queued callback and one replaceable completed-result mailbox, independent of request count |
| Replacement work | One task per view guarded by `_replaceBusy`; query/text/replace-value changes and unload cancel its token |
| Unloaded | Cancels work, disposes worker, disconnects dispatcher/apply delegates, hides flyouts; queued deliveries become inert |
| Loaded again | Creates a new worker for the current lifetime and resubmits the current query |
| Multiple windows | No shared mutable search state, callbacks or query data |
| Native handlers | TextBox events belong to their view; no new static event, polling timer or root event subscription |

## Validation and evidence boundary

The implementation adds 18 model cases in `TextSearchTests.cs`: literal/case/empty/no-result,
Korean/surrogate/mixed-newline offsets, non-overlap, both wrapping directions and caret anchoring,
stale generation/revision/query, preview/read-only guards, single-range replacement and preserved
surrounding text, empty replacement, 1,048,576 matches, cancellation, expansion guard, rapid latest
requests, one queued delivery, cancel/dispose after enqueue, rejected dispatch and independent views.

- Focused model/test build: Release/x64, warnings as errors, **0 warnings / 0 errors**.
  Evidence: `artifacts/a396/model-build.log`. Runtime tests are handed to the main agent because
  sandbox testhost localhost failures are already established; no redundant failed test run was made.
- Initial production App build: **0 warnings / 0 errors**, 3m44.81s,
  `artifacts/a396/app-build.log`. Final build incorporating current-match highlighting, scroll,
  and last state-transition fixes: **0 warnings / 0 errors**, 1m44.89s,
  `artifacts/a396/app-build-final.log`. Both build processes have exited successfully.
- Architecture: 20 projects, reference/cycle/model-isolation/solution mapping checks passed.
- Release workflow: seven bypass regressions rejected; contract check passed.
- Changed XML parses; `git diff --check` passes; new source/test Korean-escape scan has no matches.
- The Windows PowerShell 5.1 architecture invocation cannot use `Path.GetRelativePath`; it was
  rerun successfully with bundled PowerShell 7.6.5. This is a tooling mismatch, not a product failure.
- No new test harness was added. The main agent selected the existing isolated Standalone build
  for real shell/All Readable UI verification and elected not to rebuild the unrelated thumbnail
  opt-in harness in this batch.

### Main reviewer: first actual UI pass

The main reviewer published the existing Standalone configuration and drove the real MainWindow
and All Readable through Windows input. Its separate instance key and in-memory settings isolate
the test from the installed application. Synthetic files live in `artifacts/a396/fixtures`.

- Focused tests: **128/128**, including the 18 new cases. Actual testhost execution was outside
  the established sandbox localhost restriction; `artifacts/a396/main/FocusedResults` contains TRX.
- Ctrl+F focuses the always-present field; typing finds case-insensitive matches and leaves the
  current body match visibly selected while the field has focus. Enter and Shift+Enter navigate.
- F3/Shift+F3 wrap between the first and last matches of a 252-line document and scroll the
  off-screen match into view. Ordinary editing at the document end preserves the typing caret.
- Replace current and Replace all each undo in one Ctrl+Z, restoring mixed case, Korean and emoji.
  Dirty counts and Save availability settle back to clean. All Readable routes Ctrl+F/H and Ctrl+S
  from the keyword field; saved replacement content is exact.
- Saving an editable copy preserves UTF-8 BOM plus CRLF; another preserves UTF-8 without BOM plus
  LF. Evidence: `crlf-save-check.json` and `lf-replace-save-check.json` in the main artifact folder.
- At the minimum window width, the input remains after `N changed`, with Print/Fit and the overflow
  control visible at application scaling 100% and the native Windows setting of 150%. The zero
  change count collapses without hiding the input.
- Viewing mode disables replacement. Returning to Edit makes it available without retyping the
  query. A capped 1-MiB preview reports only its loaded match (`1 / 1 · Loaded preview only`) and
  disables all replacement controls; the second match beyond the preview is not counted.
- Markdown Ctrl+F exposes read-only source and finds `한글😀` as one match. HTML rendered view
  retains the field in its disabled state. Multiple test windows and module/file transitions ran.
- Escape after replacement incorrectly reached the unsaved-changes dialog. The reviewer cancelled
  it without losing text and requested the focused correction below. The first build is not counted
  as passing Escape acceptance.
- In the capped single-line fixture, the first match was counted but not visible. The source now
  moves index zero to scroll origin without relying on native geometry. A second UI run still
  showed only the repeated `x` characters, already before a search. The pre-existing A401-c native
  long-line display limitation remains; this change does not claim to fix document rendering.
- A second run verified 10,000 matches multiplied by a 2,000-character replacement: the size-limit
  error remains visible, Save stays disabled, and the original body remains unchanged.

### Final UI follow-up

The rebuilt executable (`KOTU.dll` SHA-256
`44B03C8194A102DE9A736F34351A2A1B6976BEB1D22E8FA1263F37BD530FA1DD`) passed the
previously failing sequence: Ctrl+F `alpha`, Enter to the second match, Ctrl+H `OMEGA`, Replace,
then one Escape. It returns to the body without the unsaved-changes dialog. Ctrl+Z immediately,
without a body click, restores the original text and proves editor focus was returned.

Ctrl+H followed by Ctrl+F and actual typing changes only the keyword field. Escape from that
field returns to the body, and a separate subsequent Escape follows the existing content-close
path. All Readable also passes Replace all, Escape and immediate one-step Undo. The resulting
empty Document keeps the disabled search field visible. Both Escape fixtures remain byte-exact.

The reviewer closed all isolated test windows normally. The installed `1-KOTU` window remains;
settings SHA-256 is unchanged and all five baseline fixture hashes match. No installed-app input,
installation or user-settings restoration was needed. Modified files were synthetic editing copies.

Remaining device coverage: actual IME composition/native Tab, light/high-contrast themes, extreme
count labels, external-save conflict and sustained close/reopen races. The native long-line display
limitation remains under A401-c. Unrun cases are not claimed as passed.

## API evidence

Repository precedents: `SelectedText` in document Tab insertion; selection properties in existing
editor operations; `GetRectFromCharacterIndex` and origin correction in `EditorDecor`; `ChangeView`
in document/render/PDF scrolling; `TryEnqueue` throughout document/shell; popup enumeration in
`MainWindow.IsAnyPopupOpen`; per-control popup and focus handling in existing toolbar controls.

New event/property semantics were checked against official Windows App SDK documentation:

- [TextChanging](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.textbox.textchanging?view=windows-app-sdk-1.8): synchronous, unlike TextChanged; visual-tree mutations are avoided.
- [TextCompositionStarted](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.textbox.textcompositionstarted?view=windows-app-sdk-1.8): IME lifecycle and composition event ordering.
- [SelectionHighlightColorWhenNotFocused](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.textbox.selectionhighlightcolorwhennotfocused?view=windows-app-sdk-1.8): default template highlight is transparent, so an explicit active-search brush is needed.
- [SelectedText](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.textbox.selectedtext?view=windows-app-sdk-1.8): range content replacement. One-step undo was verified in the native editor.

If the new unfocused-selection property proves incompatible on a target runtime, the minimal fallback
is to remove that brush assignment while retaining native focused selection; this would leave the
unfocused-highlight acceptance condition unmet and must not be silently treated as complete.

## First UI review follow-up: search popup Escape

The main reviewer reported that, after replacing the current `ALPHA` match with `OMEGA`, Escape
opened the document's unsaved-changes dialog instead of ending at popup dismissal. The reviewer
cancelled that dialog and retained the document. The source path allowed the popup presenter to
fall outside the search-content focus check when the focused Replace button became disabled.
Native light-dismiss could also remove the popup before the shell's bubble-stage popup check.

The first correction expanded search ownership to the actual flyout presenter and used a
dispatcher-turn dismissal marker. Its rebuilt Standalone executable still reproduced the same
unsaved-changes dialog, so that attempt is recorded as failed. The reviewer again cancelled the
dialog and restored the synthetic document through one native Undo.

The second correction retains a separate search interaction and an Escape gesture latch until
KeyUp. Native popup dismissal can no longer erase search ownership merely by changing focus.
The shell and popup observe KeyUp; explicit body pointer/typing ends search interaction. Popup
handlers detach at Closed/Unloaded, and unrelated popups and PassThrough areas retain priority.
There is no polling or dispatcher-time assumption in the key-consumption lifetime. The actual
retest of this second correction passed in Document and All Readable as detailed above.

Official [Closing](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.primitives.flyoutbase.closing?view=windows-app-sdk-1.8)
and [Closed](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.primitives.flyoutbase.closed?view=windows-app-sdk-1.8)
event signatures were checked. XML and diff whitespace checks pass. Build/test logs and failed
UI observations remain under `artifacts/a396`; failed attempts are not counted as passing checks.

## Final local regression and packaging

The main reviewer rebuilt the full solution with the normal production policy, warnings as errors,
and the thumbnail harness disabled: **0 warnings / 0 errors**, 1m18.72s. All eight test assemblies
passed **663/663**, with zero failures or skips; this includes DocumentModel **128/128**. TRX totals
were independently aggregated into `artifacts/a396/main/test-summary.json`.

The 20-project architecture checks and seven release-gate regression checks passed. Normal
self-contained publish and package preparation completed successfully. Because the standard local
7-Zip installation path is absent, a temporary copy of the packaging script uses the existing
`artifacts/native/7zip/7z.dll`; required application/media/license files and VLC plugins passed the
same checks. No repository packaging script was changed.

Production metadata confirms `KOTU.App.Program.Main` and no thumbnail harness types, methods or
fields. The normal Core assembly independently reports `IsStandalone=false` and instance key
`KOTU-Main`; the UI test executable used the separate Standalone policy. Core SHA-256 is
`7D54BEA261796FB5EE38C8F58160FA6C88487CEC10A8DFC5291FD444CB58CC73`. Evidence is in
`production-metadata.json` and `production-policy.json`.

## Remote delivery

Product commit `cd0ed8b31fbacd9d01609c051e7e4640ff6c495d` was pushed once to `master`.
[Build 39207125677](https://github.com/zpstudios/kotu/actions/runs/39207125677) and
[release 39207125639](https://github.com/zpstudios/kotu/actions/runs/39207125639) both completed
successfully. Each passed **663/663 tests**, with **0 warnings / 0 errors**, and the three actual
Explorer COM verbs. Release startup, installer/package integrity and Standalone verification all
passed. The Standalone log confirms memory settings, disabled updates, preserved AppData/registry
and normal-exit cleanup.

[v0.396.0](https://github.com/zpstudios/kotu/releases/tag/v0.396.0) was published as a normal release
on **2026-10-19 at 15:03:59 KST** (`06:03:59Z`). It is Latest; the tag and remote `master` both point
to the product commit. All eight assets are uploaded: Setup, Portable, Standalone, full package,
delta package (615,895 bytes), `RELEASES`, `releases.win.json` and `assets.win.json`.
Compact evidence is saved in `artifacts/a396/main/remote-validation.json`.

Final verification documentation is committed locally without an additional docs-only push.
The subsequent WhatsApp copy-error report (A400) and right-click Copy image request (A410) are
requirements only; they are not included as implemented features in this release.
