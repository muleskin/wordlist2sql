# wordlist2sql

A Windows desktop app (WinForms, .NET 8) that imports very large — gigabyte-sized —
one-word-per-line text wordlists into a single **SQLite** database file, giving each
wordlist its own table. Tables can be exported straight back to a `.txt` file, or
pulled over HTTP with `curl`.

## Features

- **Streaming import** of multi-GB files — words are read line by line and inserted in
  200k-row transactions, so memory stays flat regardless of file size.
- **One table per wordlist** — the table is named after the source file (sanitized to a
  safe SQLite identifier, e.g. `Rock-You! 2024.txt` → `Rock_You__2024`).
- **Optional de-duplication** — tick the box to keep only unique words
  (`WITHOUT ROWID` table with a text primary key, `INSERT OR IGNORE`).
- **Direct export** — dump any table back to a UTF-8 `.txt`, one word per line.
- **Curl / HTTP server** — start the built-in server and fetch a table from anywhere:
  ```
  curl http://localhost:8088/                  # list tables
  curl http://localhost:8088/rockyou -o out.txt # stream a table to a file
  ```
- **Cheap stats** — word counts come from a `_wordlist_meta` bookkeeping table, so the
  table list never does a full `COUNT(*)` scan on a huge table.
- Progress bar + cancel for both import and export; multiple files can be queued in one go.

## Build & run

Requires the .NET 8 SDK (Windows Desktop).

```powershell
dotnet build -c Release
dotnet run   -c Release
```

The native SQLite binaries (`runtimes/win-x64`, `win-x86`) are restored automatically
via the `System.Data.SQLite.Core` NuGet package.

## Single-file build (one standalone .exe)

To produce a single, self-contained `wordlist2sql.exe` with the .NET runtime,
WinForms, and the managed + native SQLite libraries all bundled inside it:

```powershell
dotnet publish -c Release -r win-x64
```

Output: `bin\Release\net8.0-windows\win-x64\publish\wordlist2sql.exe` — one file,
no other DLLs, runs on any 64-bit Windows machine **without** the .NET runtime
installed (~70 MB; the native `SQLite.Interop.dll` is bundled and extracted to a
temp folder on first run).

The single-file settings live in the `.csproj` and only activate when a
`-r <RID>` is given, so a plain `dotnet build` stays a fast multi-file dev build.
For a 32-bit build use `-r win-x86`.

### Smaller, framework-dependent exe (~4 MB)

If the target machine has (or can install) the **.NET 8 Desktop Runtime**, you can
ship a much smaller single exe that leaves the runtime out:

```powershell
dotnet publish wordlist2sql.csproj -c Release -r win-x64 --self-contained false -o bin\publish-fd
```

Output: `bin\publish-fd\wordlist2sql.exe` — a single ~4 MB file (vs. ~70 MB
self-contained). The native `SQLite.Interop.dll` is still bundled inside. It needs
the .NET 8 Desktop Runtime present; if it's missing, Windows shows a prompt with a
download link on launch.

| Variant | Command | Size | Needs runtime installed? |
|---------|---------|------|--------------------------|
| Self-contained | `dotnet publish -c Release -r win-x64` | ~70 MB | No |
| Framework-dependent | `… -r win-x64 --self-contained false` | ~4 MB | Yes (.NET 8 Desktop) |

Compression is applied automatically to the self-contained build only (it isn't
supported for framework-dependent single files).

### Recommended: small app + self-installing launcher

The best of both worlds — a small download that still "just works" on a PC without
the runtime. A tiny **Native-AOT bootstrapper** (`launcher/`) ships next to the
framework-dependent app. Being AOT-compiled it has *no* .NET dependency itself, so
it always runs; on launch it:

1. checks whether the **.NET 8+ Desktop Runtime** is installed,
2. if not, offers to download it from Microsoft's official link and installs it
   (passive UI, auto-elevates via UAC), then
3. starts `wordlist2sql.exe`.

Build the whole distributable with one command:

```powershell
.\build-dist.ps1            # -Rid win-x86 for 32-bit
```

Output is `dist\` with two files (~8.6 MB total):

| File | What it is |
|------|------------|
| `wordlist2sql-launcher.exe` | Native AOT bootstrapper — **this is what users run** |
| `wordlist2sql.exe` | The framework-dependent app it launches |

Ship both together. The app sets `RollForward=Major`, so it runs on the .NET 8, 9,
or 10 Desktop Runtime — whichever is present.

> **Build requirement:** the AOT launcher needs the Visual Studio **"Desktop
> development with C++"** workload (MSVC linker). `build-dist.ps1` locates it via
> `vswhere` and imports the VC environment automatically. The framework-dependent
> app alone does not need C++ tools.

## How to use

1. **Open / Create…** — choose where the `.db` file lives (created if missing).
2. **Import wordlist file(s)…** — pick one or more `.txt` files; each becomes a table.
   Tick *De-duplicate* first if you want unique words only.
3. Select a table and **Export to file…** to write it back out.
4. Set a **port** and **Start server** to expose tables over HTTP for `curl`.

## Performance notes

The database is opened with `journal_mode=OFF`, `synchronous=OFF`, and a large page
cache for maximum import throughput. This trades crash-durability for speed, which is
appropriate here: the original wordlist file is the source of truth, so an interrupted
import can simply be re-run.

## Project layout

| File | Purpose |
|------|---------|
| `Program.cs`     | App entry point. |
| `MainForm.cs`    | The full UI (built in code) and async import/export/server wiring. |
| `WordlistDb.cs`  | All SQLite access: streaming import, export, listing, metadata. |
| `ExportServer.cs`| Embedded `HttpListener` server for curl access (read-only). |
