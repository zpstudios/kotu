# A398 EXIF orientation — PNG/TIFF/APNG follow-ups

Current candidate: v0.426.0, extending the released v0.404.0 orientation/layout work with static PNG eXIf, local PNG/TIFF thumbnails and APNG. The user authorized commit, push and release on 2026-11-14. The sections below retain earlier checkpoints; the newest follow-up takes precedence. A398 remains partial for untested source/codec boundaries and the unavailable original reporter file.

## Local thumbnails and APNG follow-up (2026-11-14)

Local PNG/APNG/TIFF previews are decoded on the existing workers and delivered through the existing BGRA/WriteableBitmap path. Shell-produced pixels are never blindly rotated again. The original is opened only for an ordinary local-content request, with a second cloud-attribute check immediately before opening. Cached-only automatic retries and cloud placeholders retain the shell's cached-only route. This does not repair an already cached cloud thumbnail or force a download. Attribute/open checks retain the existing filesystem race boundary. Source reads are limited to64 MiB with cooperative cancellation and a five-second token; decoded source dimensions are limited to100 million pixels and thumbnail edges to768 pixels. Unsupported/invalid/oversized inputs retain the fallback tile behavior.

APNG uses a bounded parser and a worker-owned compositor because this PC's native WIC decoder exposed only one frame for the two-frame fixtures. Both `.png` and `.apng` are routed to the image module. Compressed frame data is retained rather than caching decoded animations. The parser checks CRCs, sequence, counts, rectangles and operations; limits are128 MiB source,16,777,216 canvas pixels,4,096 frames and65,536 chunks. SOURCE/OVER blend and NONE/BACKGROUND/PREVIOUS disposal operate on the unrotated premultiplied canvas; EXIF1–8 is applied once to the composed result. Each loop begins with a transparent canvas. Frame delays, denominator0=100, a10ms minimum and finite/infinite play counts are honored. Decode/presentation time is included in each frame interval.

Only pixel publication enters the UI dispatcher, with at most one outstanding frame. Navigation/unload cancels playback and queued results recheck sequence/path. Manual rotation remains the existing display transform. Thumbnails, clipboard and print use the first animation frame, excluding a separate default poster; they do not promise the currently visible animated frame. First-frame PNGs retain16-bit samples, DPI and color metadata. Animated display is8-bit premultiplied sRGB, matching the WinUI bitmap surface.

The new ICC check exposed a weakness in the earlier static test: Magick could recover a profile from a copied text chunk even when WinRT had replaced the real iCCP chunk with sRGB. Color-description chunks are now restored explicitly after unmanaged transcoding, and the first-frame preview is compared against the color-managed animated frame. This corrects the product and strengthens the old preservation claim.

Automated verification: image319/319 (89 static PNG +18 thumbnail +93 APNG additions over119 prior tests); full1,406/1,406 across8 assemblies, no failures/skips, normal build0 warnings/errors. Tests cover both TIFF byte orders,8 orientations ×4 manual turns in all clipboard formats, every animated frame, separate posters, partial rectangles, all disposal/blend operations, alpha, loop reset, timing/cancellation, malformed input,16-bit/DPI/ICC and no-open cloud/cache gates. Final normal run: `artifacts/local-build/runs/20261114-011532-All/`; native evidence is recorded separately under `artifacts/a398-png/`.

Native R1 confirmed all8 PNG and TIFF thumbnail orientations, APNG first-frame thumbnails, and orientation6 animated playback/manual rotation. R1 also exposed oversized/clipped Contain layout with WriteableBitmap. The corrected path specifies both dimensions from oriented pixels and the selected fit mode; final screen verification is recorded below. Earlier R1 success does not count as a fit-layout pass.

Native R2 passed: orientation6 in Contain shows all six cells at240×360 and manual90° at360×240, without enlargement/clipping. Navigation to orientation7 resets manual rotation and repeatedly shows only its two expected frames. A two-play `.png` animation remains on its final AD/BE/CF frame after completion; its actual print preview shows the first FC/EB/DA animation frame. Printing was cancelled. Closing during an infinite animation completed normally, with no KOTU process remaining. All26 new review fixture hashes were unchanged. Evidence: `native-426-observations.txt`, `native-426-cleanup.json`, `review/wic-apng.json`, `review/original-hashes.json`. Exact frame intervals/play counts are automated assertions, not measurements of screen captures. APNG Fit width/height, other themes/DPI and a native clipboard destination were not repeated; the existing static PNG destination check and the exact APNG clipboard-format tests remain separate evidence.

