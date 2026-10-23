# A398 rotated image layout

Status: v0.404.0 locally validated, release pending. Normal publish/package and the normal application fit UI gate passed after main review. No commit, push or release has been performed for this follow-up. A398 PNG eXIf, actual thumbnail-provider and other codec boundaries from `A398-EXIF-ORIENTATION.md` remain open; this is not completion of all A398 work.

## Problem and chosen scope

The matched native comparison in `artifacts/a398/agent/fit-layout-followup.md` found the same pre-existing geometry defect in v403 TIFF8 R0 and v404 TIFF8 R90: a landscape visual kept its portrait layout extent, appeared too low, and scrolled vertically despite fitting the viewport. The v404 direction was correct; resetting offset to zero did not fix the bounds.

`RotatedImageHost` is a single-image local WinUI Panel. It exchanges available axes before calling the real Image.Measure, exchanges the resulting DesiredSize for its own measured extent, and arranges the unrotated child about the host center. The existing Image.RenderTransform remains the only visual rotation. The host changes no source pixels and reads no dimensions or metadata. `RotatedImageLayout` contains only the axis exchange and centered child slot arithmetic.

The XAML places that host between the existing ScrollViewer and Image. ApplyRotation publishes the same manual-only axis-swap state to the host; an actual axis change invalidates measure. Switching 0/180 or 90/270 only changes the existing renderer because the layout shape is identical.

UpdateFit, Original physical pixel/DPI conversion, ScrollViewer and its presenter, wheel/zoom limits and anchor arithmetic, Shift bypass, mouse capture and pan, all event hooks, source loading, clipboard, print, PSD, preload and cloud policies are unchanged. No Canvas, extra decoder, image translation workaround, ActualWidth feedback loop or polling is introduced. Source absence and clear use native Image measurement rather than guessed dimensions.

## Largest trap and native control evidence

The important risk was that a rotated child needs a negative pre-rotation slot coordinate on one axis. Merely moving pixels could still leave a wrong extent or WinUI layout clipping. The isolated native control probe exercises the real Image, host and ScrollViewer together, measures transformed bounds and captures the host with RenderTargetBitmap to inspect alpha coverage.

Final probe: `artifacts/a398-fit/agent/probe/publish/results.json`, Passed=true, 90 recorded samples, no failures:

- 64 cases: source 480×320, 320×480, 48×32 and 3200×4800; Contain, Fit width, Fit height and Original; manual 0/90/180/270. Every host size and transformed image bounds agree with the independently selected rotated axes, and ScrollViewer extent agrees with those bounds. Contain has no scrollable overflow; width/height modes keep the fitted axis non-scrollable.
- 16 Contain renders have opaque alpha at all four inset corners and center, 80 points total. This specifically checks native rendering with the negative child slot; it is not an assertion that normal app interaction has been tested.
- 16 matched R0 direct-Image baselines have identical actual image sizes. In particular the 48×32 Contain image is 750×500 in both controls for the 800×500 viewport. This preserves the observed enlargement, despite the older UpdateFit comment claiming shrink-only behavior. This follow-up does not redefine that policy.
- 10 additional samples replace an unsized Image source with 162×108 then 108×162 and repeatedly change 0/90/180/270/0 on the same host. Native dimensions and center stay correct without metadata. Initial null and both source clears produce zero DesiredSize, without stale extent.

The first probe run failed 44 extent assertions because its oracle incorrectly assumed `Extent = max(viewport, content)`. Native WinUI returned the smaller content extent, including the unchanged direct-Image baseline. The corrected oracle requires the actual rotated content bounds, while scrollability is checked separately. All other geometry, coverage and baseline checks had already passed. The product was not changed to satisfy the mistaken oracle. The initial result remains `probe/results-attempt1-oracle.json`.

The probe is an artifact-only entry point with separate intermediate/output/publish directories and an explicit command guard. It bypasses normal Program.Main and App.OnLaunched, does not resolve settings services, and closes its own window. It is not a production binary or normal application UI acceptance.

## Automated validation and API evidence

