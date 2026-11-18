# A400 / A410 image clipboard interoperability

## Scope and serial batches

On 2026-10-19 the user selected A400 and A410 for development. The main agent coordinates,
reviews and validates; GPT-6 Astra/high implements serially. A400 fixes image copy data and
invocation routing. A410 adds Copy image to the existing surface context menu only after A400
passes review and local validation. A398 full EXIF orientation redesign remains separate.

The user clarified that the reported `image you tride adding has no content` error occurs in
the **Windows WhatsApp application**. Browser testing is not equivalent to that application's
acceptance check.

## Baseline evidence

- Baseline source: product `cd0ed8b`, v0.396.0; starting local HEAD `a17ab08`.
- Main launched the existing isolated Standalone build with a synthetic 160×100 RGBA PNG.
  The installed KOTU window was left running. No personal image or chat content was used as a fixture.
- Actual Ctrl+C with the image surface focused advertised CF_BITMAP, CF_DIB and CF_DIBV5.
  Native GetClipboardData returned **null for all three**. CF_DIB and CF_DIBV5 had **zero bytes**;
  GetObject could not read any bitmap dimensions. Two readbacks agreed.
- A blank Word document did not display an image after pasting that clipboard item.
- Evidence: `artifacts/a400/baseline-image.json` and `baseline-image-readback.json`.
- This demonstrates an actual empty native image payload. It does not establish the internal
  cause of the WinRT conversion or reproduce the user's original WhatsApp session.
- Installed-app inventory did not expose a Windows WhatsApp or Paint installation in this
  execution environment. An existing WhatsApp Web tab is a distinct product and was not used
  to claim Windows-app validation. No message was sent.

## Code findings

The old path publishes an encoded PNG through DataPackage.SetBitmap and Clipboard.Flush.
Its fresh MemoryStream starts at zero; an EOF-stream explanation is unsupported. There is no
explicit early stream disposal. Unit tests of the PNG encoder do not exercise native clipboard
conversion, publication or consumer lifetime.

Ctrl+C was attached only to ImageViewerView.KeyDown. Its toolbar is detached by TakeBottomBar,
and AllReadable can receive focus on its outer control; neither route reached the image handler.
After accepting Ctrl+C, the old path also discarded results on ordinary focus loss or foreground
change. Encoding shared a FIFO worker with image preloading. These routing/cancellation gaps
are independently established from code, not assumed to be the user's original trigger.

The existing image context menu contained Set as desktop background. A410 extends that menu
and invokes the same image-copy operation. Explorer file copy and text selection remain intact.

## A400 implementation and first verification gate

One worker operation decodes the oriented first frame and produces registered PNG, 32-bit
straight-alpha CF_DIBV5 and an opaque 24-bit white-composited CF_DIB fallback. All global-memory
allocations are prepared before opening the clipboard. Publication uses the actual window owner,
checks source and clipboard sequence again under the lock, and transfers only successful handles.
Unpublished handles are freed once. A busy clipboard has six attempts with at most five asynchronous
40 ms delays; partial publication is not cleared for retry.

Invocation admission excludes text, explorer and unrelated popups, but permits the image view,
its detached toolbar and exact active AllReadable host focus. Ordinary focus loss after an accepted
copy is allowed. Source replacement, rotation, unload and newer clipboard contents invalidate it.

The main reviewed all source and new files, then ran **61/61 image tests**. The initial restricted
test runner could not connect to its testhost and aborted; the same Windows-local run passed in
368 ms. This was an execution-environment failure, not a failed product assertion. Standalone
compilation/publish passed; metadata confirmed the normal Program.Main and no test-harness seam.

Actual native readback after Ctrl+C returned PNG **404 bytes**, DIBV5 **64,124 bytes** and DIB
**48,040 bytes**, with a valid 160×100 CF_BITMAP. Word displayed the red/green/blue image and its
transparent corner. Manual rotation followed by Ctrl+C produced a 100×160 image with the expected
pixel arrangement. After normal process exit, PNG bytes remained identical, native image formats
were readable, and Word successfully pasted the rotated image. Independent PNG decoding confirmed
blue/red top corners, transparent bottom-left and green bottom-right.

Evidence: `artifacts/a400/main/focused-tests.log`, `a400-image.json`, `a400-rotated.json`,
`a400-after-exit.json`, and `main/a400-native-pixels.json`. A410 started only after this gate passed.

New APIs were checked against Microsoft's [clipboard ownership contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setclipboarddata)
and [BITMAPV5HEADER format](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-bitmapv5header).

## A410 implementation and combined UI verification