Primary specifications: [PNG animation control and frame composition](https://www.w3.org/TR/png-3/#11acTL), [WinRT transcoding](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.bitmapencoder.createfortranscodingasync).

## Static PNG eXIf follow-up — earlier local checkpoint (2026-11-14)

The user selected the photo-orientation follow-up. This working-tree change extends display, copy and print to static PNG eXIf Orientation1–8. It does not change the published v0.425.0 release. The original user-reported file remains unavailable; the evidence uses synthetic files.

`PngExifOrientation.NormalizeAsync` recognizes standard PNG chunks and the IFD0 SHORT orientation in either TIFF byte order. Chunk bounds, the eXIf CRC, TIFF offsets/counts and duplicate metadata are checked. Missing/identity/invalid/ambiguous orientation and APNG return the original byte array. It does not follow nested IFD pointers. For Orientation2–8, a memory copy excludes the eXIf chunk before WIC transcoding, preventing a future native decoder from applying the same orientation twice. This display-only representation is not an edited original file; the whole eXIf chunk is absent from the normalized copy. Original disk metadata remains available to the existing information view.

The viewer normalizes on its existing image worker before decoding oriented dimensions; preload, display, copy and print consume the same representation. The original file size is recorded before normalization. Clipboard also handles a raw tagged PNG directly, and normalizing an already normalized PNG is a no-op. Existing stale-result, navigation, manual-rotation and print-state capture guards are unchanged. JPEG/TIFF retain native orientation, PSD retains Magick AutoOrient, and GIF/APNG are not flattened by this helper. No file writes or cloud-placeholder reads were added.

An additional WIC defect was reproduced with a three-pixel-wide indexed PNG: direct transcoding omitted horizontal reflection for orientations2/3. Palette and sub-8-bit images are therefore expanded to straight BGRA8 without color management before transformation. Other images use transcoding without a Q8 conversion. Pixel storage/color type may change (including 16-bit grayscale to RGBA); exact sample precision, alpha, ICC profile bytes and resolution are the preservation contract.

### Verification

- Before implementation, 56/64 genuine PNG orientation/manual-rotation cases failed. All64 now pass: Orientation1–8 × manual0/90/180/270 × little/big-endian metadata, independently decoding PNG/DIBV5/DIB against literal six-color expectations.
- Image suite **208/208** (119 existing +89 added), no failures/skips. Added cases cover idempotence, unchanged input, partial alpha, AdobeRGB ICC bytes, non-8-bit-aligned 16-bit RGBA/grayscale samples and144 DPI, malformed metadata and APNG bypass. Existing JPEG/TIFF/PSD/GIF tests pass.
- Full normal Release/x64 build **0 warnings,0 errors**; **1,295/1,295** tests across8 assemblies, no failures/skips; architecture, release-note/workflow, editor protocol/visual, recording-engine contract and Explorer COM smoke checks pass. Local run: `artifacts/local-build/runs/20261114-003426-All/`.
- A separate self-contained `KotuStandalone=true` publish passed. Its uninstrumented Image module was used in the actual desktop UI checks. This verifies the shared image implementation, not installation or update behavior.
- Actual KOTU screens: PNG1–8 matched ABC/DEF-derived literal directions and360×240 or240×360 oriented dimensions. PNG5+R90 displayed CBA/FED; PNG7+R90 displayed DEF/ABC. PNG8 Original and successive R90/R180 stayed centered with the expected composition; switching to Contain retained direction. Fit width/height, arbitrary resize and other DPI/theme combinations were not rechecked.
- Actual KOTU Ctrl+C on PNG8+R180, pasted through Paint's menu, produced DA/EB/FC with a240×360 selected image. This is a real destination check; it does not identify which offered clipboard format Paint selected or replace the exact per-format automated checks.
- Actual PNG7+R90 print preview showed centered DEF/ABC on portrait and landscape Microsoft Print to PDF paper. The dialog was cancelled; no print job was submitted.
- All24 original fixture hashes and the8 PNG copies actually opened in the UI were unchanged; test KOTU, Paint and print windows were closed, KOTU processes0. The first plain-path launch unexpectedly opened the installed KOTU; it was closed without file operations, and `process:` plus the exact review path launched the intended Standalone. No before/after installation-settings snapshot was taken, so this is not a settings-preservation claim.

Focused evidence: `artifacts/a398-png/preservation-pass.log`, `preservation-pass-results/`, `native-ui-results.json`, `native-cleanup.json`, `publish.log`. Failed intermediate runs remain separate, including the initial UTF-8 PNG-signature mistake and unsupported explicit Gray16 test-fixture API; they are not counted as passes.

### Actual thumbnail provider boundary

On this PC, all24 local PNG/JPEG/TIFF fixtures returned `ThumbnailType.Image` from `GetThumbnailAsync(SingleItem,256)`; the old Icon-only observation was from a different environment. An independent six-cell pixel comparison passed **all8 JPEG orientations**, maximum channel error1. PNG and the3×2 TIFF fixtures remained raw ABC/DEF for every tag: identity passes, orientations2–8 fail. This confirms the provider limitation rather than image-viewer failure. The app's thumbnail routes are unchanged; no original-file hydration workaround was added. Evidence: `fixtures/thumbnail-direction.json` and the local probe source.

A398 remains partial: PNG/TIFF provider thumbnails, APNG orientation and other source/codec combinations are not solved by this batch. The initial v0.404 notes below are historical and do not supersede this follow-up.

Primary references: [PNG eXIf chunk](https://www.w3.org/TR/png-3/#eXIf), [WIC transcoding contract](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.bitmapencoder.createfortranscodingasync), [native transform order](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.bitmaptransform).

## Problem and contract

The v0.403.0 viewer passes original JPEG/TIFF bytes to BitmapImage, which already applies EXIF orientation, then applies a second EXIF-derived rotation. Synthetic native screen checks showed JPEG/TIFF6 rotated180 degrees and JPEG5/7 reduced to horizontal reflection. The reporter's original file was unavailable; these are independent baseline defects, not a claimed reproduction of that file.

The new contract is **EXIF-normalized image, followed by manual clockwise rotation**. Original WIC-encoded bytes stay in memory unchanged, including animated GIFs. WIC oriented dimensions are the baseline for layout, tray, clipboard and print. BitmapImage supplies display/print EXIF correction. Clipboard requests RespectExifOrientation and applies only manual rotation. PSD pixels are normalized once by Magick before PNG encoding.

## Changes

- Removed the EXIF angle field/reader/preload tuple value; TotalRotation now means manual rotation only. The XAML transform is still the existing RotateTransform.
- ReadImageFile uses OrientedPixelWidth/OrientedPixelHeight. No speculative BitmapImage dimension fallback was added; existing metadata-failure behavior remains.
- Display source, dimensions and metadata now publish together after SetSourceAsync and the existing path/load-sequence check. Print captures bytes, dimensions and manual rotation before its asynchronous decode and computes its axis swap from that captured rotation.
- Clipboard PNG/DIBV5/DIB still derive from one full-resolution BGRA first frame, now decoded with RespectExifOrientation and dimensions composed from oriented size plus manual axis swap. Ownership, clipboard sequence checks, stale-source gates and publication routing are unchanged.
- Small worker-only MagickImageDecoder.Read helper retains the existing first-composite read and applies AutoOrient before size/PNG extraction. No profile stripping or color conversion was added.
- QuickInfo changes are comments only. Its existing raw metadata summary policy is not expanded. Thumbnail/cloud/provider, wallpaper, navigation and GIF behavior are unchanged.

## Automated verification

- Focused image tests **111/111** passed; previous suite61 plus50 new cases.
- Whole solution Release/x64 build: **0 warnings, 0 errors**.
- Whole solution: **764/764**, eight test assemblies,0 failures/skips.
- Architecture20 projects, release-workflow bypass regression7, diff whitespace check passed. No orphan EXIF-angle symbols, escaped Hangul comments or XML-doc double hyphens in changed code.
- Evidence: `artifacts/a398/agent/focused-tests.log`, `focused-test-results/`, `full-build.log`, `full-tests.log`, `full-test-results/`, `full-test-summary.json`, `static-checks.json`.

New coverage uses a non-square independent ABC/DEF pixel fixture and32 literal EXIF1–8×manual0/90/180/270 expectations. Every case checks exact pixels and exact width/height through independently decoded PNG, DIBV5 and DIB, signed header dimensions, re-decoding PNG with EXIF enabled, and unchanged source bytes. Three360×240 JPEG5/6/7 cases verify real metadata presence and region colors with a three-level channel tolerance.

Eight PSDs contain an independently inserted standard Photoshop1058 EXIF resource. They pass through the actual product loader and verify orientation, exact dimensions/pixels/partial alpha, AdobeRGB ICC bytes, Ignore/Respect PNG agreement and unchanged source-file bytes. Separate tests cover first composite vs white layer and untagged PSD alpha/order. Existing GIF-first-frame, clipboard ownership/stale result, thumbnail raster and navigation tests remain included.

## Initial failures and resolution

These are recorded rather than counted as successful runs:

1. The test fixture initially used obsolete ColorProfile.SRGB; TreatWarningsAsErrors caught it. The installed API's ColorProfiles replacement was used, with no warning suppression.
2. Eight PSD assertions initially found no ICC object in emitted PNG. Exact oriented pixels/alpha had already passed. ImageMagick can represent standard sRGB as a PNG sRGB chunk instead of carrying the redundant ICC profile. A distinctive AdobeRGB fixture now proves actual ICC byte preservation through the unchanged production encoder; no product strip/convert workaround was added.
3. xUnit could not bind boxed Int32 InlineData to nullable UInt16. The test parameter is now nullable Int32, explicitly checked/cast when encoding its SHORT tag.
4. A TIFF tag with count0 but inline SHORT6 is malformed yet WIC recovers orientation6. The initial identity assumption was wrong. The regression now checks that the decodable image keeps that native90-degree result, correct dimensions and pixels; the product delegates consistently to the same native orientation contract rather than inventing a different malformed-tag policy.
5. One20-pixel JPEG cell center differed by12 channel levels because of compression near small color regions. The fixture was enlarged to120-pixel cells; the original three-level tolerance stayed unchanged. No orientation expectation or product code was weakened.
6. First full build warned MSB3277 because old Hardware test assets selected System.Management10.0.12 while the Hardware product selected10.0.13. A whole-solution restore aligned generated assets; no project dependency or warning policy changed. Final build is0/0.

Failed logs and TRX remain in the artifact directory with attempt labels or their original timestamp; the final logs and full-test-summary are authoritative for the completed automated pass.

## Lifecycle and original-data review

| Contract | Review/result |
|---|---|
|Original WIC bytes|Read path still uses shared-read and returns the unchanged byte array; clipboard32 cases assert input equality.|
|PSD disk bytes|Actual loader is read-only;8 tagged and1 untagged file tests assert byte equality.|
|GIF|BitmapImage and original GIF source preserved; existing first-frame copy test passes. Actual animation is a later native gate.|
|Preload|Maximum2 neighbors, PSD exclusion, cloud-placeholder exclusion, worker and pruning paths unchanged; tuple carries oriented dimensions.|
|Display races|Both current-path/load-sequence checks retained; dimensions publish only with the winning decoded source.|
|Print state|bytes/width/height/manual rotation captured before await; layout axis swap uses the captured rotation. Existing imageable-area centering remains.|
|Copy lifetime|Gate invalidation on rotation/navigation/unload, byte identity, load sequence, owner/clipboard sequence and handle transfer unchanged; original writer tests pass.|
|Cloud thumbnails|No original file opens or EXIF reads added; existing provider/no-hydration policy unchanged.|

## Native evidence and outstanding gates

Before implementation, the baseline JPEG1/5/6/7 and TIFF6 screen was inspected using the computer-use skill. Both owned windows were closed normally; processes/windows0, settings hash restored and8 synthetic file hashes unchanged (`cleanup-check.json`). Detailed investigation and native32-composition/PSD results are in `artifacts/a398/agent/investigation.md` and `probe-results.json`.

A separate genuine PNG eXIf probe preserved tags5/6/7, confirmed both by chunk inspection and Magick readback. This Windows WIC decoder returned no System.Photo.Orientation, kept360×240 oriented dimensions and identical Ignore/Respect pixels. This is a supported-codec boundary, **not successful PNG EXIF correction**. Artifacts `metadata-orientation-{5,6,7}.png` and `png-metadata-results.json` support the follow-up BitmapImage baseline/new comparison. No PNG support expansion is included in this batch.

The native shell thumbnail provider returned Icon for all8 local JPEG/TIFF fixtures; icon colors are not orientation evidence. A successful image-thumbnail provider comparison remains unverified. No hydration workaround is permitted.

At this report's initial checkpoint, the corrected binary has not yet undergone UI validation or deployment. Parent subsequently authorized normal publish/package and native UI checks; their results will be appended below. Pending gates include all8 screen states,5/7 manual90, fit modes, representative PSD, GIF animation and print preview portrait/landscape layout. Do not present the automated pass as complete A398 deployment.

Primary references: [Microsoft BitmapTransform](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.bitmaptransform) for native operation order; [Adobe PSD specification](https://www.adobe.com/devnet-apps/photoshop/fileformatashtml/) for resource1058. Current BitmapImage behavior is established by the native baseline observations, not inferred from a general documentation promise.

## Native validation completed, release pending (2026-10-23)

The Astra/high implementation agent performed the actual UI validation; main independently reviewed source/diff, TRX, probes and reports. Normal self-contained publish and required package/VLC checks passed. The local package reused the verified v403 7z.dll because the runner path was absent; no CI file changed. Production metadata confirmed Program.Main, IsStandalone=false and no thumbnail harness. The tested KOTU.dll SHA256 was `7E0F1F2515E52F127338E047E47EDE0DC48C06C2167A90A5F733B8E28237FA05`.

- Actual JPEG/TIFF Orientation1–8 screens matched the independent six-cell direction matrix and oriented dimensions in Original mode; PSD5/6/7 matched. JPEG5+manual90 produced CBA/FED and JPEG7+manual90 DEF/ABC. Original, Contain, Fit width and Fit height preserved observed direction/axes on TIFF8. These fit observations are not acceptance of all zoom/cropping policies.
- Normal Ctrl+C on JPEG7+manual90 published PNG, DIBV5 and DIB plus Windows-synthesized CF_BITMAP. All three persisted representations independently decoded to480×320 with DEF/ABC six-cell colors (JPEG channel tolerance3). A first artifact-only probe used255 intensity where the UI fixture uses180/220; correcting that fixture definition preserved tolerance3 and passed. No product adjustment was involved.
- TIFF8 print preview retained CF/BE/AD on both portrait and landscape paper and centered the image. TIFF8+manual90 displayed ABC/DEF landscape centered vertically on portrait paper. Microsoft Print to PDF initially stayed connecting/loading, then completed; this is initial delay, not a proven environment/product failure. All dialogs were cancelled without submitting a print job.
- GIF visibly alternated both synthetic color frames. Original file bytes remained unchanged. Real user source files were unavailable; actual Windows WhatsApp remains unverified because it is not installed.
- Genuine PNG eXIf5/6/7 screens stayed raw ABC/DEF360×240 in v404. A matched v403 PNG6 screen was also raw at the same dimensions. This agrees with native decoder observations and remains an explicit A398 codec boundary, not successful PNG orientation support. Provider returned Icon, so actual image-thumbnail orientation remains unverified. No cloud hydration policy changed.
- Small-image Contain enlargement was observed; the earlier baseline showed similar enlargement, but no matched same-fixture/mode/window comparison proves attribution to A398. A manual rotation in Contain also showed a shifted/cropped viewport while direction was correct. Original manual rotation and print output were centered. This is a separate fit/layout observation, not silently fixed or declared regression-free.
- Both owned test processes/windows closed normally. At20:39:42 host KOTU processes0, final sky KOTU/print windows0; fresh backup restored to SHA256 `F11284486319B6C38F988A3556CF6076B3CD2163F2156040E2109AE8242C73D9`. All21 UI fixture hashes and3 PNG hashes were unchanged. No original user window existed before testing, so no user app relaunch was needed.

Evidence: `artifacts/a398/agent/native-ui-results.md`, `native-clipboard-pixels.json`, `native-exif7-r90.json`, `cleanup-v404-ui.json`, `production-metadata.json`, `production-policy.json`, `publish.log`, `package-check.log`.

This JPEG/TIFF native-path and PSD batch is locally implemented/validated at v0.404.0, with full automated764/764 and focused111/111. It is not committed/pushed/released yet, and A398 as a whole remains partial. Next serial investigation is A412 after main's batch review/deployment. A post-UI XML comment correction only distinguishes QuickInfo raw metadata resolution from oriented display resolution; no runtime behavior changed and the tested binary hash remains the actual publish hash.


## Historical fit gate follow-up checkpoint (20:48, unresolved at that time)

Main required a matched baseline comparison before accepting the fit observation. Same TIFF1/8, Contain, zoom100 and1280×752 window: TIFF1+R90 is identical/centered in v403/v404. TIFF8 is shifted/clipped in v403 R0 (double EXIF rotation) and v404 R90 (correct manual composition), with the same visual geometry. In v404, vertical scrollbar value0 was confirmed before pan; dragging up183 screen units exposed the image and produced scrollbar183.3333 while zoom stayed100. This supports an old pre-rotation layout extent problem, not just stale positive offsets. A reset-to0 is insufficient.

The native display gate remains open despite correct pixels, copy and print. No behavioral fix was applied. Main must review the minimal single-image layout-host proposal in `artifacts/a398/agent/fit-layout-followup.md`; a fresh Astra/high batch can implement it while preserving source/GIF, fit sizing, ScrollViewer/presenter wheel/pan hooks and zero-metadata fallback behavior. This is not a general transform framework or permission to change small-image zoom policy.

Additional paired-test cleanup at20:48:11: processes0, final sky windows0, fresh `settings-before-fitcompare.json` restored to F11284486319B6C38F988A3556CF6076B3CD2163F2156040E2109AE8242C73D9,21 fixtures unchanged (`cleanup-fitcompare.json`). Earlier21+PNG3 unchanged check remains valid. Current implementation is uncommitted, full764/764 remains the tested baseline, and deployment must wait for this native gate.

## Final rotated-layout follow-up: fit gate passed, release pending

A single-image local WinUI Panel now swaps native Image.Measure constraints/DesiredSize axes and centers the unrotated child slot. The existing RotateTransform remains the only renderer; source, fit sizing, DPI, wheel/pan, GIF, clipboard and print policies are unchanged. The native direct-Image baseline confirms the existing small-image Contain enlargement is preserved. Design, installed API evidence and failure history are in `A398-ROTATED-LAYOUT.md`.

Final focused119/119 and full772/772 pass with zero build warnings/errors, architecture20 and release gates7. The isolated control probe passes90 recorded samples:64 geometry conditions,16 direct-Image R0 baselines and10 source-replacement/rotation samples, plus80 alpha checks across16 renders. Its initial44 failures were an incorrect extent oracle; correcting the oracle to the measured native/baseline contract required no product change or test weakening.

Normal publish/package passed with Program.Main, IsStandalone=false and no injected probe/harness. The final tested KOTU.dll SHA256 is `BEE307F846C3BD1B7FD03B0EE8001F079F7C621BEF38DCA3F286471886CC18FF`. Actual TIFF1/8 Contain at all four manual angles in1280×752 is centered with no false scrollbar; TIFF8 R90's prior y205 clipping is gone and ABC/DEF starts near y22. Original DPI, fitted axes, both pan axes, wheel anchor,110% repeated rotation/reset, navigation/external open, maximize/fullscreen, GIF animation after rotation and PSD7 passed representative checks. TIFF8 R90 copy has exact480×320 ABC/DEF in all three formats; print preview centers the same composition on portrait paper and was cancelled without a job.

Ctrl/Shift+wheel cannot be injected by the documented UI API and remains static review only. Full restored-window/free resize inspection is limited by the saved offscreen position; mid-drag unload is unverified. These explicit limits do not change the accepted fit gate or the existing PNG/provider/codec residues. At21:20:16, normal close left processes/windows0, restored the fresh settings backup to F11284486319B6C38F988A3556CF6076B3CD2163F2156040E2109AE8242C73D9 and preserved all26 fixture hashes. Evidence is `artifacts/a398-fit/agent/native-ui-results.md`, `native-clipboard-pixels.json`, `cleanup-ui.json` and the automated results there. No runtime code changed after validation.

## Release verified

Product and tag `4c65d212755753bba62946d4c04703af921541cf` match. [Build39953510825](https://github.com/zpstudios/kotu/actions/runs/39953510825) and [release39953510868](https://github.com/zpstudios/kotu/actions/runs/39953510868), attempt1, both passed772/772 across eight assemblies with zero warnings/errors. Release startup/integrity, installer, Standalone and Explorer COM checks passed. [v0.404.0](https://github.com/zpstudios/kotu/releases/tag/v0.404.0) was published at **2026-10-23 21:36:15 KST**, Latest, non-draft/non-prerelease, with eight uploaded nonempty assets. Evidence and full remote job logs are under `artifacts/a398/main/`. Final documentation is retained locally without an additional push; A412 investigation is next, and A398 residuals above remain open.
