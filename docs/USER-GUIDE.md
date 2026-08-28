# KOTU — User Guide

> **As of v0.257.0.** KOTU is under active development. This guide describes the behaviour of the
> version above; later releases may add or change things without this page being reissued.

KOTU opens photos, videos, music, documents, archives and hardware information in a single window.
Double-click a file and the matching module takes over the centre of the window and the bottom bar.
Everything below is written as "what you can do and how", not "why it works that way".

---

## Contents

1. [The window](#1-the-window)
2. [Modules](#2-modules)
3. [Keyboard shortcuts](#3-keyboard-shortcuts)
4. [Side panels](#4-side-panels)
5. [The built-in file browser](#5-the-built-in-file-browser)
6. [File associations and the Start menu](#6-file-associations-and-the-start-menu)
7. [Several windows at once](#7-several-windows-at-once)
8. [Settings](#8-settings)
9. [The tray icon](#9-the-tray-icon)

---

## 1. The window

Every KOTU window has the same three parts.

- **Centre** — the module view: the photo, the video, the PDF, the archive listing.
- **Bottom bar** — one row, 44 px tall. On the left the **menu** button and the **Open file** button;
  at the right end the **Full screen** button; the rest belongs to whichever module is open.
- **Two side panels** — a **left** panel (folder tree and file list) and a **right** panel
  (information about the current file). Both are hidden until you call them.

The side panels are opaque **sidebars**: an open panel stands beside the content and makes the
centre narrower. Either panel is 25% of the window width.

**Full screen.** `Enter` and `Alt`+`Enter` both toggle full screen: the window fills the screen,
taskbar included, the bottom bar goes away, and both side panels close so the content stands
alone — press `F11` or `F12` if you want a panel back while you are there. Leaving full screen —
with `Enter`, `Alt`+`Enter` or `Esc` — brings back the window, the bottom bar and the panel
arrangement you had when you went in. The **Full screen** button at the right end of the bottom
bar does the same as the keys.

While a video is playing the bottom bar hides itself after a few seconds and the full-screen rule
above bends a little — see [2.2](#22-video).

Opening a file, or switching module, always leaves full screen.

The title bar shows `1-KOTU`, or `1-KOTU - filename` once a file is open. The number in front is the
window number: the first window is `1-KOTU`, the second `2-KOTU - holiday.jpg`, and so on. A leading
`●` means the document in that window has unsaved changes. The module is not named in the title —
the window icon carries a coloured ring instead, one colour per module, and at the larger size
Windows uses for `Alt`+`Tab` it also carries the module's three letters.

**Opening a file.** Double-click it in Windows Explorer (if you registered the file type), drop it
onto the KOTU window, or use the **Open file** button in the bottom bar. Dropping a file whose type
does not belong to the module you are looking at opens it in a new window.

---

## 2. Modules

Seven modules. Switch between them from the menu at the bottom left.

| # | Module | Opens |
|---|---|---|
| 1 | All Readable | anything the other file modules can open |
| 2 | Image | `.jpg .jpeg .png .gif .bmp .webp .tif .tiff .ico .psd` |
| 3 | Video | `.mp4 .mkv .avi .webm .mov .wmv .m4v .mpg .mpeg .ts .m2ts .flv .3gp .ogv` |
| 4 | Audio | `.mp3 .flac .wav .ogg .opus .m4a .aac .wma` |
| 5 | Document | `.txt .md .markdown .log .ini .html .htm .pdf` |
| 6 | Archive | `.zip .7z .rar .tar .gz .tgz .bz2 .xz` |
| 7 | H/W Info | no files — live hardware information |

### 2.1 Image

- Move through the folder with `←` and `→`. Files are sorted naturally, so `img2` comes before `img10`.
- **Zoom** with the mouse wheel, from 10% to 800%. The point under the pointer stays put.
  Touch pinch-zoom works too. Scroll bars appear when the picture is bigger than the view.
- **Push the picture around** by dragging it with the left mouse button. It starts only when the
  picture is bigger than the view and only once the pointer has really moved, so double-clicking for
  full screen still works. Mouse only — touch and pen pan the way they always did.
- **Rotate** 90° clockwise with the rotate button or `R`. Rotation is not saved to the file and
  resets when you move to the next picture. Orientation stored in EXIF is applied automatically.
- **Fit** — see [2.9](#29-the-fit-button). The Image module keeps your choice while you browse the folder.
- **Delete** removes the current file to the Recycle Bin and shows the next picture. There is no
  confirmation dialog and no toolbar button — the `Delete` key is the only way in.
- **Print** the picture with `Ctrl+P` or the print button in the bottom bar. Windows brings up its
  own print dialog, with its own preview, where the printer, the paper and the number of copies are
  chosen. The picture goes on one page, scaled as large as the printable area of that page allows
  without cropping it, and the rotation you are looking at — from EXIF or from `R` — is printed the
  same way. Zoom and Fit are screen settings and change nothing on paper. The button is greyed out
  while no picture is open.
- Animated GIFs play.
- The bottom bar carries one run of text on the left: the file name, then `width×height`, the kind
  (`JPG 24-bit`), the size, `position/total`, and an EXIF summary (date taken, camera, exposure,
  aperture, ISO, focal length) where the format has one. Everything after the file name is repeated
  in a tooltip, since a narrow window trims it.
- To the right of that run sits the current zoom level — `100%`. The Fit options resize the picture
  without touching that figure, so it stays at 100% until you zoom with the wheel or a pinch.
- A double-click on the picture goes full screen, and `Enter` or `Alt`+`Enter` does the same from the keyboard; `Esc` leaves it.

### 2.2 Video

- Playback uses the bundled libvlc engine. No codec pack is needed.
- **Play/pause** with `Space`, with the ▶ button, or by clicking the picture.
- **Seek** ±5 seconds with `←` / `→`, or drag the seek slider. Seeking happens once, when you let go.
- **Volume** ±5 with `↑` / `↓` or the mouse wheel. `M` mutes. The volume is remembered.
- **Speed**: 0.5× · 0.75× · 1× · 1.25× · 1.5× · 2×.
- **A narrow window drops what it can spare.** Below about 640 px of bottom bar the volume slider
  and the two time labels give up their room so the seek slider keeps its own. Volume is still on
  `↑` / `↓`, the wheel and `M`, and the position of the seek thumb still says where you are.
- **Subtitles** are picked up automatically from the video's own folder — `.srt .smi .ass .ssa .sub .vtt`.
  A file with exactly the video's name wins over a suffixed one (`movie.srt` before `movie.ko.srt`).
  Subtitles that are not UTF-8 are converted from CP949 before playback, so Korean `.srt` and `.smi`
  files display correctly. Press `C` or the subtitle button to switch track or choose **No subtitles**.
- **Resume**: if you watched more than 30 seconds and stopped before the last 3%, the position is
  remembered and playback continues there next time.
- **The whole folder plays as a list.** When a video ends, the next file in the same folder starts
  automatically (name order, hidden files skipped), and playback stops after the last one by
  default. Press `L` or the loop button — the first button in the bottom bar, immediately left
  of ▶ — to cycle through the three loop modes: off (dimmed icon), **Loop list** (after the last
  file the list starts over), and **Repeat this file** (the same file restarts). Right-click the
  button to choose how often either mode repeats — once more, three more times, or forever;
  cycling with the button always enters a mode at forever. Repeating never replays from a resume
  point; every pass starts at 0:00.
  To stop after every file instead, turn **Auto-play next file** off in
  [Settings → Playback](#playback). It only applies while no loop mode is set — a loop mode plays
  on regardless.
- **Zoom** with `Ctrl`+wheel, from 10% to 800%.
- **Fit** — see [2.9](#29-the-fit-button). Each new file starts at Contain again.
- `Enter` or `Alt`+`Enter` toggles full screen, and `Esc` leaves it.
  In full screen, play, pause, seek, volume, mute and zoom each flash a short caption in the middle
  of the picture.
- **The bottom bar keeps out of the way while you watch.** Once a video has been playing for
  3 seconds without any mouse or touch input, the whole bar — seek slider included — slides out of
  sight. Move the pointer, tap the screen, or bring the pointer near the bottom edge and it comes
  back; pause or let the video end and it stays visible. The same rule applies in full screen,
  which also means full screen is no longer bar-less for video: move the mouse and the controls
  appear, hold still and they hide again. The mouse pointer itself is never hidden.
- Press ▶ with nothing open to play a built-in 32-second display-and-speaker test clip.
- When playback actually stops at the end — looping is off and the list is done — resizing the
  window clears the picture to black instead of rescaling the last frame. Press play to start it
  again and the picture returns at the new size.

### 2.3 Audio

- Same transport as Video: `Space`, `←` / `→` (±5 s), `↑` / `↓` and the wheel (volume ±5), `M` to
  mute, `S` for the same six speeds. Clicking the surface also toggles play/pause.
- A live waveform fills the window while a track plays, with the track name above it.
- **The controls stand where Video puts them.** Loop, ▶, the position, the seek slider, the length,
  mute, the volume slider and the speed box occupy the same places in both modules, so nothing
  shifts under the pointer when you go from a video to a track. Audio's own equalizer and
  audio-device buttons take the right-hand end, where Video keeps subtitles and Fit. The
  narrow-window rule is the same too: below about 640 px of bar the volume slider and the two time
  labels are hidden.
- **Resume** works exactly as in Video (over 30 seconds played, stopped before the last 3%).
- **The whole folder plays as a list.** When a track ends, the next file in the same folder starts
  automatically (name order, hidden files skipped), and playback stops after the last one by
  default. Press `L` or the loop button — first in the bottom bar, immediately left of ▶, as in
  Video — to cycle through the three loop modes: off (dimmed icon), **Loop list** (after the last
  track the list starts over), and **Repeat this file** (the same track restarts). Right-click the
  button to choose how often either mode repeats — once more, three more times, or forever;
  cycling with the button always enters a mode at forever. Repeating never replays from a resume
  point; every pass starts at 0:00.
  **Auto-play next file** in [Settings → Playback](#playback) governs this here too — it is one
  setting shared by Video and Audio.
- `Enter` or `Alt`+`Enter` toggles full screen, and `Esc` leaves it. The bottom bar stays put —
  the auto-hide above is a Video-module behaviour.
- Press ▶ with nothing open to play a built-in 18-second sample tune.

### 2.4 Document — plain text

- `.txt .md .markdown .log .ini .html .htm` open in an editable text box.
- **The module brings four buttons of its own to the bottom bar**, just right of the window's menu
  and **Open file** buttons, in this order: **New text file**, **Save**, **Print** and the **view**
  toggle. Opening a document is the window's own **Open file** button — a document with unsaved
  changes still asks before it gives way. *New text file* is always available and starts an empty
  `Untitled` document in place. If the current document has unsaved changes it asks first —
  **Save and new file**, **Discard and new file**, **Open in new instance** (keeps your edits and
  opens the new document in a new window) or **Cancel**.
- Further right, past the file name, the bar shows the unsaved mark, the zoom percentage and the
  PDF page count, then the two display toggles (**Line guides** and **Paragraph marks**, editor
  only) and the **Fit** control.
- **Every text document has an edit mode and a view mode.** The view button in the bottom bar
  switches between them. In view mode the editor is locked so nothing changes while you read —
  you can still move the caret, select and copy, and save any edits you made earlier. For
  Markdown and HTML the view mode is a rendered view, described below. The button is greyed out
  only when nothing is open, for PDFs (always view-only) and for files opened read-only (over 4 MB).
- **Markdown is rendered.** A `.md` or `.markdown` file opens as a formatted preview — headings,
  bold and italic, inline code and code blocks, lists, quotes, horizontal rules and links — and the
  view button switches between that preview and the editor. The preview is built
  from what is in the editor at the moment you switch, so unsaved edits show up in it; anything the
  renderer does not know stays as it was written. Markdown files over about 1 MB of text open
  straight in the editor, and at that size the view button locks the editor instead of rendering
  the preview.
- **HTML is rendered too.** A `.html` or `.htm` file still opens in the editor as source, and the
  view button shows it as a real web page instead of the locked source view. The page is rendered
  from the saved file on disk — relative images and stylesheets next to it work, and unsaved edits
  stay in the editor until you save and switch again. It is a viewer, not a browser: scripts never
  run, links that lead outside your files do nothing, and nothing opens a new window. The page
  follows the shared document zoom, and printing stays the source text, exactly as in edit mode.
  Rendering uses the WebView2 runtime that ships with Windows 11 (on older Windows 10 it may not
  be installed); without it the view button falls back to the locked source view and the bottom
  bar says so after the file name.
- The encoding is detected (UTF-8 with or without BOM, UTF-16, otherwise CP949) and kept on save,
  as is the line ending style (CRLF or LF).
- `Ctrl+S` saves. The save button stays disabled until there is something to save, and
  **● Unsaved** appears in the bottom bar while there is.
- Closing or switching away with unsaved changes asks first: **Save** / **Don't save** / **Cancel**.
- Files larger than 4 MB open read-only, showing the first 4 MB.
- `Tab` inserts a tab character in the editor.
- The text fills the full width of the window. To make it larger or smaller, zoom with
  `Ctrl`+wheel — 20% to 500% of the normal size, 10% per notch — or with `Ctrl`+`+` / `Ctrl`+`-`
  on the same scale (the numpad keys work too, holding a key repeats, and the keys work even while
  the cursor is in the editor); `Ctrl`+numpad `*` puts the zoom back to 100%. The current level
  shows in the bottom bar, and one level applies everywhere: it is remembered across files, windows
  and restarts. Zooming never touches the file itself. **The rendered Markdown view zooms with the
  same keys and the same `Ctrl`+wheel on the same scale**, so switching between the preview and the
  editor keeps the size you set; the text still wraps to the width of the view at every level, so
  nothing scrolls sideways.
- Very faint guide lines mark the top and bottom of each line of text, `¶` marks a line break and
  `·EOF` marks the end of the file. They are drawn over the text and never become part of it.
  **Two toggle buttons at the right of the bottom bar, just before Fit, turn them off and on** — *Line guides* for
  the lines, *Paragraph marks* for `¶` and `·EOF`. Either one takes effect as you press it and is
  remembered for next time. The pair is there whenever the editor is on screen, view mode included,
  and steps aside for the rendered Markdown and HTML views and for PDFs.
- **Fit** — see [2.9](#29-the-fit-button). Text and Markdown documents offer three of the four
  options.
- **Print** with `Ctrl+P` or the print button in the bottom bar — the same Windows print dialog as
  for pictures and PDFs, with its own preview and the same page-range option. The text prints in
  the editor's fixed-width font at its normal size — the on-screen zoom never changes the
  printout — with no headers, footers or page numbers, filling the printable area of the chosen
  paper. Long lines wrap as in the editor, tabs print as spaces, and a long file simply continues
  onto the next page. What prints is the text as it stands in the editor, saved or not: a new
  unsaved document prints too, under the name `Untitled`. Very large files (over about 1 MB of
  text) print a single notice page instead of the document.
- **Markdown prints the way you are looking at it.** From the preview it prints rendered — heading
  sizes, code block backgrounds, list indents and quote bars are kept, and links print as ordinary
  black text since there is nothing to click on paper. Each block stays whole on its page, and a
  code block taller than one page carries on to the next. From the editor it prints as the plain
  text described above. Whichever view you started the print from is what the whole job uses, even
  if you switch views while the print dialog is open. When the preview is not available or cannot
  be built — a very long file, for instance — the plain text rules above apply instead.

**Before it writes.** When there is anything to be careful about, saving stops and asks first:

- the file changed on disk after you opened it — *File changed on disk*, with **Overwrite** or
  **Cancel** (Cancel is the default);
- saving cannot reproduce the original bytes exactly, because the file mixes CRLF and LF line
  endings or contained bytes that could not be decoded — *Saving will normalize this file*, with
  **Save anyway** or **Cancel**;
- your text no longer fits the file's original CP949 encoding — *Encoding change required*, with
  **Save as UTF-8** or **Cancel**.

After writing, KOTU reads the file back and compares it. If the copy on disk does not match,
*Save verification failed* offers **Retry**, **Save as...** or **Cancel** — your text stays in the
editor either way.

### 2.5 Document — PDF

- Pages scroll continuously in one column. The bottom bar shows `current / total` as you scroll.
  There are no page-forward or page-back buttons.
- The wheel scrolls; `Ctrl`+wheel zooms around the pointer, about 10% per notch. Touch pinch-zoom
  works. `Ctrl`+`+` / `Ctrl`+`-` zoom in the same steps around the centre of the view (the numpad
  keys work too; hold to repeat), and `Ctrl`+numpad `*` goes back to the actual size — the same as
  picking **Original** from the Fit button.
- **Push the pages around** by dragging with the left mouse button, whenever there is anything to
  scroll to. Mouse only — touch and pen pan the way they always did.
- The keyboard scrolls too: `↑` `↓` move about an eighth of the view, `Page Up` `Page Down` move
  about a full screen, and `Home` `End` jump to the start and the end of the document. Holding a key
  keeps scrolling. The zoom level never changes, and `←` `→` are not used.
- **Fit** — see [2.9](#29-the-fit-button). Zooming by hand releases the fit.
- **Print** with `Ctrl+P` or the print button in the bottom bar — the same Windows print dialog as
  for pictures, with its own preview. Every page prints by default; a **page range** typed in the
  dialog (for example `2-5, 8`) prints just those pages, while the preview keeps showing the whole
  document. Each PDF page goes on its own sheet, scaled to fit the printable area of the chosen
  paper, and is rendered for paper — up to 300 DPI — no matter the zoom or Fit on screen. Pages are
  prepared one at a time, so a document hundreds of pages long prints without loading them all at
  once, and a password-protected PDF prints without asking for the password again while it is open.
  The button is greyed out while there is nothing to print.

### 2.6 Archive

- **Browse without extracting**: open an archive and its contents are listed with **Name**, **Size**
  and **Modified**. Double-click a folder to go in, `U` or the up button to come back out.
- Double-click a file inside the archive to open it with your default Windows app. Only that one
  file is extracted, to a temporary folder.
- **Extract here** (`E`) unpacks the whole archive next to itself, into a folder named after it.
  If the archive already has a single top-level folder, no extra wrapper folder is made. If the
  target name is taken, ` (2)`, ` (3)` … is appended.
- **Extract...** (`T`) asks for a destination folder and unpacks only the selected rows — or
  everything, if nothing is selected.
- **New archive** (`C`) creates a **ZIP** or a **7z**. A password is optional; 7z also encrypts the
  file names inside, ZIP does not.
- Drop files or folders onto the window to compress them — the same New archive dialog appears.
- Archives that need a password ask for one when you list or extract them.
- ZIP files with broken Korean file names are re-read as CP949 automatically.
- Long operations show a progress bar and a **Cancel** button. When it finishes, the result is
  revealed in Windows Explorer.
- All eight listed formats can be extracted. Only ZIP and 7z can be created.

### 2.7 H/W Info

The centre of the window is the sensor grid; the two side panels carry the rest.

- **Centre** — a tile per sensor, ten of them, each covering the last 30 seconds:
  CPU Temp, CPU Power, CPU Load, CPU Clock, GPU Temp, GPU Power, GPU Load, RAM, Fan, SSD Temp.
  **Click** a tile to select it — up to two at a time; picking a third releases the oldest.
  **Drag** a tile to reorder the grid. The tiles are square, and the grid is 4 columns wide with
  both sidebars docked, 6 with one and 8 with none.
- **Left panel** — up to two enlarged graphs, one per selected sensor, covering the last 10 seconds.
- **Right panel** — the specification list, scrollable: CPU, GPU, RAM, Motherboard, Storage,
  Network, System.
- **Bottom bar** — two long graphs of the same selected sensors, covering the last 5 minutes at
  every refresh setting. The graphs stretch to share the full width of the bar, and each writes
  the length of that window in its bottom-right corner.

The two panels are the ordinary side panels: `F11`, `F12` and the edge buttons drive them exactly
as in the file modules, and the module opens with both docked. Close them and the sensor
grid takes the whole window.

Every graph writes its channel name and its current value along the top. Where there is room for
them, the tiles and the enlarged graphs also label the top of the scale, prefixed so it cannot be
misread as the current value — `max 100%`, `max 100°C`, `max 5000MHz` — and the length of the time
window in the bottom right corner. The two graphs in the bottom bar are too shallow for the scale
label, but they do carry the window length in the same bottom-right corner.

Controls:

| Control | Key | What it does |
|---|---|---|
| Copy all | `C` | puts the whole specification list plus every current sensor value on the clipboard as text |
| Sensor refresh interval | `I` | 50 · 200 · 500 · 1000 · 2000 · 5000 ms. Default 500 ms. 50 ms costs noticeably more CPU |
| Graph size | `Ctrl` + `+` / `-` | one step bigger / smaller text and lines on every graph, Small ↔ Medium ↔ Large (numpad `+` / `-` work too; `Ctrl`+wheel over the graphs does the same, wheel up = bigger) |
| Graph size reset | `Ctrl` + numpad `*` | back to Medium |
| Always on top | `P` | pins the window over other windows and shrinks it once to the smallest window size — grow it back freely; unpinning leaves the window as it is |
| Full screen | `Enter` / `Alt`+`Enter` | full-screen dashboard, like every other module; `Esc` leaves it |

The graph size steps affect the text and line thickness of every graph — the two long graphs in the
bottom bar, the ten centre tiles and the enlarged graphs in the left panel. The graphs themselves
keep their sizes: tiles stay square at the width the grid gives them.

The selected sensors are also what this window's tray icon displays. Selection, tile order and graph
size belong to each window separately; the most recent change is what a newly opened window starts from.

CPU temperature, CPU power, fan speed and drive temperature need administrator rights — and the CPU
and fan readings additionally need **PawnIO**, a separately installed signed kernel driver that
LibreHardwareMonitor uses instead of bundling one of its own (KOTU does not install it for you).
When these sensors cannot be read, a line saying why appears just above the bottom bar: without
administrator rights it offers **Restart as admin**, and when the program is already elevated but
PawnIO is missing it offers a **Get PawnIO** download link instead. CPU clock is the one exception —
when it cannot be read directly, KOTU falls back to an approximate reading based on Windows
performance counters, which needs neither administrator rights nor PawnIO.
Restarting is a whole-program affair — every KOTU window closes and the program starts again with
administrator rights — so KOTU notes which windows were open and brings the same set back: same
modules, same files, same positions and sizes. Cancelling the Windows prompt changes nothing.

### 2.8 All Readable

One module that opens every file type the other modules handle. When you open a file, the centre
view and the bottom bar become that module's own; the window, the side panels and the tray icon stay
with All Readable. Ctrl+S in a text file, `E` in an archive and so on all keep working.

All Readable deliberately does not register file associations — double-clicking a file in Windows
Explorer still opens its dedicated module.

### 2.9 The Fit button

Image, Video, PDF and text documents share one Fit control with four options:

| Option | Result |
|---|---|
| **Original** | the original size, one image pixel per screen pixel. The button then shows a small `1:1` box |
| **Contain** | the whole thing fits in the view. Never enlarged — smaller files stay at their own size |
| **Fit width** | fills the width; the other axis scrolls |
| **Fit height** | fills the height |

Clicking the body of the button re-applies the option you last chose (`F`). Clicking the arrow opens
the list. `A` jumps straight to **Original**. A new picture, video or PDF starts at Contain.

**In a text or Markdown document** — while you edit, while you read the locked view, and in the
rendered Markdown view alike — the first three options are live and **Fit height** is greyed out,
since a document has no fixed height to fit. The text already wraps to the width of the view, so
all three come to the same thing: the document goes back to 100%. Which one you picked is what the
button then shows, and a document opens showing **Original**.

While nothing is open — the window is showing the file browser — the Fit control stays on the bar
but is greyed out; it comes alive when a file opens.

---

## 3. Keyboard shortcuts

### 3.1 Global

These work in every module.

| Key | Action |
|---|---|
| ``Alt+` `` | open the menu (bottom left) |
| `F11` | left panel open / closed |
| `F12` | right panel open / closed |
| `Enter` | full screen and back again |
| `Alt`+`Enter` | full screen and back again — the same toggle, typing-proof (see below) |
| `Esc` | one step back: out of full screen, out of the Open file browser, or close the open file |
| `Shift+N` | new window of the module you are looking at |
| `Ctrl+S` | save (text document) |
| `Ctrl+P` | print what you are looking at (picture, PDF or text document) |
| `Browser Back` | go back one step — see [3.6](#36-going-back) |

``Alt+` ``, `Alt`+`Enter`, `F11` / `F12` and `Ctrl+P` work even while you are typing in a text box.
These keys produce no character of their own, so nothing is taken away from the text — pressing
`Ctrl+P` mid-edit prints the document just as it stands. Plain `Enter` does
give way to typing — in a text document it is a line break — and `Shift+N` gives way as well.

`Esc` undoes one layer per press: full screen first, then the Open file browser, then the file
itself. Closing the file this way lands on the module's own browser with **both sidebars open** —
the same screen as starting the module from the menu — so a file opened straight from Windows
Explorer is one `Esc` away from the standard screen. A document with unsaved changes asks before
closing. Where `Esc` already means something it keeps that meaning first: it cancels a rename, it
clears the cut-mark in the file browser, and an open dialog takes it for itself.

There is no keyboard shortcut for switching module or for opening Settings — both live in the menu
at the bottom left. Switching module from the menu always arrives with both sidebars open. Choosing
the module you are already in changes nothing.

### 3.2 Panels: how `F11` and `F12` behave

`F11` drives the left panel, `F12` the right one. One press opens the panel as a sidebar; the next
press closes it. That is the whole story — there is no hold, double-press or translucent variant.
Press both keys and both sides toggle, in any order.

When a panel opens, a short hint appears next to it on a small dark plate, so it stays readable
over whatever is behind it — *Sidebar - press F11 or the pin button to close* — and fades after a
couple of seconds.

The panels answer these keys in every view mode, full screen included — and on almost every screen:
the unsupported-file notice and a new unsaved document included. With no file open the left panel
shows the folder you last browsed — on the notice it lists files of every type — and the right
panel says *No file open*.

**Settings is the one exception.** It has no side panels at all: `F11`, `F12` and the edge buttons
do nothing there, and any panel that was open slides away when you enter Settings. Leave Settings
and your previous panel layout comes back.

Inside the Open file browser, `F11` and `F12` do nothing; `Esc` gets you out.

### 3.3 Letter keys, per module

Only one module is on screen at a time, so the same letter can mean different things in different
modules. Letter keys give way to typing: they do nothing while the text cursor is in an editable
box or while you are typing to jump through the file list.

| Module | Key | Action |
|---|---|---|
| Image | `R` | rotate 90° clockwise |
| Image | `A` | Original |
| Image | `F` | re-apply the last Fit option |
| Image | `←` `→` | previous / next file in the folder |
| Image | `Delete` | move to the Recycle Bin |
| Image | `Ctrl+P` | print the picture |
| Video | `Space` | play / pause |
| Video | `←` `→` | back / forward 5 seconds |
| Video | `↑` `↓` | volume |
| Video | `M` | mute |
| Video | `S` | playback speed |
| Video | `C` | subtitles |
| Video | `L` | cycle the loop mode (right-click the button for repeat counts) |
| Video | `A` | Original |
| Video | `F` | re-apply the last Fit option |
| Audio | `Space` | play / pause |
| Audio | `←` `→` | back / forward 5 seconds |
| Audio | `↑` `↓` | volume |
| Audio | `M` | mute |
| Audio | `S` | playback speed |
| Audio | `L` | cycle the loop mode (right-click the button for repeat counts) |
| Document | `Ctrl+S` | save |
| Document | `Ctrl+P` | print the PDF or the text |
| Document | `Ctrl` + `+` / `-` | zoom one step in / out (numpad `+` / `-` work too; hold to repeat) |
| Document | `Ctrl` + numpad `*` | back to 100% |
| Document | `A` | Original |
| Document | `F` | re-apply the last Fit option |
| Document | `↑` `↓` | scroll the PDF, about an eighth of the view |
| Document | `Page Up` `Page Down` | scroll the PDF, about a full screen |
| Document | `Home` `End` | start / end of the PDF |
| Archive | `E` | Extract here |
| Archive | `T` | Extract... (choose a folder) |
| Archive | `C` | New archive |
| Archive | `U` | up one level inside the archive |
| H/W Info | `C` | Copy all |
| H/W Info | `I` | sensor refresh interval |
| H/W Info | `Ctrl` + `+` / `-` | graph size one step up / down (numpad `+` / `-` work too) |
| H/W Info | `Ctrl` + numpad `*` | graph size back to Medium |
| H/W Info | `P` | always on top |

`Enter`, `Alt`+`Enter` and `Esc` behave the same in all seven modules — they belong to the window,
not to the module.

`Space` gives way in the same manner as the letter keys. While the file list, the folder tree or the
centre thumbnails have the focus it no longer starts or stops playback in Video and Audio; in the
file list and in the thumbnails it selects the focused item instead.

### 3.4 In the file browser

| Key | Action |
|---|---|
| `Enter` | open the selected item — with several files selected, all of them |
| `Space` | select or deselect the item that has the focus |
| `F2` | rename the selected item (nothing selected: nothing happens) |
| `Delete` | move to the Recycle Bin |
| `Shift+Delete` | delete for good, after a confirmation |
| `Ctrl+C` / `Ctrl+X` / `Ctrl+V` | copy / cut / paste |
| `Ctrl+A` | select all |
| `Ctrl+Shift+N` | new folder |
| `Esc` | clear the dimming left by `Ctrl+X` (the clipboard keeps its contents); with nothing to clear, the window rule applies — see [3.1](#31-global) |

### 3.5 Mouse and wheel

| Where | Input | Action |
|---|---|---|
| Image | wheel | zoom, 10%–800% |
| Image | left-button drag | push an enlarged picture around |
| Image | double-click | full screen |
| Video | wheel | volume |
| Video | `Ctrl`+wheel | zoom, 10%–800% |
| Video | click | play / pause |
| Audio | wheel | volume |
| Audio | click | play / pause |
| PDF | wheel | scroll |
| PDF | `Ctrl`+wheel | zoom |
| PDF | left-button drag | push the pages around |
| Text document | wheel | scroll |
| Text document | `Ctrl`+wheel | zoom the text, 20%–500% — editor and Markdown view alike |
| H/W Info graphs | `Ctrl`+wheel | graph size, Small ↔ Medium ↔ Large (wheel up = bigger) |
| File browser, side panels | wheel | scroll |
| File browser | double-click | open (folders: go in) |
| File browser | `Shift`+double-click | open in a new window |
| File browser | right-click a file | Open in new instance · Cut · Copy · Rename · Delete |
| File browser | right-click a folder | Cut · Copy · Paste · Rename · Delete |
| File browser | right-click empty space | New folder · Paste · Refresh |
| File browser | drag | move within a drive, copy across drives. `Ctrl` forces copy, `Shift` forces move |
| Anywhere | back button (the thumb button) | go back one step — see [3.6](#36-going-back) |
| Near the bottom of the left or right window edge | move the pointer | a narrow edge button appears — see [4](#4-side-panels) |

The wheel never zooms thumbnails, and it never zooms while the pointer is over a side panel — it
scrolls there.

### 3.6 Going back

The mouse back button, and the `Browser Back` key on keyboards that have one, undo one step at a
time, in this order:

1. step back out of full screen;
2. leave the Open file browser, returning to the arrangement you had;
3. close the file and go back to the module's own browser, keeping the side panels as they were.

With nothing left to undo they do nothing. The mouse button works everywhere; the key gives way to
typing, like the letter keys.

Step 3 is the one place going back and `Esc` differ: going back keeps the panels exactly as they
were, while closing with `Esc` arrives with both sidebars open — the standard module screen.

---

## 4. Side panels

**The left panel** holds, from top to bottom:

- the path bar, with the **Up** button, the current folder, a **Filter** list and a **Sort** list;
- a tree of drives and folders, opened at the folder of the file you have open;
- the file list for that folder.

**The right panel** describes the file you have open — whatever the module knows about it, or
otherwise its name, size, date and folder. You can drop a file onto this panel to open it.

In H/W Info the same two panels carry that module's own content instead — the enlarged graphs on
the left, the specification list on the right. Everything below applies there unchanged.

Ways to bring a panel up:

- `F11` / `F12`, as described in [3.2](#32-panels-how-f11-and-f12-behave);
- the **edge button** — move the pointer close to the left or right edge of the window, near the
  bottom, and a small **pin** button appears just over the bottom bar. It opens that side as a
  sidebar, and closes it again if it already is one — the same toggle as the key.

Opening a module (rather than opening a file directly) starts with both sidebars open. A file
opened straight from Windows Explorer starts with no panels, so nothing covers what you came to
see. Press `Esc` when you are done and the file closes onto that standard screen — sidebars and
thumbnails — as if you had started the module from the menu.

---

## 5. The built-in file browser

Open a module without a file and the window becomes a file browser: folder tree and file list on the
left, thumbnails in the centre, file information on the right. The thumbnails are square, and the
grid is 4 columns wide with both sidebars docked, 6 with one and 8 with none. Image files show a
real miniature; text documents — `.txt .md .log .ini .html` and friends — show the first lines of
their content, loaded in the background so the grid never waits; everything else shows its
extension on the module's colour.

- **Each row in the left panel is two lines** — the file name on the first, and on the second its
  size, one fact from the file's own module — playing length for video and audio, resolution for an
  image, page count for a PDF, text encoding for a document, compression ratio for a ZIP — and the
  dates it was created and last modified, both written as `26-08-19`. Point at a row and the tooltip
  repeats the same values with labels such as **Size:**, **Length:**, **Created:** and **Modified:**
  in front of them, with the dates in full, so the two dates cannot be mistaken for one another.
- **A checkbox at the right-hand end of each row** shows whether that file is selected. Ticking or
  unticking it selects or deselects the file, and `Space` does the same to whichever item has the
  focus. It is one selection, not two — the boxes only make it visible.
- **Sort** by **Name**, **Size**, **Date modified** or **Date created**. Your choice is remembered.
- **Filter** by extension, or **Show all**. The Filter button says how many files are hidden.
- **Show hidden and system files** sits at the bottom of the same Filter menu, below a separator.
  It is off to start with, it is remembered, and it applies to the file list and to the folder tree
  alike.
- **Select** several files with `Ctrl`+click, `Shift`+click or `Ctrl+A`.
- **Copy, cut and paste** with `Ctrl+C` / `Ctrl+X` / `Ctrl+V`. Cut items go half-transparent in
  every KOTU window until you paste them; `Esc` clears that mark without emptying the clipboard.
- **Drag** files to another folder, another KOTU window or Windows Explorer. Within one drive that
  moves them, across drives it copies; hold `Ctrl` to copy or `Shift` to move regardless.
- **Rename** with `F2` or the right-click menu. `Enter` confirms, `Esc` cancels. A name that is
  empty, already taken or contains characters Windows refuses is not committed — the old name comes
  back and a short message says why.
- **Delete** with the `Delete` key or the right-click menu. Files go to the Recycle Bin, without a
  confirmation prompt.
- **Delete for good** with `Shift+Delete`. This one does ask — *Permanently delete this item?*, or
  *these n items?* — with **Delete** and **Cancel**, and Cancel is the button `Enter` presses. It is
  not in the right-click menu; the key is the only way in.
- **New folder** with `Ctrl+Shift+N` or the right-click menu on empty space. It is created and put
  straight into rename mode.
- **Open in a new window** with `Shift`+double-click, or **Open in new instance** from the
  right-click menu.
- **Open several at once**: select a few files and press `Enter`, or double-click one of them. The
  first opens the way a single file would; the rest each get their own window. Ten at a time is the
  limit — beyond that KOTU opens the first ten and says so. Folders are left out of this; a
  selection of folders alone behaves as before.

### Copying, moving and clashing names

Everything below applies to dragging, to `Ctrl+V` and to the **Paste** entries alike.

- When the destination already holds something of the same name, KOTU asks — *Replace or skip
  files*, with **Replace**, **Keep both** and **Skip**. `Enter` picks Replace. For folders, Replace
  merges the contents in rather than wiping the folder first, and files inside get the same three
  choices. Keep both appends ` (2)`, ` (3)` … as usual.
- With more clashes still to come, **Do this for all remaining conflicts** appears. It applies to
  whichever of the three buttons you press, and only for that one operation.
- `Esc` cancels. Whatever was already moved or copied stays where it went, and a short message says
  how far it got — *Move cancelled - 3 of 12 completed*.
- From three items up, a live message counts the work off — *Copying 3 of 12...*. One or two items
  finish too quickly to bother.
- If something cannot be done because Windows refuses permission, KOTU says *Access denied* and
  offers **Restart as admin**, which restarts the program with administrator rights and brings your
  windows back. Nothing is retried automatically — repeat the operation yourself afterwards.
- Anything else that fails is reported as a short message with the number of items and the first
  error. One failure never stops the rest.
- Cutting and copying carry over to Windows Explorer. Cut something there and paste it in KOTU and
  the files move rather than copy; KOTU marks its own cuts the same way, so Explorer reads them as
  a move too.

### The right-click menus

- **On a file** — Open in new instance · Cut · Copy · Rename · Delete.
- **On a folder** — Cut · Copy · Paste · Rename · Delete. Paste goes into that folder, not the one
  you are looking at.
- **On empty space** — New folder · Paste · Refresh. Paste is greyed out when there is nothing to
  paste.

Cut, Copy and Delete follow the same rule as dragging: if you right-clicked something that is part
of the selection, they act on the whole selection; if not, on that one item alone.

### Keeping up with the folder

KOTU watches the folder it is showing. Files added, removed or renamed by other programs appear
within about half a second, without a Refresh. If the folder itself is deleted or unplugged, the
view moves up to the nearest folder that still exists.

### The Open file button

The button beside the menu in the bottom bar — tooltip **Open file** — is how you open a file
without leaving what you are looking at. It does not open the Windows file dialog. Instead the file
browser takes over the window: whichever panels were already open stay as they are, the missing
ones are added, and the centre becomes a thumbnail browser.

Press `Esc`, or the button again, to go back to exactly the arrangement you had. Choosing a file
also ends it. If the browser is already what you are looking at, the button flashes the centre view
instead of opening anything.

---

## 6. File associations and the Start menu

KOTU registers nothing until you ask. Everything happens under your own user account, so no
administrator rights are needed, and switching a toggle off removes the registration completely.

In **Settings → Explorer integration**:

- One switch at the top for the Explorer right-click entries: **Extract here with KOTU-archive** on
  archive files and **Compress with KOTU-archive** on everything. On Windows 11 both live under
  **Show more options** (`Shift+F10`).
- **Register all file associations** below it — one switch that turns every module's file
  association on or off in one go. It drives the module switches under it, so each module still
  shows its own progress and result; a module that is busy at that moment is skipped, and you can
  flip that one by hand afterwards. It reads as on only while every module is registered.
- Then one switch per module — *Register KOTU-image file associations*, and the same for video,
  audio, document and archive — with the extensions listed beside it. Turning a switch on also
  tries to make KOTU the default app for those types. Windows protects a few types; for those, the
  Windows default-apps page opens so you can confirm once, or you can use **Set default...** for a
  single extension. The line under each switch tells you how many extensions KOTU is currently the
  default for.

If KOTU's own folder moves — after an update, or after you move a portable copy — the registrations
you turned on are repaired silently the next time it starts.

**The Start menu** is the button at the bottom-left corner (``Alt+` ``). It rises from the bar and
lists, from the bottom up: Min to tray, then All Readable, Image, Video, Audio, Document,
Archive, H/W Info, then Settings. There is no Exit entry here — closing the last window ends the
app, and the tray menu has **Exit KOTU**.

---

## 7. Several windows at once

KOTU runs as one program with as many windows as you like.

Ways to get a new one:

- `Shift+N` — a new, empty window of the module you are looking at;
- `Shift`+double-click a file in the built-in file browser;
- right-click a file there and choose **Open in new instance**.

Otherwise, opening a file re-uses an existing window of the same module.

Each window is independent: its own module, its own file, its own side panels, its own tray icon,
its own taskbar button, its own H/W Info selection. Every window carries its number at the front of
the title bar, so you can tell them apart in the taskbar and in `Alt`+`Tab`.

A new window inherits the size and position of the last window you closed.

**Minimising** a window works the standard Windows way — the window stays on the taskbar. To hide a
window completely, use the **Min to tray** entry at the bottom of the menu, or the same command in
the right-click menu of that window's tray icon (which spells it out as *Minimize to tray*). A
hidden window disappears from the taskbar and from `Alt`+`Tab`; its tray icon stays — click that
to bring it back.

---

## 8. Settings

The last entry in the menu at the bottom left. Each setting on that screen carries a one-line
description and a **Learn more** link; this is what those links point at.

### UI scale

Under **Display**. *System default* follows the Windows display scaling for this monitor, and the
list marks which entry that currently is. Picking a fixed value — 100% to 350% — overrides Windows
for KOTU only, and nothing else on the desktop changes.

A change takes effect in every open KOTU window immediately; there is nothing to restart. If the
Windows scaling on your monitor is a value the list does not offer, a note under the list says so.

This list is the only way to change the scale — there is no keyboard shortcut for it. `Ctrl`+`+` /
`Ctrl`+`-` and `Ctrl`+wheel belong to the content instead: in the Document module they zoom the
document ([2.4](#24-document--plain-text)), and `Ctrl`+wheel zooms an image or a PDF as it
always did.

### Explorer integration

The file-type switches and the right-click-menu switch, described in full in
[6](#6-file-associations-and-the-start-menu). In short:

- **Register all file associations** at the top flips every module switch at once. It reads as on
  only while every module is registered, and it leaves the right-click-menu switch alone.
- Everything is registered under **your own user account only**, so no administrator rights are
  needed, and turning a switch off removes the registration completely — nothing is left behind.
- Turning a switch on also tries to make KOTU the **default app** for those file types, so
  double-clicking one in Windows Explorer opens KOTU.
- Windows protects a few file types and can refuse that last step. When it does, the Windows
  default-apps page opens so you can confirm the change once by hand. You can also set a single
  extension at a time with **Set default...** beside each switch. The line under each switch says
  how many of that module's extensions KOTU is currently the default for.

### The settings file

**Open settings.json** opens the settings file itself in a new KOTU window, so you can read or edit
it with the built-in text editor.

- It lives in `%AppData%\KOTU\settings.json`.
- Changes you make by hand apply after a restart — KOTU does not re-read the file while it runs.
- Editing it incorrectly can break your settings. Nothing validates what you type; a malformed file
  is the one way to lose your preferences.

### Playback

**Auto-play next file** decides what happens when a video or a track reaches its end. On — the
default — the next file in the same folder starts, which is how the folder plays as a list
([2.2](#22-video), [2.3](#23-audio)). Off, playback simply stops at the end of each file.

- It is one switch for both modules: Video and Audio share it.
- It only has a say while the loop button is off. With **Loop list** or **Repeat this file** set,
  that mode plays on and this switch is ignored — including the pass after a repeat count runs out,
  which still moves to the next file.
- Turning it off does not stop you moving on by hand: opening the next file works as always.

### Updates

- **Current version** and **Latest version**, plus when the last check ran and how long it is until
  the next one — *Next check in 1:23*, counting down. While a check is running the line says so
  instead.
- KOTU checks for updates once whenever you open Settings, and then every two minutes for as long
  as you stay on this screen. Leave Settings and the checking stops, so nothing runs in the
  background while you work. There is no button to check by hand — opening this screen is the
  check. Nothing pops up when a new version is found — the Updates section is the only place it
  is announced.
- When there is one, an **Update to vX.Y.Z** button appears. It downloads with a progress figure,
  then asks: *KOTU will close and restart to finish installing.* **Install and restart** applies it
  immediately; **Later** keeps the download ready for the next time you press the button.
- Updates need the Setup.exe installation or the Velopack portable build. A build unpacked by hand
  says so, leaves the section disabled, and shows no countdown.

### About

The version, a link to the repository, and the mission statement.

The bottom bar of the Settings screen has a link to the project's Patreon page.

---

## 9. The tray icon

Every open window puts one small icon in the notification area, and keeps it there for as long as
the window lives.

- **Idle** — the whole icon is filled with the module's colour and carries one line in white:
  `IMG`, `VID`, `AUD`, `DOC`, `ARC` or `ALL`. H/W Info is the exception: it has no open-or-closed
  state, so it keeps its own muted `INF` on a dark badge.
- **With a file open** — a dark badge with a thin border in the module's colour, and two lines in
  that colour: what it is on top, a value underneath.
  An image shows its format and size, a video its resolution and bitrate, an archive its type and
  compression, and the Audio module shows a small level display. H/W Info shows the sensors you
  selected in that window.
- The icon's tooltip is the window's title.
- **Left-click** brings that window to the front.
- **Right-click** opens a menu: **Activate window** · **Minimize to tray** · **Close this window** ·
  **Exit KOTU**. The last one closes every window.

---

*KOTU is free and open source, MIT-licensed. Source, releases and issue tracker:*
*https://github.com/zpstudios/kotu*