Copy image is the first item in the existing image context menu, with the Copy glyph and
`Copy image (Ctrl+C)` tooltip. The menu is attached to the focusable viewer root for keyboard
access. Its enabled state follows image readiness and the shared pending-copy gate. The explicit
menu action allows only its own popup and calls the same operation as Ctrl+C.

The main verified the v0.397.0 Standalone build in the actual Windows UI:

| Check | Observed result |
|---|---|
| Right-click → Copy image | PNG bytes match Ctrl+C exactly; valid PNG/DIBV5/DIB and CF_BITMAP |
| Shift+F10 → Enter | Menu opens with Copy image focused and copies valid image data |
| Menu → Esc | Menu closes, image stays open, clipboard sequence stays 97 |
| Next separate Esc | Existing content-close behavior works |
| No image | Copy image is disabled |
| Explorer selection → Ctrl+C | File copy retains CF_HDROP; no PNG image publication |
| Open selected image → Ctrl+C | Valid image formats replace the file clipboard item |
| Detached toolbar / AllReadable | Ctrl+C reaches the current image copy operation |
| Image navigation / app switch | 4096×3072 image retains full dimensions and valid payload |
| AllReadable text selection → Ctrl+C | CF_UNICODETEXT remains available; no PNG publication |

The large-image payload contained PNG 56,826 bytes, DIBV5 50,331,772 bytes and DIB 37,748,776
bytes. App-switch success is an observed end result, not an instrumented timing proof of switching
during encoding. Clipboard busy, stale-source/sequence and ownership failures are covered by
automated deterministic tests rather than claimed as actual UI race reproductions.

Evidence is under `artifacts/a400/`: `a410-menu.json`, `main/menu-parity.json`,
`a410-keyboard-menu.json`, `a410-cancel.json`, `a410-explorer-file-copy.json`,
`a410-open-from-explorer.json`, `a410-toolbar.json`, `a410-allreadable.json`,
`a410-large-switch.json`, and `a410-text-copy.json`. The final Standalone KOTU.dll SHA-256 is
`D55FD9812E63E0E14E81017CF19911913DDC903167279620BEB61DE715EC4DFC`.
Metadata confirms normal Program.Main and no thumbnail test harness. Both test application
windows were closed normally; the synthetic unsaved Word document was discarded. The installed
KOTU window was untouched. Settings and all three fixture hashes match the baseline
(`main/cleanup-check.json`).

## Final local build and package gate

The normal Release/x64 full solution build passed with **zero warnings and zero errors**.
All **687/687 tests in eight assemblies** passed with no failures or skips, including 61 image
tests. Architecture checks passed for 20 projects and all seven release-workflow bypass
regressions were rejected. Normal self-contained publish and package preparation passed.
Local package preparation used the existing repository 7-Zip DLL through a temporary script;
the tracked packaging scripts were unchanged.

Production metadata confirms `KOTU.App.Program.Main`, no thumbnail harness types/methods/fields,
and normal Core policy (`IsStandalone=false`, `InstanceKey=KOTU-Main`). KOTU.dll has the same
SHA-256 as the UI-tested Standalone build; distribution policy resides in KOTU.Core.dll.
Changed/new C# content passed the Korean-escape scan, Directory.Build.props XML parses, and
`git diff --check` passes. Evidence: `artifacts/a400/main/{build,tests,architecture,release-gates,
publish,package}.log`, `test-summary.json` and `production-metadata.json`.

## Remote release verification

Product commit `b231ddddc6d049922a28478efc63db2fec38bf81` was pushed to master once.
[Build 39212654342](https://github.com/zpstudios/kotu/actions/runs/39212654342) and
[release 39212654335](https://github.com/zpstudios/kotu/actions/runs/39212654335) both completed
successfully. Each independently passed **687/687 tests**, a build with zero warnings/errors,
architecture/release gates and the actual Explorer COM selection check for all three verbs.
Release executable startup, installer integrity and Standalone regressions/startup also passed.

[v0.397.0](https://github.com/zpstudios/kotu/releases/tag/v0.397.0) was published on
**2026-10-19 at 16:02:06 KST**. It is non-draft, non-prerelease and Latest. All eight assets
(including delta, Setup, Portable and Standalone) are uploaded and nonempty; the tag resolves
to the product commit above. Local evidence: `artifacts/a400/main/release-verification.json`.
Final verification documentation is retained in a local-only commit without another push.

## Validation limits

Windows WhatsApp
and Paint remain unverified because neither is installed in this execution environment. Word
and native readback success do not substitute for the user's Windows WhatsApp acceptance check.
Original failing input/application version are unknown. Full EXIF orientation 1–8 support remains
A398. The earlier asynchronous
explorer-file-copy overwrite candidate was identified from code but was not reproduced or changed;
this fix does not redesign explorer copy ordering.
