# Magazine Grabber

A small Windows desktop app (**.NET 10 / WPF**) for bulk-downloading magazine
scans (**PDF / DjVu / JP2**) from **archive.org** and **stare.e-gry.net**, with
optional automatic conversion to PDF.

Paste a listing / search / item / collection URL, tick the issues you want,
pick a folder, and let it download (and, where possible, convert) everything —
with per-item progress, a staged color-coded log, and a clickable list of the
files it produced.

---

## Table of contents

- [Features](#features)
- [Download (prebuilt binaries)](#download-prebuilt-binaries)
- [How it works](#how-it-works)
- [Prerequisites](#prerequisites)
- [Dependencies](#dependencies)
- [Building](#building)
- [Usage manual](#usage-manual)
- [Supported URLs](#supported-urls)
- [Output layout](#output-layout)
- [Converters (JP2 and DjVu)](#converters-jp2-and-djvu)
- [Troubleshooting](#troubleshooting)
- [Adding a new source](#adding-a-new-source)
- [Project layout](#project-layout)
- [Responsible use](#responsible-use)
- [License](#license)

---

## Features

- **Two sources out of the box** — archive.org (public items/collections) and
  stare.e-gry.net (interactive login via an embedded browser).
- **Bulk selection** — load a whole search/collection, tick what you want.
- **Per-item + overall progress**, plus an indeterminate bar during conversion.
- **Staged, color-coded log** — *Loading list → Downloading → Summary → Results*,
  with recognized-vs-produced counts.
- **Clickable results** — double-click any produced PDF/DjVu line to open it.
- **Automatic conversion**
  - JP2 page scans → single PDF (via `img2pdf`, Pillow fallback).
  - DjVu → PDF (via DjVuLibre `ddjvu`), when available.
- **Concurrency control** — a 1–5 slider (default 1), capped per source.
- **Cancel** any run, **Open folder** in one click.
- **Resumable** — already-downloaded sources and already-made PDFs are skipped.

## Download (prebuilt binaries)

Don't want to build it yourself? Grab a ready EXE from the
[**Releases**](../../releases) page — no Visual Studio or SDK needed. Each release
ships two Windows x64 builds:

| File | Needs .NET installed? | Size | Use when |
|---|---|---|---|
| `MagazineGrabber-<ver>-selfcontained-win-x64.exe` | **No** | ~60–90 MB | Any Windows 11 PC — just download and run. |
| `MagazineGrabber-<ver>-net10-required-win-x64.exe` | **Yes** — [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) | a few MB | You already have (or can install) the runtime. |

Both are a single `.exe`. WebView2 (used for the stare.e-gry.net login) is already
present on Windows 11. Optional PDF conversion still needs Python (JP2) or DjVuLibre
(DjVu) on `PATH` — see [Converters](#converters-jp2-and-djvu).

> Releases are produced automatically by GitHub Actions
> (`.github/workflows/release.yml`) whenever a `v*` tag is pushed.

## How it works

The app is built around a small **provider pattern**. Each source implements
`IMagazineProvider` (list items, download one item, optionally log in). `MainWindow`
picks the first provider whose `CanHandle(uri)` matches the pasted URL, and a
`DownloadManager` runs the selected items through that provider with limited
concurrency, a shared login gate, and per-item retry.

For each item the provider downloads raw source files into a `source\` subfolder
and places the best available **result** (a converted or copied PDF, or a raw DjVu)
into the chosen download folder. The manager tallies what actually landed —
PDFs generated vs DjVu left for manual conversion vs failures — and returns a
result set the UI turns into the summary and the clickable file list.

## Prerequisites

### To run the app

- **Windows 10/11** (x64).
- **.NET Desktop Runtime 10** — *only* for the small "framework-dependent" build.
  A **self-contained** build needs nothing installed (see [Building](#building)).
  > Note: Windows does **not** ship the modern .NET desktop runtime out of the box
  > (only the classic .NET Framework 4.8). Either install the .NET 10 Desktop Runtime,
  > or publish self-contained.
- **WebView2 Runtime** — used for the stare.e-gry.net login window. It is
  **pre-installed on Windows 11**; on Windows 10 it usually arrives via Edge, and
  can otherwise be installed from Microsoft's Evergreen WebView2 download.

### To build

- **.NET SDK 10** (includes the WPF workload on Windows).
- **Visual Studio 2022** (17.8+) or just the `dotnet` CLI.

### Optional — for automatic conversion

| Conversion   | Needs on `PATH`            | If missing                                    |
|--------------|----------------------------|-----------------------------------------------|
| JP2 → PDF    | **Python + pip**           | JP2 items fail to convert (raw files kept).   |
| DjVu → PDF   | **DjVuLibre (`ddjvu`)**    | DjVu files are kept as-is for manual convert. |

`img2pdf` and `Pillow` are installed automatically (via pip) on first use.
**DjVuLibre is not bundled** (it is GPL-licensed); install it yourself from the
official DjVuLibre distribution and make sure `ddjvu.exe` is on `PATH`.

## Dependencies

NuGet packages (restored automatically on build):

- [`HtmlAgilityPack`](https://www.nuget.org/packages/HtmlAgilityPack) — HTML parsing
  for the stare.e-gry.net listing.
- [`Microsoft.Web.WebView2`](https://www.nuget.org/packages/Microsoft.Web.WebView2) —
  embedded browser for interactive login.

## Building

All commands are run from the repository root (where `MagazineGrabber.sln` lives).

### From Visual Studio (one click)

Two publish profiles are included under
`MagazineGrabber/Properties/PublishProfiles/`:

- **SelfContained-SingleFile** — portable EXE, no runtime needed.
- **FrameworkDependent-SingleFile** — tiny EXE, needs the .NET 10 runtime.

Right-click the **MagazineGrabber** project → **Publish…** → pick the profile →
**Publish**. The output folder opens when it's done. (Or use the CLI commands below
from **View → Terminal** in Visual Studio.)

### Debug (day-to-day development)

```powershell
dotnet build
# or press F5 in Visual Studio
```

### Release — framework-dependent (smallest download, needs .NET 10 runtime)

```powershell
dotnet publish MagazineGrabber/MagazineGrabber.csproj -c Release -r win-x64 --self-contained false
```

Output: `MagazineGrabber\bin\Release\net10.0-windows\win-x64\publish\`.
Tiny (a few MB), but the target machine must have the **.NET 10 Desktop Runtime**.
Best for a managed fleet where you deploy the runtime once.

### Release — self-contained single file (portable, no runtime needed)

```powershell
dotnet publish MagazineGrabber/MagazineGrabber.csproj -c Release -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true
```

One self-extracting `MagazineGrabber.exe` that runs on a vanilla Windows 11 box
(WebView2 is already present there). Larger (~60–90 MB compressed) because it
bundles the .NET runtime + WPF.

### Release — self-contained, size-optimized

Add these to the command above (or set them in the `.csproj`) to trim the fat:

```powershell
  -p:PublishReadyToRun=false `
  -p:SatelliteResourceLanguages=en `
  -p:DebugType=none
```

> **Do not** use `-p:InvariantGlobalization=true` with this (or any) WPF app. It
> removes non-invariant cultures, and WPF's default `xml:lang="en-US"` then fails at
> startup with *"Cannot find non-neutral culture related to 'en-us'"*. Keep globalization on.

> **Do not** blindly add `-p:PublishTrimmed=true` for a WPF app — the trimmer can
> remove types used only via XAML/reflection and the app may crash at runtime.
> If you try it, test every screen thoroughly first.

## Usage manual

1. **Paste a URL** into the top box (see [Supported URLs](#supported-urls)) and
   click **Load list**. The status line shows progress while a large collection is
   read; the log prints how many items were recognized, broken down by format.
2. **Select** the rows you want (**Select all / none** help), then
   **Choose download folder…**.
3. **Set parallelism** with the **Parallel** slider (default **1** = gentlest).
   archive.org allows up to 5; stare.e-gry.net is always run at 1.
4. Click **Start download**. For stare.e-gry.net an embedded browser opens — log in
   on the real page, click **"I'm logged in"**, and the app reuses that session for
   the whole batch. Use **Cancel** to stop early.
5. When it finishes, the **Summary** compares recognized/selected vs downloaded vs
   PDFs-ready vs DjVu-left vs failed, and the **Results** section lists every file
   produced. **Double-click** a PDF/DjVu line to open it, or use **Open folder**.

## Supported URLs

**archive.org** — any of:

- **Search:** `https://archive.org/search?query=creator%3A%22ZPR+Express...%22`
- **Single item:** `https://archive.org/details/<identifier>`
- **Collection:** `https://archive.org/details/<collection-id>`

A `/details/<id>` URL is turned into the query
`(identifier:"<id>" OR collection:"<id>")` and run through the search API, so it
works whether `<id>` is one item or a whole collection.

**stare.e-gry.net** — a magazine listing page, e.g.
`https://stare.e-gry.net/czasopisma/click`.

## Output layout

```
<download folder>\
├─ <Magazine Title>.pdf            ← final PDFs (converted / copied) — the deliverables
└─ source\
   └─ <Magazine Title>\            ← raw fetched files (jp2 zip / djvu / pdf)
```

## Converters (JP2 and DjVu)

- **JP2 → PDF** extracts the `*_jp2.zip` page scans and combines them with a small
  generated Python script that tries `img2pdf` directly, then re-saves pages through
  Pillow if a page has an unusual colorspace. Requires **Python + pip** on `PATH`;
  `img2pdf`/`Pillow` install automatically on first run.
- **DjVu → PDF** shells out to DjVuLibre's `ddjvu -format=pdf`. Requires
  **DjVuLibre** on `PATH`. If it isn't installed, DjVu files are simply kept in
  `source\` and reported for manual conversion — the app keeps working.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| App won't start, missing runtime error | Install the **.NET 10 Desktop Runtime**, or use the **self-contained** build. |
| stare.e-gry.net login window blank | Install/repair the **WebView2 Runtime**. |
| JP2 items fail to convert | Install **Python** (tick "Add to PATH"); first run installs `img2pdf`/`Pillow`. |
| DjVu never becomes a PDF | Install **DjVuLibre** and ensure `ddjvu.exe` is on `PATH`. |
| First download after login used to fail | Fixed in 2.0 — cookies are re-scoped and the item retries automatically. |

## Adding a new source

Implement `IMagazineProvider` (see `Providers/`) — `CanHandle`, `ListItemsAsync`,
`DownloadAsync`, and the login members if the site needs auth — then add an
instance to the provider list in `MainWindow`. Nothing else changes.

## Project layout

```
MagazineGrabber.sln
MagazineGrabber/
  MagazineGrabber.csproj
  app.ico
  App.xaml(.cs)
  MainWindow.xaml(.cs)              ← UI, staged logging, summary/results, cancel
  LoginWebViewDialog.xaml(.cs)      ← embedded-browser login + cookie harvest
  Models/
    MagazineItem.cs                 ← row model (progress, status, selection)
    LogEntry.cs                     ← log line (level, section, file link)
  Providers/
    IMagazineProvider.cs            ← provider contract + DownloadOutcome
    ArchiveOrgProvider.cs
    StareEGryProvider.cs
  Services/
    DownloadManager.cs              ← concurrency, retry, login gate, tallies
    Jp2PdfConverter.cs              ← JP2 → PDF (img2pdf + Pillow fallback)
    DjVuPdfConverter.cs             ← DjVu → PDF (DjVuLibre ddjvu, optional)
  Utils/
    FileNaming.cs
```

## Responsible use

This tool automates downloads a browser could make. Content on archive.org and
stare.e-gry.net has its own licensing and terms of use — you are responsible for
downloading only what you're allowed to, respecting each site's terms and rate
limits, and keeping the default low concurrency so you don't hammer a server.
Intended for personal archival of material you have the right to access.

## License

[MIT](LICENSE) © 2026 Damian Kowalski.

Bundled/optional third-party components keep their own licenses: HtmlAgilityPack
(MIT), Microsoft.Web.WebView2 (Microsoft), and — if you install it separately —
DjVuLibre (GPL). DjVuLibre is intentionally **not** distributed with this project.
