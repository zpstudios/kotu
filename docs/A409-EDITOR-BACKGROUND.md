# A409 — stable editor background

2026-10-23. User reconfirmed the document-editor hover brightness issue. Main coordinated and
reviewed; GPT-6 Astra/high investigated then implemented serially after A396 validation.
Product version:0.402.0, published as5ec5d35 on2026-10-23 at16:32:52 KST.
Release attempt1 failed one existing archive test; the explicitly approved single retry passed
all701 tests and every release gate. Eight assets, the matching tag and Latest were verified.

## Cause and scope

EditorBox used `Background="Transparent"`, but the installed WinUI1.8 default TextBox template
overwrites BorderElement.Background in PointerOver, Focused and Disabled states. In dark mode,
PointerOver uses #15FFFFFF and Focused uses #B31E1E1E. Main reproduced the change on the normal
v0.401.0 publish: focus FindBox, then move the pointer into the body with a zero-delta scroll;
the black body changed to dark gray without editing or transferring focus to the editor.

EditorBox now owns local Default/Light/HighContrast dictionaries for exactly four brushes:
TextControlBackground, TextControlBackgroundPointerOver, TextControlBackgroundFocused and
TextControlBackgroundDisabled. All four resolve to Transparent in dark, White in light, and
SystemColorButtonFaceColor in high contrast. Normal Background references the same local key.
The HC pairing follows the existing TextBox ButtonText/ButtonFace contract; WindowColor alone
would not guarantee contrast with a customized ButtonText color.

RootGrid hit-test transparency, dimensions/padding, wrapping, foreground, selection, borders,
focus visuals, native context menu and sibling FindBox/ReplaceBox are unchanged. The focused
template border can keep its outline, while ContentElement.Margin still binds to the unchanged
EditorBox.BorderThickness=0. No layout calculation, timer or new performance work was added.

## Main verification

- Release/x64 build:zero warnings/errors; full701/701 tests; normal production publish.
- Package checks passed using the existing local7zip DLL instead of CI's Program Files path.
- Actual normal-entry WinUI, dark: focused editor, FindBox focus, pointer entry, pointer exit,
  viewing/editing transitions retain the black body; no XAML resource-resolution crash.
- Search Alpha still gives1/2 with visible blue result highlighting. Native caret is visible,
  Ctrl+A returns the complete selection through accessibility, and Shift+F10 opens the existing
  Cut/Copy/Select all menu. Ordinary Ctrl+A's visual highlight was not separately confirmed
  after focus changes; do not confuse the accessibility selection result with that observation.
- Save remained disabled and the synthetic document was not edited. Both test windows closed
  normally. The fresh user settings backup was restored with matching SHA256, and the original
  installed Image/Downloads/no-open-file window was relaunched.

An isolated component probe additionally checks real compiled App resources + DocumentView
across Dark→Light→Dark and Normal/PointerOver/Focused/Disabled. It uses in-memory settings,
a separate entrypoint/bin/obj/publish, forced VSM states and no OS theme mutation. It compares
FindBox with a stock sibling TextBox to catch resource-scope leakage. All93 assertions passed
across12 samples. Editor backgrounds stayed #00FFFFFF in dark and #FFFFFFFF in light across
all four states; FindBox matched the stock reference in every sample. These forced-state
component checks supplement the real dark-mode pointer observations above.
HighContrast runtime remains untested; its dictionary/platform references are audited separately.
The probe must never be packaged as production. Evidence:`artifacts/a409-background/`.

## Additional selection-highlight batch

Before publication, the user reported invisible drag/Shift+arrow selections. Main reproduced
on normal v0.401.0 and the background-only v0.402.0: search Alpha, then drag a nonmatching
multiline range. Accessibility exposes the selected range, but there is no blue highlight;
before searching, ordinary selection is visible. This is a separate existing search interaction.

Astra/high removed the cached inactive brush and the search-state-dependent setter. EditorBox
now binds SelectionHighlightColorWhenNotFocused to TextControlSelectionHighlightColor, the
same themed key used by the default active selection. Ordinary inactive selections remain
visible too. The search controller no longer toggles the native hidden-selection state.

Main verified the final normal-entry build: zero warnings/errors,701/701 tests and publish/package.
Actual dark UI passed: pre-search Shift+Right; search result1/2; subsequent multiline drag;
Shift+Down extension and Shift+Left reduction; focus transfer to FindBox; query clear;
Escape back to the editor; Ctrl+A with visibly blue selection. Background remained black and
Save stayed disabled. The fixture hash is unchanged. Evidence:`artifacts/a409-selection/main/`.

