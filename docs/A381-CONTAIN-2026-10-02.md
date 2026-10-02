# A381 PDF Contain visible viewport — 2026-10-02

## Evidence and cause

QA §4 observed the lower border hidden in both Document and All Readable. Source inspection confirms
`MainWindow.BottomBar` overlays `CenterArea` rather than reserving a row, while `PdfPane.ApplyFitAt`
previously divided the full `ScrollViewer.ViewportHeight` by the page/frame height. The existing list
bottom padding only extends scrollable content; it does not reduce that fit input. A height-limited
page therefore occupied space behind the bar. This is a source-confirmed geometry mismatch; no new
GUI reproduction was performed in this implementation pass.

## Change and review conditions

| Condition | Implementation |
|---|---|
| Direct Document and All Readable | Shell supplies its actual BottomBar via `IBottomOverlayConsumer`; All Readable forwards it to existing and newly created children. |
| Bar visible, resized, or UI scaled | Convert actual bar bounds into the ScrollViewer coordinate space; use the top of an overlapping bottom bar as the visible height. No fixed bar-height subtraction. |
| Ancestor hidden, disconnected root, or zero-size bar | Full viewport height; visibility is checked through the bar's ancestors. |
| Initial layout and subsequent mode/fullscreen changes | Fit records zero-size inputs and retries after layout changes; width/visible-height equality prevents repeated fits on unchanged layout. |
| Reentrant layout | A `try/finally` fit guard blocks UpdateLayout recursion. There are no cross-tree event subscriptions to leak when bars are reparented. |
| Fit semantics | Contain remains shrink-only; Fit height uses the same visible height. Fit width and Original retain their original zoom formulas and horizontal alignment. |
| Later page anchoring | Page offsets now include the 2 DIP border plus 16 DIP margins, matching the frame used in fit calculations. Previously offsets accumulated 2 DIP of error per prior page. |
| Fractional transform rounding | A 0.5 DIP bottom-edge tolerance prevents an almost-touching bar from being ignored. |

Viewport dimensions were already treated as physical, zoom-independent input by the existing fit,
minimum-content-width (`viewport / zoom`), and zoom-center calculations. The measured transform ends
at the ScrollViewer itself, not its zoomed content. Content zoom and offset do not change that frame.

## Validation and remaining limits

- DocumentModel tests: **91/91 passed**, including **14 new geometry regression cases** covering Letter
  page/frame bounds, overlay overlap and non-overlap, zero/full occlusion, scaled/fractional bounds,
  resize, hidden-bar height, Contain shrink-only, zoom limits, Fit width and Original invariance.
- Full app build: **0 warnings, 0 errors**. `TransformToVisual`/`TransformBounds` are new usages in this
  repository; compilation validates the signatures. Their runtime visual result remains unobserved.
- Static review: whitespace diff check and Korean Unicode-escape scan passed; no legacy identifiers changed.
- GUI still to verify: the QA three-page PDF at 150% UI scale in Document and All Readable, initial open,
  Fit width → Contain reselection, all three page anchors, resize, bar hide/show and fullscreen, additional
  UI scales, and Fit width/Original alignment. The existing minimum zoom can prevent full containment in
  exceptionally small windows. Padding remains the previous scroll-end policy; it is separate from fit.

Final integration: Release/x64 solution build passed with 0 warnings and 0 errors; all **517/517 tests**
passed with normal Windows permissions. Architecture (20 projects), release gates (7 bypass cases),
and Explorer COM selection smoke (3 verbs, long lists, nonce/replay defense) passed. Evidence:
`artifacts/a381-build.log`, `artifacts/a381-tests.log`, `artifacts/a381-shell-smoke.log`,
`TestResults/a381-final`. Independent review found no confirmed defect and checked the bundled WinUI
template: ListView padding is on ItemsPresenter, while ScrollViewer padding is 0, so overlay bounds and
viewport height share the expected frame. This still does not substitute for the listed GUI checks.

Version is v0.376.0. On 2026-10-02 the user explicitly authorized release. A380 `72895f4` and A381
`df59305` were pushed sequentially to GitHub `zpstudios/kotu` master, superseding the earlier automatic
approval review block. GitHub build `36972856131` and release `36972856268` succeeded. The final release
was published at 15:32:55 KST on 2026-10-02, is neither draft nor prerelease, and is the Latest release.
Tag/source match `df59305`. Setup, Portable, Standalone, full/delta packages and three update metadata
files were present (8 assets). Actual installation, installed-file consistency, startup and Standalone
gates passed. Runtime GUI checks listed above remain unperformed.
