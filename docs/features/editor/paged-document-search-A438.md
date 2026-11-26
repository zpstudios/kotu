# A438 — Find in PDF and HWP documents

This is the A438 search contract for v0.435.0. Current release and installation status are in [HANDOVER](../../HANDOVER.md); observed behavior and remaining conditions are in [device checks](../../DEVICE-CHECKS.md#a438).

## Behavior

Document and All Readable route Ctrl+F to a read-only Find field for PDF and office previews. HWP page view retains its original page rendering while finding text. Switching between HWP page and text views searches the newly displayed representation; it does not silently switch modes to perform a search. This extends the original [A396 search contract](find-replace-A396.md), whose PDF exclusion no longer applies.

The field selects its query on Ctrl+F. Enter/F3 and Shift+Enter/Shift+F3 move forward/backward and wrap. Enter navigates only from the Find input. Ctrl+H and the Find options button expose counts and navigation without replacement controls. Escape returns focus to the visible document. IME composition and held-key repeats do not trigger navigation. Unrelated popups, other text inputs and pass-through surfaces keep their existing key routing.

Find options always provides a Close button, including while the query is empty, pending or unsuccessful. Focus stays inside the popup when navigation becomes unavailable; Close and Escape return to the document. This avoids the observed empty-popup focus recovery that previously hid its notice immediately, without weakening the shell's focus guard.

The current match is highlighted and brought into view without changing the PDF or HWP zoom. The input is 32 DIP high, 120 DIP wide below a 700-DIP module bar and 180 DIP otherwise. Counts and navigation appear inline from 1100 DIP and remain in Find options at smaller widths. Existing page, fit and print availability are preserved.

| Representation | Search content and result position |
|---|---|
| PDF | The same input byte snapshot used by Windows.Data.Pdf rendering is indexed by PdfPig. Glyph coordinates map to the rotated, cropped visible page; original rendering and printing remain with Windows.Data.Pdf. |
| HWP pages | The bundled rhwp engine searches body and table-cell text. Its selection rectangle APIs identify the displayed page and match coordinates. The renderer's Unicode case folding is used. |
| Office text preview | Literal `OrdinalIgnoreCase` search uses the loaded preview, including matches crossing its virtualized text chunks. Truncated input retains the `Loaded preview only` notice. |

PDF results distinguish no matches from `No searchable text in this PDF.` and search failure. Scanned or graphical content is not OCR-processed. HWP page search failure preserves the readable page rather than treating the document as empty or switching to text view. The existing [office preview limits](office-text-preview-A45.md#input-bounds) remain in force.

## Bounds and lifetime

Queries are limited to 1024 UTF-16 code units. Search retains at most 10,000 matches and explicitly marks an exceeded match count with `+`. Only the current match is painted, with at most 256 rectangles.

PDF indexing accepts snapshots up to 256 MiB, at most 10,000 pages and 8,388,608 indexed characters. A partial index is identified in the search details. PDF parsing and search execute on the pane worker. Cancellation is checked between pages and glyphs; a synchronous PdfPig page parse cannot be interrupted mid-call.

HWP searching runs in the existing local renderer worker. The rhwp API returns all results synchronously, so the 16-MiB result-string check and retained-match cap apply after that call and cannot bound its internal allocation. A timeout reports search failure separately from rendering. Worker dispatch has one active operation and one latest mailbox each for page, query and selection; host messages carry bounded result state rather than the complete match list. After a timeout the current page remains visible, but further rendering must wait for the synchronous engine call to return.

Document/view lifetimes, query revisions and selection requests are checked before applying results. Query changes, composition, source replacement and unload invalidate earlier requests. Body pointer and wheel interaction cancel pending automatic selection, including handled PDF zoom-wheel events. Find/Find options entry also accepts the explicitly forwarded neutral All Readable host itself and the shell's exact Start button or focus-recovery anchor. Current focus identity and visual ownership are checked; this does not extend Enter/Escape ownership or capture other inputs. HWP browser shortcuts and Escape key release are bridged to the host, including when focus is inside the page.

## Implementation evidence

[PdfPig 0.1.16](https://github.com/UglyToad/PdfPig/releases/tag/v0.1.16) is a managed, read-only extraction dependency. Its [coordinate transformation](https://github.com/UglyToad/PdfPig/blob/v0.1.16/src/UglyToad.PdfPig/Graphics/OperationContextHelper.cs) already applies crop origin and page rotation. KOTU uses all four glyph rectangle corners before normalizing to top-left page coordinates. PdfPig license/NOTICE files accompany the implementation and are required by package validation; [Third-Party Notices](../../../THIRD-PARTY-NOTICES.md) identifies the dependency.

The existing [rhwp dependency](office-text-preview-A45.md) remains at 0.9.1. Runtime probes confirmed body/cell rectangle APIs on seven existing HWP fixtures totaling 20 pages and established Unicode-scalar offsets, including emoji and old Hangul. `getPageText` was rejected for match positioning because its page assignment and table coverage differed from rendered content. These Node probes do not establish installed-font or actual WebView highlight alignment.

The main reviewer reproduced the original Ctrl+F failure in PDF, HWP page view and HWP text view in the isolated v0.434.0 app. Local evidence is under `artifacts/a438-review/`; current implementation and verification status remain in [HANDOVER](../../HANDOVER.md). Outstanding device coverage is recorded in [DEVICE-CHECKS](../../DEVICE-CHECKS.md#editor), rather than being inferred from parser or source checks.
