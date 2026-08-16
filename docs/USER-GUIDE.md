# KOTU — User Guide

> **As of v0.146.0.** KOTU is under active development. This guide describes the behaviour of the
> version above; later releases may add or change things without this page being reissued.

KOTU opens photos, videos, music, documents, archives and hardware information in a single window.
Double-click a file and the matching module takes over the centre of the window and the bottom bar.
Everything below is written as "what you can do and how", not "why it works that way".

---

## Contents

1. [The window](#1-the-window)
2. [Modules](#2-modules)
3. [Keyboard shortcuts](#3-keyboard-shortcuts)
4. [Sidebars and overlays](#4-sidebars-and-overlays)
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
  the rest belongs to whichever module is open.
- **Two side panels** — a **left** panel (folder tree and file list) and a **right** panel
  (information about the current file). Both are hidden until you call them.

Two words are used consistently for the side panels:

- A **sidebar** is opaque. It stands beside the content and makes the centre narrower.
- An **overlay** is translucent. It lies over the content and takes no space away from it.

Either panel is 25% of the window width.

The title bar shows `KOTU`, or `KOTU - filename` once a file is open. When more than one window is
open, the window number is written directly in front, for example `2KOTU - holiday.jpg`. A leading
`●` means the document in that window has unsaved changes. The module is not named in the title —
the window icon carries a coloured ring instead, one colour per module.

**Opening a file.** Double-click it in Windows Explorer (if you registered the file type), drop it
onto the KOTU window, or use the **Open file** button in the bottom bar. Dropping a file whose type
does not belong to the module you are looking at opens it in a new window.

---

## 2. Modules

Seven modules. Switch between them with `Alt+1` … `Alt+7`, or from the menu at the bottom left.

| # | Module | Opens |
|---|---|---|
| 1 | All Readable | anything the other file modules can open |
| 2 | Image | `.jpg .jpeg .png .gif .bmp .webp .tif .tiff .ico .psd` |
| 3 | Video | `.mp4 .mkv .avi .webm .mov .wmv .m4v .mpg .mpeg .ts .m2ts .flv .3gp .ogv` |
| 4 | Audio | `.mp3 .flac .wav .ogg .opus .m4a .aac .wma` |
| 5 | Document | `.txt .md .markdown .log .ini .pdf` |
| 6 | Archive | `.zip .7z .rar .tar .gz .tgz .bz2 .xz` |
| 7 | H/W Info | no files — live hardware information |

### 2.1 Image

- Move through the folder with `←` and `→`. Files are sorted naturally, so `img2` comes before `img10`.
- **Zoom** with the mouse wheel, from 10% to 800%. The point under the pointer stays put.
  Touch pinch-zoom works too. Scroll bars appear when the picture is bigger than the view.
- **Rotate** 90° clockwise with the rotate button or `R`. Rotation is not saved to the file and
  resets when you move to the next picture. Orientation stored in EXIF is applied automatically.
- **Fit** — see [2.9](#29-the-fit-button). The Image module keeps your choice while you browse the folder.
- **Delete** removes the current file to the Recycle Bin and shows the next picture. There is no
  confirmation dialog and no toolbar button — the `Delete` key is the only way in.
- Animated GIFs play.
- The bottom bar shows the file name, size, kind, an EXIF summary (date taken, camera, exposure,
  aperture, ISO, focal length) where the format has one, and `width×height · position/total`.
- `F11` or a double-click on the picture goes full screen; `Esc` leaves it.

### 2.2 Video

- Playback uses the bundled libvlc engine. No codec pack is needed.
- **Play/pause** with `Space`, with the ▶ button, or by clicking the picture.
- **Seek** ±5 seconds with `←` / `→`, or drag the seek slider. Seeking happens once, when you let go.
- **Volume** ±5 with `↑` / `↓` or the mouse wheel. `M` mutes. The volume is remembered.
- **Speed**: 0.5× · 0.75× · 1× · 1.25× · 1.5× · 2×.
- **Subtitles** are picked up automatically from the video's own folder — `.srt .smi .ass .ssa .sub .vtt`.
  A file with exactly the video's name wins over a suffixed one (`movie.srt` before `movie.ko.srt`).
  Subtitles that are not UTF-8 are converted from CP949 before playback, so Korean `.srt` and `.smi`
  files display correctly. Press `C` or the subtitle button to switch track or choose **No subtitles**.
- **Resume**: if you watched more than 30 seconds and stopped before the last 3%, the position is
  remembered and playback continues there next time.
- **Zoom** with `Ctrl`+wheel, from 10% to 800%.
- **Fit** — see [2.9](#29-the-fit-button). Each new file starts at Contain again.
- `Enter`, `F11` or the ⛶ button goes full screen; `Esc` leaves it. In full screen, play, pause,
  seek, volume, mute and zoom each flash a short caption in the middle of the picture.
- Press ▶ with nothing open to play a built-in 32-second display-and-speaker test clip.

### 2.3 Audio

- Same transport as Video: `Space`, `←` / `→` (±5 s), `↑` / `↓` and the wheel (volume ±5), `M` to
  mute, `S` for the same six speeds. Clicking the surface also toggles play/pause.
- A live waveform fills the window while a track plays, with the track name above it.
- **Resume** works exactly as in Video (over 30 seconds played, stopped before the last 3%).
- `F11` or the ⛶ button goes full screen; `Esc` leaves it.
- Press ▶ with nothing open to play a built-in 18-second sample tune.

### 2.4 Document — plain text

- `.txt .md .markdown .log .ini` open in an editable text box. Markdown is shown as text; it is
  not rendered.
- The encoding is detected (UTF-8 with or without BOM, UTF-16, otherwise CP949) and kept on save,
  as is the line ending style (CRLF or LF).
- `Ctrl+S` saves. The save button stays disabled until there is something to save, and
  **● Unsaved** appears in the bottom bar while there is.
- If your edits contain characters the file's original CP949 encoding cannot store, KOTU offers to
  save the file as UTF-8 instead.
- Closing or switching away with unsaved changes asks first: **Save** / **Don't save** / **Cancel**.
- Files larger than 4 MB open read-only, showing the first 4 MB.
- `Tab` inserts a tab character in the editor.

### 2.5 Document — PDF

- Pages scroll continuously in one column. The bottom bar shows `current / total` as you scroll.
  There are no page-forward or page-back buttons.
- The wheel scrolls; `Ctrl`+wheel zooms around the pointer, about 10% per notch. Touch pinch-zoom works.
- The keyboard scrolls too: `↑` `↓` move about an eighth of the view, `Page Up` `Page Down` move
  about a full screen, and `Home` `End` jump to the start and the end of the document. Holding a key
  keeps scrolling. The zoom level never changes, and `←` `→` are not used.
- **Fit** — see [2.9](#29-the-fit-button). Zooming by hand releases the fit.

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

The window is split into three areas, with two more graphs in the bottom bar.

- **Left** — up to two enlarged graphs, one per selected sensor, covering the last 10 seconds.
- **Centre** — a tile per sensor, ten of them, each covering the last 30 seconds:
  CPU Temp, CPU Power, CPU Load, CPU Clock, GPU Temp, GPU Power, GPU Load, RAM, Fan, SSD Temp.
  **Click** a tile to select it — up to two at a time; picking a third releases the oldest.
  **Drag** a tile to reorder the grid.
- **Right** — the specification list, scrollable: CPU, GPU, RAM, Motherboard, Storage, Network, System.
- **Bottom bar** — two long graphs of the same selected sensors, covering up to 5 minutes.
  (At the fastest refresh settings the history is shorter than 5 minutes.)

Controls:

| Control | Key | What it does |
|---|---|---|
| Copy all | `C` | puts the whole specification list plus every current sensor value on the clipboard as text |
| Sensor refresh interval | `I` | 50 · 200 · 500 · 1000 · 2000 · 5000 ms. Default 500 ms. 50 ms costs noticeably more CPU |
| Bottom bar size | `B` | cycles Small → Medium → Large |
| Always on top | `P` | pins the window over other windows and collapses it down to just the bottom bar |
| Full screen | `F11` | full-screen dashboard; `Esc` leaves it |

The selected sensors are also what this window's tray icon displays. Selection, tile order and bar
size belong to each window separately; the most recent change is what a newly opened window starts from.

CPU temperature, CPU power, fan speed and drive temperature need administrator rights. When they
cannot be read, a **Restart as admin** button appears above the panes.

### 2.8 All Readable

One module that opens every file type the other modules handle. When you open a file, the centre
view and the bottom bar become that module's own; the window, the side panels and the tray icon stay
with All Readable. Ctrl+S in a text file, `E` in an archive and so on all keep working.

All Readable deliberately does not register file associations — double-clicking a file in Windows
Explorer still opens its dedicated module.

### 2.9 The Fit button

Image, Video and PDF share one Fit control with four options:

| Option | Result |
|---|---|
| **100%** | actual size, one image pixel per screen pixel. The button then reads `1:1` |
| **Contain** | the whole thing fits in the view. Never enlarged — smaller files stay at their own size |
| **Fit width** | fills the width; the other axis scrolls |
| **Fit height** | fills the height |

Clicking the body of the button re-applies the option you last chose (`F`). Clicking the arrow opens
the list. `A` jumps straight to 100%. New files start at Contain.

---

## 3. Keyboard shortcuts

### 3.1 Global

These work in every module.

| Key | Action |
|---|---|
| ``Alt+` `` | open the menu (bottom left) |
| `Alt+1` … `Alt+7` | switch module — 1 All Readable · 2 Image · 3 Video · 4 Audio · 5 Document · 6 Archive · 7 H/W Info |
| `Alt+0` | Settings |
| `F1` | left panel |
| `F2` | right panel |
| `Enter` | open or close both panels at once |
| `Esc` | leave full screen, or leave the Open file browser |
| `Shift+N` | new window of the module you are looking at |
| `F11` | full screen |
| `Ctrl+S` | save (text document) |

The `Alt` combinations and `F1` / `F2` work even while you are typing in a text box, because they
produce no character of their own. `Shift+N` gives way to typing.

Switching module with `Alt+1` … `Alt+7` or from the menu always arrives with both sidebars open.
Choosing the module you are already in changes nothing.

### 3.2 Panels: how `F1` and `F2` behave

`F1` drives the left panel, `F2` the right one. Both react to *how* you press them.

| Input | Result |
|---|---|
| Hold the key | the panel appears as a translucent **overlay** and stays while you hold it |
| Hold for 2 seconds | the overlay is pinned — it stays after you let go |
| Press twice quickly | the panel docks as an opaque **sidebar**, making the centre narrower |
| Press once while it is open | that panel closes |
| Press twice quickly while it is open | it stays closed |
| `F1` and `F2` together | both sides do all of the above at once, in any press order |
| `Enter` | closes both if either is open; opens them again in the arrangement you last had |

When a panel is pinned or docked, a short hint appears next to it — *Pinned - press F1 to close*,
*Sidebar - press F1 to close* — and fades after a couple of seconds.

One exception: while the file browser has the focus **and a file is selected**, `F2` renames that
file instead of calling the right panel — renaming keeps its usual Windows key.

Inside the Open file browser, `F1` and `F2` do nothing; `Esc` gets you out.

### 3.3 Letter keys, per module

Only one module is on screen at a time, so the same letter can mean different things in different
modules. Letter keys give way to typing: they do nothing while the text cursor is in an editable
box or while you are typing to jump through the file list.

| Module | Key | Action |
|---|---|---|
| Image | `R` | rotate 90° clockwise |
| Image | `A` | 100% |
| Image | `F` | re-apply the last Fit option |
| Image | `←` `→` | previous / next file in the folder |
| Image | `Delete` | move to the Recycle Bin |
| Video | `Space` | play / pause |
| Video | `←` `→` | back / forward 5 seconds |
| Video | `↑` `↓` | volume |
| Video | `M` | mute |
| Video | `S` | playback speed |
| Video | `C` | subtitles |
| Video | `A` | 100% |
| Video | `F` | re-apply the last Fit option |
| Video | `Enter` | full screen |
| Audio | `Space` | play / pause |
| Audio | `←` `→` | back / forward 5 seconds |
| Audio | `↑` `↓` | volume |
| Audio | `M` | mute |
| Audio | `S` | playback speed |
| Document | `Ctrl+S` | save |
| Document | `A` | 100% (PDF) |
| Document | `F` | re-apply the last Fit option (PDF) |
| Document | `↑` `↓` | scroll the PDF, about an eighth of the view |
| Document | `Page Up` `Page Down` | scroll the PDF, about a full screen |
| Document | `Home` `End` | start / end of the PDF |
| Archive | `E` | Extract here |
| Archive | `T` | Extract... (choose a folder) |
| Archive | `C` | New archive |
| Archive | `U` | up one level inside the archive |
| H/W Info | `C` | Copy all |
| H/W Info | `I` | sensor refresh interval |
| H/W Info | `B` | bottom bar size |
| H/W Info | `P` | always on top |

`F11` and `Esc` behave the same in all seven modules.

### 3.4 In the file browser

| Key | Action |
|---|---|
| `Enter` | open the selected item |
| `F2` | rename the selected item (with nothing selected, `F2` calls the right panel as usual) |
| `Delete` | move to the Recycle Bin |
| `Ctrl+C` / `Ctrl+X` / `Ctrl+V` | copy / cut / paste |
| `Ctrl+A` | select all |
| `Ctrl+Shift+N` | new folder |

### 3.5 Mouse and wheel

| Where | Input | Action |
|---|---|---|
| Image | wheel | zoom, 10%–800% |
| Image | double-click | full screen |
| Video | wheel | volume |
| Video | `Ctrl`+wheel | zoom, 10%–800% |
| Video | click | play / pause |
| Audio | wheel | volume |
| Audio | click | play / pause |
| PDF | wheel | scroll |
| PDF | `Ctrl`+wheel | zoom |
| Text editor | wheel | scroll |
| File browser, side panels | wheel | scroll |
| File browser | double-click | open (folders: go in) |
| File browser | `Shift`+double-click | open in a new window |
| File browser | right-click | Open in new instance · Rename · Delete |
| File browser | drag | move within a drive, copy across drives. `Ctrl` forces copy, `Shift` forces move |
| Near the left or right window edge | move the pointer | a narrow edge button appears; click it to dock or undock that sidebar |

The wheel never zooms thumbnails, and it never zooms while the pointer is over a side panel — it
scrolls there.

---

## 4. Sidebars and overlays

**The left panel** holds, from top to bottom:

- the path bar, with the **Up** button, the current folder, a **Filter** list and a **Sort** list;
- a tree of drives and folders, opened at the folder of the file you have open;
- the file list for that folder.

**The right panel** describes the file you have open — whatever the module knows about it, or
otherwise its name, size, date and folder. You can drop a file onto this panel to open it.

Ways to bring a panel up:

- `F1` / `F2`, as described in [3.2](#32-panels-how-f1-and-f2-behave);
- `Enter` for both at once;
- the **edge button** — move the pointer close to the left or right edge of the window and a small
  button appears there. Clicking it docks or undocks that sidebar.

Opening a module (rather than opening a file directly) starts with both sidebars docked. A file
opened straight from Windows Explorer starts with no panels, so nothing covers what you came to see.

---

## 5. The built-in file browser

Open a module without a file and the window becomes a file browser: folder tree and file list on the
left, thumbnails in the centre, file information on the right.

- **Sort** by **Name**, **Size**, **Date modified** or **Date created**. Your choice is remembered.
- **Filter** by extension, or **Show all**. The Filter button says how many files are hidden.
- **Select** several files with `Ctrl`+click, `Shift`+click or `Ctrl+A`.
- **Copy, cut and paste** with `Ctrl+C` / `Ctrl+X` / `Ctrl+V`.
- **Drag** files to another folder, another KOTU window or Windows Explorer. Within one drive that
  moves them, across drives it copies; hold `Ctrl` to copy or `Shift` to move regardless.
- **Rename** with `F2` or the right-click menu. `Enter` confirms, `Esc` cancels.
- **Delete** with the `Delete` key or the right-click menu. Files go to the Recycle Bin, without a
  confirmation prompt.
- **New folder** with `Ctrl+Shift+N`. It is created and put straight into rename mode.
- **Open in a new window** with `Shift`+double-click, or **Open in new instance** from the
  right-click menu.

### The Open file button

The button beside the menu in the bottom bar — tooltip **Open file** — is how you open a file
without leaving what you are looking at. It does not open the Windows file dialog. Instead the file
browser appears translucently over the current view: whichever panels were already open stay as they
are, the missing ones are added as overlays, and the centre becomes a translucent thumbnail browser.

Press `Esc`, or the button again, to go back to exactly the arrangement you had. Choosing a file
also ends it. If the browser is already what you are looking at, the button flashes the centre view
instead of opening anything.

---

## 6. File associations and the Start menu

KOTU registers nothing until you ask. Everything happens under your own user account, so no
administrator rights are needed, and switching a toggle off removes the registration completely.

In **Settings → Explorer integration**:

- One switch per module — *Register KOTU-image file associations*, and the same for video, audio,
  document and archive — with the extensions listed beside it. Turning a switch on also tries to
  make KOTU the default app for those types. Windows protects a few types; for those, the Windows
  default-apps page opens so you can confirm once, or you can use **Set default...** for a single
  extension. The line under each switch tells you how many extensions KOTU is currently the default for.
- One switch for the Explorer right-click entries: **Extract here with KOTU-archive** on archive
  files and **Compress with KOTU-archive** on everything. On Windows 11 both live under
  **Show more options** (`Shift+F10`).

If KOTU's own folder moves — after an update, or after you move a portable copy — the registrations
you turned on are repaired silently the next time it starts.

**The Start menu** is the button at the bottom-left corner (``Alt+` ``). It rises from the bar and
lists, from the bottom up: All Readable, Image, Video, Audio, Document, Archive, H/W Info, then
Settings. Each entry's tooltip shows its shortcut, `Alt+1` through `Alt+7` and `Alt+0`. There is no
Exit entry here — closing the last window ends the app, and the tray menu has **Exit KOTU**.

---

## 7. Several windows at once

KOTU runs as one program with as many windows as you like.

Ways to get a new one:

- `Shift+N` — a new, empty window of the module you are looking at;
- `Shift`+double-click a file in the built-in file browser;
- right-click a file there and choose **Open in new instance**;
- turn on **Always open files in a new instance** in Settings, and every file opens its own window.

With that setting off (the default), opening a file re-uses an existing window of the same module.

Each window is independent: its own module, its own file, its own side panels, its own tray icon,
its own H/W Info selection. From the second window onwards, the window number appears in the title
bar and as a small coloured badge over the menu button.

A new window inherits the size and position of the last window you closed.

**Minimising** a window hides it from the taskbar and from `Alt`+`Tab`. Its tray icon stays — click
that to bring it back.

---

## 8. Settings

`Alt+0`, or the last entry in the menu.

**Display**

- **UI scale** — *System default* (follow Windows), or a fixed 100% to 350%. A fixed value applies
  to KOTU only, and takes effect in every open window immediately.

**Windows**

- **Always open files in a new instance** — off by default. See [7](#7-several-windows-at-once).

**Explorer integration** — see [6](#6-file-associations-and-the-start-menu).

- **Open settings.json** — opens the settings file itself in a new KOTU window. It lives in
  `%AppData%\KOTU\settings.json`. Changes you make by hand apply after a restart; editing it
  incorrectly can break your settings.

**Updates**

- **Current version** and **Latest version**, plus when the last check ran.
- KOTU checks for updates on its own every two minutes, once whenever you open Settings, and
  whenever you press **Check now**. Nothing pops up when a new version is found — the Updates
  section is the only place it is announced.
- When there is one, an **Update to vX.Y.Z** button appears. It downloads with a progress figure,
  then asks: *KOTU will close and restart to finish installing.* **Install and restart** applies it
  immediately; **Later** keeps the download ready for the next time you press the button.
- Updates need the Setup.exe installation or the Velopack portable build. A build unpacked by hand
  says so and leaves the section disabled.

**About** — the version, a link to the repository, and the mission statement.

The bottom bar of the Settings screen has a link to the project's Patreon page and the full-screen
button.

---

## 9. The tray icon

Every open window puts one small icon in the notification area, and keeps it there for as long as
the window lives.

- **Idle** — one line, in muted colour: `IMG`, `VID`, `AUD`, `DOC`, `ARC`, `ALL` or `INF`, according
  to the module.
- **With a file open** — two lines in the module's colour: what it is on top, a value underneath.
  An image shows its format and size, a video its resolution and bitrate, an archive its type and
  compression, and the Audio module shows a small level display. H/W Info shows the sensors you
  selected in that window.
- The icon's tooltip is the window's title.
- **Left-click** brings that window to the front.
- **Right-click** opens a menu: **Activate window** · **Close this window** · **Exit KOTU**.
  The last one closes every window.

---

*KOTU is free and open source, MIT-licensed. Source, releases and issue tracker:*
*https://github.com/zpstudios/kotu*
