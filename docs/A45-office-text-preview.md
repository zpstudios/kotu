# A45: offline office text preview

This product was developed by referring to Hancom's public HWP document file specification.

본 제품은 한글과컴퓨터의 한글 문서 파일(.hwp) 공개 문서를 참고하여 개발하였습니다.

## Delivered scope

The Document module and All Readable can open `.hwp`, `.hwpx`, `.odt`, `.ods` and
`.odp` as **read-only text previews**. The screen explicitly labels the result as
a preview and explains that original layout, images, charts and formulas are not
rendered. Copy text copies only the displayed preview. Text size controls and
virtualized, selectable text support reading long results. No save or print
contract is exposed for these binary/package formats.

This is partial delivery of A45, not a page-faithful office viewer. The original
page layout requirement remains open. Export to PDF in the authoring application
is the recommended way to see the original appearance in KOTU.

| Format | Readable content | Limits |
|---|---|---|
| HWP | HWP 5.x `BodyText/SectionN` paragraph text, compressed or uncompressed; table-cell paragraphs appear in storage order | HWP 3.x, encrypted, DRM and distribution-only documents rejected; shape/style records ignored; old Hangul private-use characters retained without conversion |
| HWPX | Sections in OPF spine order; paragraph/run text, including nested table paragraphs | Header/style/image objects ignored; table geometry and original reading order not reconstructed |
| ODT | Body headings, paragraphs, basic inline spaces/tabs/line breaks, links as plain text, table-cell text | No original styling or pagination; notes, annotations, tracked deletions and embedded graphical text in paragraph frames may be omitted |
| ODS | Sheet names, cell text or cached typed values, repeated populated rows/cells | No formula evaluation, cell geometry, charts or formatting; repeated entirely empty ranges collapsed |
| ODP | Slide names and text boxes | No slide layout, notes, images, animations or transitions |

Missing text and object-only documents show an explicit empty-preview message.
Truncation at 200,000 characters is visible in the notice and also applies to
Copy text. Text previews may omit content or change reading order and must not
be used to verify the visual fidelity or completeness of a document.

## Design and routing

- `KOTU.DocumentModel/OfficeTextReader.cs` owns ZIP/XML/HWP text parsing.
- `CompoundDocumentReader.cs` is a bounded, read-only MS-CFB reader supporting
  version 3/4 sector sizes, FAT/DIFAT and mini streams. Only requested streams
  are materialized. It is not a general purpose compound-file writer.
- `OfficeTextView` is a separate view chosen by `DocumentModule.CreateView`.
  Existing `DocumentView` (text editing, Markdown, HTML and PDF) is unchanged.
- Module extension registration is the source for the router, All Readable,
  file filters and optional Explorer associations. Standalone builds retain A43's
  registry-write prohibition.
- Quick info identifies these files as their format plus “Text preview”; it
  does not misclassify ZIP/binary bytes as text encoding or line counts.
- Explorer tiles and detail rows exclude office formats from raw plain-text
  preview and encoding detection, retaining their format tiles instead.
- The parser runs on a dedicated module worker. Unload cancels parsing and
  invalidates UI results. Text is split on the worker into chunks of at most
  2,048 UTF-16 code units for a virtualized ListView, avoiding a huge TextBox.
- No new packages, native libraries, external office applications, converters,
  browser engines, network calls or temporary files are used. The same code
  works in self-contained Windows x64 and the standalone distribution.

## Input bounds

- Source file: 64 MiB; ZIP entries: 4,096; total advertised ZIP expansion:
  256 MiB; each consumed package/HWP part: 8 MiB; consumed text parts: 64 MiB.
- XML: DTD prohibited, resolver disabled, 8 MiB character cap, depth 64,
  200,000 reader nodes. Every XML part is checked before tree construction.
- HWP/HWPX sections: at most 128. HWP section numbers must be contiguous.
- CFB: header, sector indices, chain lengths, cycles, duplicate directory paths,
  FAT counts and directory bounds are validated. Directory parsing is iterative.
- Package names/references reject traversal and absolute/URI paths. Entries are
  never extracted and external references are never followed.
- Scripts/macros are not executed; spreadsheet formulas are not evaluated.
- Loops check cancellation, including decompression, XML scanning, CFB traversal,
  HWP records and expansion of repeated table content. OS file reads and the
  bounded final XML tree construction are not interruptible mid-call.

## Research and decisions

The public specifications describe storage and semantic content, not a compact
C# engine that reproduces office layout. Building that layout engine is larger
than a parser: fonts, paragraph pagination, floating objects, drawing geometry,
tables and equations all contribute. A local text preview can be delivered
without claiming that fidelity.

Primary sources consulted:

1. [Hancom HWP/OWPML format disclosure](https://www.hancom.com/support/downloadCenter/hwpOwpml)
   and [HWP 5.0 revision 1.2](https://cdn.hancom.com/link/docs/한글문서파일형식_5.0_revision1.2.pdf):
   container, header flags, raw DEFLATE body records, UTF-16 controls and required attribution.
2. [Microsoft MS-CFB](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cfb/53989ce4-7b05-4f8d-829b-d08d6148375b):
   compound sector chains and directory format.
3. [OASIS OpenDocument 1.3](https://www.oasis-open.org/standard/open-document-format-for-office-applications-opendocument-version-1-3/)
   and [schema](https://docs.oasis-open.org/office/OpenDocument/v1.3/OpenDocument-v1.3-part3-schema.html):
   package MIME types, body elements, whitespace and repeated table ranges.
4. [Hancom OWPML model](https://github.com/hancom-io/hwpx-owpml-model)
   and [Hancom content extractor](https://github.com/hancom-io/hwpx-contents-extract):
   official projects are C++/Java and represent/extract content rather than supplying
   an embeddable .NET page renderer. No code from these projects is bundled.
5. [OpenMcdf](https://github.com/openmcdf/openmcdf) is an MPL-2.0 CFB library,
   not an HWP layout engine. A narrowly bounded reader avoids adding another
   dependency for the two required HWP stream families.
6. [pyhwp](https://github.com/mete0r/pyhwp) is a Python parser with experimental
   conversion and AGPL licensing. It is not shipped, executed or incorporated;
   only public sample documents were used as compatibility inputs.

## Verification performed

- New parser tests: 30 passed; DocumentModel suite: 77 passed. Full original ZIP/CFB fixtures are generated in
  memory from the public formats. Tests exercise Unicode, HWP mini/regular FAT
  streams, compressed/plain and extended-length HWP records, HWPX spine/header
  handling, ODT whitespace, ODS cached values and repeats, ODP slide text,
  unsupported/encrypted types, CFB cycles, XML DTD/depth limits, ZIP traversal,
  duplicates, oversized parts, truncation and cancellation.
- Full App and Document module Release/x64 builds: 0 warnings, 0 errors.
- Independent review fixed HWPX table-before-following-text order, raw binary
  Explorer previews, stale quick-info results after unloading, and worker-ordered
  cancellation-source disposal. Zoom buttons have descriptive accessibility
  names; HWP/HWPX displays the original Hancom notice with its English translation.
- All solution test suites: 498 passed in total. The sandbox denied native file
  operations in the FileOperations suite; those 36 tests passed when rerun with
  ordinary Windows access. The other 462 tests passed in the sandbox.
- Additional actual public files were downloaded to ignored `artifacts/a45-fixtures`
  and parsed locally. They are not redistributed or included in the application.

| Fixture and primary project URL | Result | SHA-256 |
|---|---|---|
| [pyhwp sample-5017.hwp](https://github.com/mete0r/pyhwp/blob/master/tests/hwp5_tests/fixtures/sample-5017.hwp) | 360 characters, not truncated; 32/35 nonempty reference XML text nodes match literally; three nodes use old Hangul PUA where pyhwp's reference output converts to Unicode jamo | `FF3FAAE938610FCFBEDC21C27C507CA0CC038B44872B65D10FCAD0B55AF67BA2` |
| [pyhwp table.hwp](https://github.com/mete0r/pyhwp/blob/master/tests/hwp5_tests/fixtures/table.hwp) | 0 characters; reference XML also has no text nodes (empty table) | `DF4746B82EEA954A2ABA36EB7DC51ACF8CD975DCED5E3331A1838932EAF123F9` |
| [python-hwpx FormattingShowcase.hwpx](https://github.com/airmang/python-hwpx/blob/main/examples/FormattingShowcase.hwpx) | 133 characters, not truncated; actual spine contains header before section | `AA6D85118875BB07B47F2F8D766DCD049A22820636384ED12B1E0FDD208FC6FB` |
| [OASIS introduction ODT](https://github.com/oasis-tcs/odf-tc/blob/master/docs/odf1.3/cs02/part1-introduction/OpenDocument-v1.3-cs02-part1-introduction.odt) | 15,301 characters, not truncated, expected OpenDocument text present | `AB6001637EB64218DA8DF7CCBC4B389BB8EA02FA89FC425494ED074609E0F3E1` |

Remaining real-device checks: visual selection/copy/zoom and long-preview
scrolling in the new native view; rapid All Readable switches during loads;
Explorer association UI for the new extensions; representative user documents
and original-authoring-app comparisons. No claim of full visual compatibility
or universal HWP/ODF support is made.
