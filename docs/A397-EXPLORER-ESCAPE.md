# A397 — Close an empty file explorer with Escape

## Scope and orchestration

On 2026-10-19 the user selected A397. The main agent owns scope, review, validation,
documentation and release. GPT-6 Astra/high investigates and implements serially, with a
main-agent validation gate between batches. Starting HEAD: `058b387`; product baseline:
v0.397.0, `b231ddd`.

Only the empty file-module explorer (S1) gains an Escape window-close action. The action must
reuse the existing current-window close path and retain unsaved-content, active-job and settings
persistence protection. S4 Escape returns to the underlying content. Existing full-screen and
content-close steps, local cancellation and key-repeat protection retain priority. Settings,
hardware, Record and the empty shell do not become new Escape-close targets. A407 stealth
recording is a separate pending feature; no new recording/tray behavior is implemented here.

## Baseline evidence and risks

Main ran the existing v0.397.0 Standalone build with a synthetic image, leaving the installed
KOTU window untouched. The first Escape closed the image and returned to the empty image
module explorer. A separate second Escape left the same explorer window open. This is an
actual UI observation, matching the missing final branch in `MainWindow.OnShellEscape`.

Code findings: `IsEmptyFileModule` distinguishes file modules without a path or untitled content.
`ConfirmThenCloseAsync` already owns current-window unsaved/job/settings guards. Explorer
rename and cut-mark cancellation consume Escape before shell bubbling. A396 documents native
popup dismissal before the bubble route, so simply checking currently open popups is not a
complete proof that the same Escape cannot also close S1.

The primary review risk is a local cancellation, content-to-S1 transition, held key or repeated
event closing another layer/window in the same physical key press. New window closing must not
be moved ahead of child controls in PreviewKeyDown or be duplicated in KeyUp. Actual menu and
rename cancellation are required validation gates.

## Implementation and review

One product batch adds the final `IsEmptyFileModule` branch to `OnShellEscape` and calls
`ConfirmThenCloseAsync`. A successful `TryCloseContent` now returns immediately, so a synchronous
content-to-S1 transition cannot also close the window. Already handled or repeated Escape events
return before the Jobs panel branch. Existing screen-mode, S4, search and local cancellation
routes retain their ordering. No new key hook, close policy, module interface or recording behavior
was introduced. Product version: **0.398.0**.

The main agent reviewed the actual diff and common close path after the Astra/high implementation.
The existing guards retain unsaved confirmation, close-in-progress exclusion, manager last-window
checks, the background-job idle lease and serialized settings persistence. Existing Core tests
cover close approval invalidation and background-job active/canceling/password-wait/idle behavior.
No policy abstraction or test duplicating these few UI routing lines was added.

## Actual UI validation

Main used the normal-entry-point Standalone build with synthetic local fixtures. The installed
KOTU window remained open throughout; its handle was still 722786 after the checks. Separate
physical key presses were issued through the Windows computer-use tool.

| Scenario | Observed result |
| --- | --- |
| Image content → Escape → S1 | Content closes; the same window stays open |
| Image, Document, Video and Archive S1 → separate Escape | Current test window closes normally; its final process exits |
| Second Image S1 window → Escape | Only that window closes; the first window and its image remain |
| Start menu, extension filter, file context menu → Escape | Menu closes; S1 window remains |
| Left-list folder rename → Escape | Edit box closes; original folder name and window remain |
| File context-menu Cut → Escape | Dimmed cut mark clears; window and source file remain |
| Image S4 → Escape | Returns to the same image |
| Full-screen Image S4 → successive separate Escapes | S4 closes, then full screen, then content, then S1 window; one layer per press |
| Video S4 → Escape | Same video remains open and playing; position advances from 0:05 in S4 to 0:11 after return |
| Edited synthetic text → Escape | Unsaved-changes dialog appears; Escape cancels it and retains the edit |
| Document Ctrl+F → Escape | Focus returns to the body; no close confirmation or window closure |
| Undo synthetic edit → Escape | Clean document returns to S1 without closing the window |
| ZIP extraction waiting for password → content Escape | Returns to Archive S1 while the job remains active |
| Active job + last S1 window → Escape | Window stays open and Jobs explains why closing is blocked |
| Jobs panel → Escape → another Escape | First hides only Jobs; next close attempt is blocked again |
| Cancel synthetic job → Escape → separate Escape | Jobs hides, then S1 window closes normally |

No password was entered. The original fixture files were not saved, moved or renamed. Their four
SHA-256 hashes and the user's settings hash match the baseline; the rename folder remains.
All A397 test windows/processes were closed normally. Local evidence and scripts are under
`artifacts/a397/`; UI observations above were read from actual screenshots/accessibility state,
not inferred from unit tests.

## Local build and package gate

- Focused Core: **308/308** passed before UI validation.
- Normal Release/x64 solution build: **zero warnings, zero errors**.
- Full regression suite: **687/687**, eight assemblies, zero failures/skips.
- Architecture: 20 projects passed. All seven release-workflow bypass regressions rejected.
- Standalone and normal self-contained publish plus normal package preparation passed.
- Normal metadata: `KOTU.App.Program.Main`, no thumbnail harness types/methods/fields;
  Core `IsStandalone=false`, `InstanceKey=KOTU-Main`.
- Production and UI-tested Standalone KOTU.dll SHA-256 both equal
  `36C122BD08EF30FB5A3377E59DB86525B133E10C31AFA0D27D7F6672A5046686`.
- Changed C# passed the Korean-escape scan; props XML parses; `git diff --check` passes.

The packaging check used the existing repository 7-Zip DLL through an ignored temporary script.
Tracked packaging scripts were unchanged. Normal startup/installer/Standalone release checks
will run in CI, keeping the installed single-instance user window undisturbed locally.

## Remote release verification

Local product validation is complete. Product commit/push and remote build/release verification
are pending. Final deployment-only documentation will remain in a local-only commit.

## Validation limits

The tool sends discrete key presses, so physically held Escape across window activation was not
directly exercised. Repeat protection was reviewed in code, not reported as a held-key UI pass.
IME composition cancellation, central-tile rename, blank-area context menus, other themes/scales,
long-running races and settings-write failure were not exercised. Audio/AllReadable S1 and the
Settings/Hardware/Record/empty-shell exclusions were checked through the shared predicate and
unchanged routes, not a separate UI run for each. A407 stealth recording remains a separate
feature; this change only joins the existing shared close path.