The expanded isolated probe passed134/134 assertions (12 background samples and16 selection
samples across Dark→Light→Dark). It checks actual search/selection properties and themed brushes,
not pixel rendering. HighContrast runtime remains untested. The original93-check background
result is retained separately as `probe/background-only-results.json`.

Viewing retained the existing blue range on entry. A subsequent drag did not expose a new range,
and Shift+Right did not create one; this is not counted as a highlight pass. Static review finds
no TXT drag-pan handler and no product Shift-selection blocker; native read-only input needs
separate comparison. No additional behavior change was made for that observation.
The user initially did not remember the mode, then clarified that the reported selection
highlight failure occurred in Editing. They explicitly cautioned that this does not confirm
Viewing works correctly. Keep the user's Editing report and main's separate Viewing observation
distinct; the latter remains unresolved.

## Publication

A396 v0.401.0's combined commit/push attempt was rejected by automatic approval review because
the current-version default-branch mutation lacked explicit approval. No part of that command
executed. The user explicitly approved sequential v0.401.0 and v0.402.0 push/releases on2026-10-23.
A396 was published first as9bd38f7: build39894524860/release39894524827 succeeded; eight assets,
matching tag and Latest confirmed at2026-10-23 12:28:14 KST. Before A409 publication, the user
reported missing manual-selection highlighting. Main reproduced on the normal v0.401.0 build:
drag/Shift selection is visible before search, but disappears after an Alpha search when manually
selecting a nonmatching range. Accessibility still exposes that range and EditorBox focus.
That separate Astra/high implementation and main visual-validation batch passed as recorded above.
The approved v0.402.0 publication now includes both the background and selection corrections.
The exact first-batch patch is retained at `artifacts/a396-hint/main/approved-batch.patch` so
the two product batches can still be committed/pushed in order without combining their versions.

v0.402.0 commit5ec5d35 was pushed after v0.401.0 verification. Build39896995024 passed701/701
(verified through connected GitHub job logs). Release39896995019 attempt1 passed its build but
failed one of701 tests: ArchiveJobCoordinatorTests.CreationCopiesSourcesAndUsesWorkerForBothFormats
(use7z:true), line170, Assert.NotEqual(ownerThread,workerThread), both27. All700 others passed.
Archive/Core implementation and the affected test are unchanged from v0.401.0. A focused local
repeat passed10 runs ×2 formats=20/20; no test was weakened or skipped. The precise runtime cause
is not proven. Astra/high's read-only audit found that .NET8 releases managed thread IDs when
Thread objects are destroyed, while the test retains only the integer ID before awaiting.
An async test caller is not guaranteed to stay alive throughout the test. ID reuse is therefore
a plausible test-lifetime explanation, not an observed CI fact. Product dispatch remains
Task.Run→CreateAsync→new ModuleWorker→new Thread; there is no direct-caller backend branch.
If a single approved retry fails again, capture Thread object identity/liveness and worker name
before changing either the product or the test. Do not classify this failed run as successful.

Automatic approval review rejected the failed-job rerun itself as lacking explicit rerun approval,
despite the earlier sequential push/release approval. The user subsequently explicitly approved
exactly one failed-job retry with all existing gates retained. The authorized connector call
returned GitHub403 `Resource not accessible by integration`; no retry was scheduled. A fresh
jobs read still returned the original failed job120229626113. Edge was signed out of GitHub,
so main opened the sign-in page returning to the same run and requested user sign-in.
Evidence of that resolved access blocker:`artifacts/a409-selection/main/retry-access-status.json`.

After the user confirmed sign-in, main used GitHub's Re-run failed jobs action exactly once.
Release39896995019 attempt2, job120294131619, completed successfully on the unchanged5ec5d35.
Decoded logs confirm701/701 tests, zero build warnings/errors and both archive-creation formats
passing. Explorer COM, production startup, installed-package and Standalone checks all passed.
The v0.402.0 lightweight tag points exactly to5ec5d358e963573bb337099b02943f64b1285795.
All eight expected assets are uploaded and nonempty; the release is neither draft nor prerelease
and is Latest. Published2026-10-23 16:32:52 KST. The first failure remains unexplained; passing
one retry is not proof of a root-cause fix. No further retry, test weakening or source change occurred.
Evidence:`retry-start.json`, `remote-jobs-attempt2.json`, `retry-test-evidence.json`,
`remote-run-attempt2.json`, `release-verification.json` under `artifacts/a409-selection/main/`.
Final verification documentation is committed locally without an additional documentation-only push.
