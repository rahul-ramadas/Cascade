<div align="center">

# Cascade

**A fast log and text analyzer for very large files, with hierarchical filters.**

An enhanced, from-scratch reimagining of [TextAnalysisTool.NET](https://textanalysistool.github.io/)
for Windows — same idea, same muscle memory, built for files that no longer fit in memory.

[![CI](https://github.com/rahul-ramadas/Cascade/actions/workflows/ci.yml/badge.svg)](https://github.com/rahul-ramadas/Cascade/actions/workflows/ci.yml)

![Cascade](docs/images/hero.png)

</div>

---

## What it is

You point Cascade at a log, describe what you care about as a set of **filters**, and it colours the
lines that match and dims (or hides) the ones that don't. That is TextAnalysisTool.NET's idea, and
Cascade keeps it — along with markers, find, encodings, saved filter sets and `.tat` import.

What's different:

| | |
|---|---|
| **Opens anything** | Memory-mapped I/O and a streaming line index. A 1 GB / 10 M-line log is on screen before you can let go of the mouse, and fully indexed in **under half a second**. Nothing is loaded into RAM up front, so file size is not bounded by memory. |
| **Filtering streams** | Matching lines appear while the rest of the file is still being scanned — and the view **stays where you put it** while that happens. |
| **Filters nest** | The headline feature. A filter can refine its parent, so `[ERROR]` → `payment-svc` means *errors, and only in payments*, with its own colour. |
| **Filter list is searchable** | Type to jump between filters in a list of hundreds. It never hides or reorders the list. |
| **Columns** | Split each line into named columns for display, hide the ones you don't want. Filtering still runs on the whole raw line. |
| **RDP-friendly** | WinForms + GDI, no GPU. Text and rectangles travel over Remote Desktop as compact drawing orders instead of bitmaps. |

---

## Install

Grab `Cascade-<version>-win-x64.exe` from the [latest release](../../releases/latest). It is a
**single file** — no installer, no install folder. Copy it anywhere and run it.

Requires **Windows** and the **.NET 10 Desktop Runtime**. The only thing Cascade writes is
`%APPDATA%\Cascade\settings.json` and `state.json` (and a crash log in `%TEMP%` if it ever needs one).
No registry keys.

It also **updates itself**: at startup it checks GitHub on a background thread, downloads quietly,
and tells you in the menu bar. The swap happens on the next launch, atomically, and the old image
deletes itself.

---

## Opening a large file

![Opening a 1 GB log](docs/images/open.gif)

The window is up and scrollable before indexing finishes; the line count and the per-filter counts
climb as the scan progresses, and filtering starts immediately behind it. Measured on a 1 GB,
10,000,000-line log with the 9 filters shown above (warm file cache):

| | |
|---|---|
| First lines available to the view | **2–4 ms** |
| Fully indexed (10 M lines) | **364–441 ms** |
| First full filter pass finished | **381–810 ms** |
| Toggling a filter afterwards | **6–14 ms** (served from a 5.3 MB match cache) |

Only the rows on screen are ever decoded, and the index costs 8 bytes per line.

---

## Filters

A filter is a **substring**, a **.NET regular expression**, or **"marked by marker N"**, plus a
case-sensitivity flag, an optional description, and any of foreground colour, background colour,
bold and italic.

![The filter editor](docs/images/filter-edit.png)

Filters are either **includes** (show these lines) or **excludes** (and not these). Anything you
leave unset is inherited from the parent filter, per property — so a child can add a background
colour and keep its parent's red bold text.

### Hierarchy

![The filter list](docs/images/filter-list.png)

Nesting is what makes a large filter set manageable. The rules are small enough to keep in your head:

- A filter matches a line only if **its own pattern and every ancestor's pattern** match — whether or
  not those ancestors are enabled. `[ERROR]` → `payment-svc` therefore means *payment-svc lines that
  are also errors*, not *payment-svc lines*.
- A line is **shown** when some enabled include matches it and no enabled exclude does.
- Its colour comes from the **deepest** enabled include that matches (ties go to the topmost),
  with each unset property inherited from the nearest ancestor that sets it.
- Because a parent's pattern constrains its children whether or not the parent is enabled,
  *parent off, children on* is a useful arrangement — scope to `[ERROR]`, then surface only the
  categories you're interested in.
- An exclude removes lines rather than scoping them, so it belongs at the end of a chain rather than
  having children of its own. Nesting goes up to 8 levels deep.

Each row shows the pattern, its description, and a **live count** of matching lines that updates
while the file is being scanned.

### Dim or hide

![Dimming and hiding the non-matching lines](docs/images/dim-or-hide.gif)

`Ctrl+H` switches between dimming non-matching lines and hiding them entirely. Original line numbers
are always shown — so a filtered view still tells you where you are in the file — and the line you
were on holds its place on screen through the switch.

### Working the list

![Toggling filters](docs/images/filters.gif)

Ticking a filter or disabling a whole subtree with `Shift+Space` re-evaluates the file, and again the
line you were looking at **stays exactly where it was on screen**, even while several million rows
are being added or removed above it.

![Searching the filter list](docs/images/search.gif)

`Ctrl+E` focuses the search box. Typing dims the filters that don't match and jumps to the ones that
do (`Enter` / `Shift+Enter` to walk them); it never hides or reorders the list, so the filter you
were looking at doesn't move.

You can also:

- **Reorder and nest** with `Ctrl+↑ ↓ ← →`, or by dragging — the filter and its subtree move live to
  where they would land, so you can see the result before you drop.
- **Jump to matches of one filter** with `F4` / `Shift+F4`, without changing what's shown.
- **Create a filter from the current line** with `Ctrl+N`, prefilled with the line's text.
- **Dock the list** to any edge with `Ctrl+Shift+↑ ↓ ← →`, or hide it with `Ctrl+Shift+L`.

---

## Find

![The find dialog](docs/images/find.png)

Literal or regular-expression search, forwards or backwards, with progress and a cancel button for
the times you search a 1 GB file for something that isn't there. The search runs in the background,
sweeping outwards from the caret, and remembers what it has already examined — so the first search
costs a scan and every `F3` after it is instant.

In filtered mode, find only ever lands on a line you can actually see.

When a search runs out of matches — plain find, per-filter find, the filter-list search, or marker
navigation — the whole window flashes briefly and the status bar says why.

---

## Markers

Eight markers, `Ctrl+1`…`Ctrl+8` to toggle one on the selected lines, `1`…`8` and `Shift+1`…`Shift+8`
to walk them, drawn as coloured bars in the left gutter. Markers are also a filter type, so you can
hand-pick lines and then treat them as a category — colour them, exclude them, or nest something
under them. The gutter can be shown always, never, or only when markers are in use.

---

## Columns

![Columns](docs/images/columns.png)

Split each line for display, either on a **single delimiter** (optionally collapsing runs of it, with
an optional split limit) or with a **bracket template** naming each field:

```
[timestamp] [[service]] [[level]] [[request]] [message]
```

Each `[name]` is a column; everything else — including the literal brackets written as `[[name]]` —
is separator. Columns can be renamed, resized, aligned and hidden (`request` is hidden above), and
splitting is lazy, so it costs nothing on a huge file. **Filtering always runs on the whole raw
line**, so turning columns on can't change what you see.

---

## Everything else

- **Opening files** — `Ctrl+O`, `F5` to reload, *Open from Clipboard*, a recent-files list, or a path
  on the command line.
- **Encodings** — auto-detect (BOM first), UTF-8, UTF-16 LE/BE, Windows-1252, or the system default;
  reopen with a different one at any time.
- **Copy** — selected lines, with or without line numbers. Selection is multi-line with `Shift` and
  `Ctrl`.
- **Save Current Lines** — writes exactly what the filters are showing to a new file.
- **Filter sets** — save, load and append `.cascade` files; the last one you used is reloaded at
  startup (switch that off in preferences). The title bar and status bar always name the filter file
  in use and mark it dirty when it has unsaved changes.
- **Preferences** — font and size, text/background/selection/dimmed colours, tab size, line numbers,
  marker visibility, filter auto-load. Export and import them as a file to move them between
  machines; recent files and other machine-local state are deliberately left out.
- **Status bar** — file path, filter-file path, what it's busy with and how far along, then selected /
  filtered / total line counts, the caret line, and the zoom level. The fields have fixed widths, so
  nothing jitters while the numbers change.
- **Zoom** — `Ctrl++`, `Ctrl+-`, `Ctrl+0`, or `Ctrl`+wheel.
- **Keyboard-complete** — every feature has a shortcut or a menu item, `Tab` cycles the three panes,
  and the focused pane is marked with an accent bar.
- **Accessible** — the log view exposes each visible row to UI Automation with its line number and
  selection state, which is also how the automated UI tests drive the app.
- **Survives being killed** — settings, state and filter files are written atomically as you change
  them, not on the way out, so ending the process from Task Manager loses nothing.

---

## Keyboard reference

<details>
<summary><b>Log view</b></summary>

| Key | Action |
|---|---|
| `↑` `↓` `PgUp` `PgDn` | Move the caret (`Shift` extends the selection) |
| `Ctrl+Home` / `Ctrl+End` | First / last line |
| `Ctrl+↑` / `Ctrl+↓` | Scroll a line without moving the caret |
| `←` `→` | Scroll sideways four characters |
| `Home` / `End` | Scroll to the far left / far right |
| `Ctrl+A` | Select all |
| `Ctrl+C` | Copy |
| `1`…`8` / `Shift+1`…`8` | Next / previous line with that marker |
| `Ctrl+1`…`Ctrl+8` | Toggle that marker on the selection |
| `Ctrl`+wheel | Zoom |

</details>

<details>
<summary><b>Filter list</b></summary>

| Key | Action |
|---|---|
| `Space` | Enable / disable the selected filter |
| `Shift+Space` | Enable / disable it and everything under it |
| `Enter` | Edit the selected filter |
| `Delete` | Remove it |
| `Ctrl+↑` / `Ctrl+↓` | Move it up / down |
| `Ctrl+→` / `Ctrl+←` | Nest it under the filter above / un-nest it |
| `F4` / `Shift+F4` | Next / previous line matching it |
| `Ctrl+F` | Focus the search box |
| `F3` / `Shift+F3` | Next / previous filter matching the search box |
| `Esc` | Clear the search box |

</details>

<details>
<summary><b>Global</b></summary>

| Key | Action |
|---|---|
| `Ctrl+O` / `F5` | Open / reload |
| `Ctrl+S` | Save filters |
| `Ctrl+F` / `F3` / `Shift+F3` | Find / next / previous |
| `Ctrl+G` | Go to line |
| `Ctrl+H` | Show only filtered lines |
| `Ctrl+N` | New filter from the current line |
| `Ctrl+E` | Focus the filter search box |
| `Ctrl+Shift+T` / `Ctrl+Shift+F` | Focus the log view / the filter list |
| `Ctrl+Shift+L` | Show or hide the filter list |
| `Ctrl+Shift+↑ ↓ ← →` | Dock the filter list |
| `Ctrl++` / `Ctrl+-` / `Ctrl+0` | Zoom in / out / reset |
| `Tab` / `Shift+Tab` | Cycle focus between the panes |
| `Esc` | Cancel a running search |

</details>

---

## Files

- **`.cascade`** — the native filter set: indented JSON holding the whole filter tree with its
  per-property styles, the column spec, and whether filtered mode is on. Versioned and
  additive, so it is diffable and safe to keep in source control.
- **`.tat`** — TextAnalysisTool.NET filter files are **imported** (flattened to top-level filters,
  keeping text, regex and case flags, colours, enabled and excluding state, descriptions and
  markers). Saving is always `.cascade`.
- **Settings** — `%APPDATA%\Cascade\settings.json` (portable preferences; this is what
  Export/Import moves) and `state.json` (recent files and other machine-local state, never exported).
  `CASCADE_SETTINGS_DIR` overrides the folder.

## Command line

```
Cascade.exe [file] [/Filters:<path>] [/demo]
```

`/Filters:` takes a `.cascade` or `.tat` file and suppresses the automatic reload of the last one.
Only the last file and the last `/Filters:` are used.

As the **first** argument: `--help`, `--version`, `--selftest` (headless engine, settings and
rendering checks), `--screens <dir>` (render every dialog and the main window to PNGs).
`CASCADE_UPDATE=off` disables the update check.

---

## Coming from TextAnalysisTool.NET

Everything you rely on is here: the filter list with checkboxes and live counts, include/exclude
filters, substring and regex matching, colours per filter, the 8 markers, `Ctrl+H`, find,
encodings, zoom, dockable filter list, recent files, and `.tat` import.

A few things are deliberately different:

- **Filters nest.** Flat `.tat` sets import unchanged, so this only costs you something when you
  want it.
- **Saving is `.cascade`, not `.tat`** — the old format cannot express a hierarchy or per-property
  style inheritance.
- **No plug-ins.** Files are read as text.
- **Copy is plain text** (optionally with line numbers); there is no HTML/colour clipboard format.
- **No `A`–`Z` filter cycling or `Space` for the next matching line.** The equivalents are `F4` /
  `Shift+F4` on the selected filter, and the filter-list search for reaching a filter quickly.
- **No live tail**, and `/Config:`, `/Line:` and `/Clipboard` are not implemented.

---

## Building

```powershell
dotnet build Cascade.slnx -c Release
dotnet test tests/Cascade.Core.Tests/Cascade.Core.Tests.csproj   # engine tests
./scripts/Run-UiTests.ps1 -Publish                               # UI automation, off-screen
```

.NET 10 SDK. `src/Cascade.Core` is a UI-agnostic engine (mapping, indexing, filtering, find, markers,
columns, persistence, updating) with no reference to WinForms; `src/Cascade.App` is the GUI. Design
notes and the reasoning behind the engine live in [docs/DESIGN.md](docs/DESIGN.md).
