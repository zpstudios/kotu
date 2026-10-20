# A411 — video fit synchronization

## Scope and baseline

On 2026-10-20 the user selected A411, followed by the A401 repeated-Enter performance work.
The main agent coordinates, reviews and validates; GPT-6 Astra/high implements one batch at a time.
Baseline: product v0.398.0, source `58e2e4e`. The report's original file and launch path are unknown.
Contain retains the existing shrink-only contract: small videos stay at their original pixel size.

## Reproduced before changes

The main agent used the existing v0.398.0 Standalone package and locally generated MJPEG AVI files
with colored outer borders. The installed KOTU window stayed open. These are synthetic
reproductions, not a claim that the reporter used the same input or sequence.

1. External cold start of a 1920×1080 video initially fits correctly. After selecting Original and
   resuming, EOF advances to a 1080×1920 video. The button returns to Contain, but the portrait
   video is cropped vertically: its displayed width is approximately 720 logical screenshot pixels,
   instead of approximately 285 at a window size of 948×539. Selecting Contain and resuming
   restores the full portrait border. A later transition to 320×180 instead enlarges the small
   video to approximately 900×506, violating shrink-only.
2. The Settings UI confirms Windows scaling is 150%. With the Standalone session's app scaling
   changed to 100%, external delivery opens a fresh 1920×1080 video window. It initially fits.
   Pause followed by Play crops the image to its original size while Contain remains selected.
   No manual zoom or Original selection is involved in this second reproduction.

Screenshots are observations of rendered boundaries. Native `Scale` readback would only report
the player setting, not prove the output was rendered with that scale. A paused one-frame-per-second
fixture does not necessarily repaint immediately after selecting another mode.

## Investigation and serial batches

The first batch prepares the scale before new-media playback and reapplies the current mode when
native video output becomes available. It preserves Manual/Original and checks player lifetime.
Its native race explanation remains a candidate until the same real-screen cases pass.

The second batch addresses a separate source-confirmed calculation error: `SurfacePixelSize`
multiplies layout dimensions by system DPI but omits the ancestor transform used for app scaling.
The installed LibVLCSharp 3.10.0 output uses SwapChainPanel dimensions and composition scale.
The app100/Windows150 reproduction supplies a direct validation case for correcting this mismatch.

Local investigation notes, installed-package IL and fixtures are under `artifacts/a411/`.
Neither a setter readback nor a model test is counted as actual video rendering verification.

## Validation status

Batch 1 passed a normal-entry-point Standalone publish and main diff review. On the actual
Windows150/system-default app session, 320×180 stays approximately 213×120 logical screenshot
pixels; switching to 1080×1920 shows the full portrait at approximately 285 pixels wide. Selecting
Original and switching to 1280×720 returns to Contain at approximately 853 pixels wide, matching
the expected original-size cap. No synthetic unit test was presented as native-output evidence.

Batch 2 and final local validation passed. Version: **0.399.0**. The transformed surface bounds
include the app UI transform and apply system rasterization once. Fit-following layout updates
are coalesced and recheck the current mode; Manual/Original and disposal guards are retained.

| Actual final-build case | Result |
| --- | --- |
| External cold start, Windows150/system default, 1920×1080 | Full outer border fits |
| External delivery to running session, Windows150/app100 | Full border fits before and after Pause/Play |
| Original selection, then next file | Original crops as requested; next file resets to Contain |
| Medium → portrait → small | Portrait fits; 320×180 stays approximately 213×120 logical pixels |
| Maximize/restore and full screen/Escape | Correct fit returns after each size change |
| Internal explorer double click, Windows150/app100 | Fresh video view fits correctly |

The external tests invoke the normal executable with a local file argument. Actual Windows
Explorer file-association registration was not changed or separately double-clicked. Physical
Ctrl+wheel, custom SAR/rotated/multi-track media, other monitors and first-presented-frame timing
were not exercised. Manual protection was reviewed in code. AVI/MJPEG was the rendered fixture;
other containers/codecs are not claimed as passed. Resume from Pause/Play was exercised, but a
saved playback-history cold resume was not.

Normal solution build: zero warnings/errors; focused Video **36/36**, full **687/687** across eight
assemblies; architecture20 and release gates7 passed. Normal production publish/package and
Standalone publish passed. Both KOTU.dll hashes equal
`C33F9203E71A49ADAA6774C955BEE484E3DD1032B15D2E01E8C0599ECFDB2D4B`, entry point is
`KOTU.App.Program.Main`, with no thumbnail harness seams. Normal Core uses `KOTU-Main` and
`IsStandalone=false`. User settings and all four fixture hashes match; installed window722786
remained. All test sessions were normally closed.

## Separate pre-existing live-scale issue — A412

While a video view is already playing, changing app100→200 from another Settings window leaves
the native output in the upper-left half-size region. Baseline v0.398.0 also reproduces a
system-default150→200 change with a roughly three-quarter-size upper-left output. Maximizing
and restoring the baseline window restores the full border. This is not a regression introduced
by the fit calculation change and is registered as A412 for a separate output-surface lifecycle
fix. The original reporter did not report this sequence.

Installed LibVLCSharp updates its swap-chain matrix on CompositionScaleChanged but does not
update the private size metadata there. This is a candidate explanation consistent with the
observations; the native event ordering was not traced. There is no public refresh method.
Private reflection, pointer manipulation and artificial resize workarounds were not introduced.
Static app100 startup and playback in A411 are independently verified above.

## Release verification

Local verification is complete; remote release verification is pending.