Focused image suite: 119/119, including all previous 111 EXIF/clipboard/PSD tests and eight new layout cases. The four unequal-axis corner tests independently rotate all four corners and verify that the host extent contains exactly those bounds. Three measurement-constraint tests cover both axis states and infinity; one checks empty measured extent. Existing 32×3 output representations, eight PSD orientation fixtures and clipboard ownership tests are unchanged.

Final whole-solution Release build: zero warnings, zero errors. Full tests: 772/772 across eight assemblies, zero failures/skips. Results are recorded in `artifacts/a398-fit/agent/full-build.log`, `full-tests.log` and `full-test-summary.json`. Static checks are in `static-checks.json`; architecture checks cover 20 projects and the release contract rejects all seven bypass regressions. Both isolated probe processes exited normally; final process/window inventory is `probe-cleanup.json`.

New Panel override APIs have no prior custom-panel implementation in this repository. Their installed WinUI 1.8.260803003 documentation is `artifacts/nuget/microsoft.windowsappsdk.winui/1.8.260803003/metadata/Microsoft.UI.Xaml.xml`: MeasureOverride at line5991, ArrangeOverride at5963, UIElement.Measure at5248, Arrange at5210 and DesiredSize at5435. Those signatures compiled with TreatWarningsAsErrors, and the isolated native probe verified their layout/render behavior. The public host compiled into product XAML and was instantiated successfully in the normal application UI below. No WPF-only syntax or shared Geometry is used.

## Normal application gate completed, release pending

After main diff review, the normal production publish and required package/VLC checks passed. Metadata confirms Program.Main, IsStandalone=false and no injected A398FitProbe/ProbeApplication or thumbnail harness. Existing PathProbe/TextPerformanceProbe are legitimate product types. KOTU.dll SHA256 is `BEE307F846C3BD1B7FD03B0EE8001F079F7C621BEF38DCA3F286471886CC18FF`; verified existing 7z.dll was reused without changing package gates.

Actual normal app checks used a fresh settings backup and documented computer-use input/observation. In the matched maximized 1280×752 window, TIFF1/8 in Contain at 0/90/180/270 and zoom100 show all six cells centered with no UIA scrollbar. TIFF8 R90 now starts near y22 rather than the prior y205, with ABC/DEF and no lower clipping beyond the existing control overlay. These are visual/UIA observations; exact transformed bounds and extent assertions belong to the isolated probe above.

Original at Windows150% preserved physical 1:1 sizing. Fit width and height exposed only the intended overflow axis; both pan directions worked. Wheel zoom100→110 preserved the sampled pointer anchor; repeated rotation at110, F reset, navigation, external replacement, maximize and fullscreen return behaved correctly. Small/large synthetic images, rotated GIF animation and PSD7 passed representative checks. Normal TIFF8 R90 Ctrl+C yielded exact 480×320 ABC/DEF in PNG, DIBV5 and DIB with zero channel tolerance. The same image appeared centered on portrait paper in Microsoft Print to PDF preview; the dialog was cancelled without submitting a job.

Explicit limits: Ctrl+wheel and Shift+wheel could not be injected by the documented UI tool API; unchanged handlers are static evidence only. The saved restored-window position was partly offscreen, so full restored-window/free resize inspection is incomplete. Mid-drag unload/capture-loss was not independently injected; replacement after completed drags worked. Actual thumbnail-provider image output, PNG eXIf support, other codecs and the unavailable reporter source remain outside this accepted fit gate.

At21:20:16, owned windows closed normally, KOTU process count and final computer-use KOTU/print window count were zero. The fresh settings backup was restored to SHA256 `F11284486319B6C38F988A3556CF6076B3CD2163F2156040E2109AE8242C73D9`; all26 synthetic fixtures were unchanged. No runtime code changed after the validated build/UI. Detailed observations and evidence: `artifacts/a398-fit/agent/native-ui-results.md`, `production-metadata.json`, `production-policy.json`, `native-clipboard-pixels.json` and `cleanup-ui.json`.
