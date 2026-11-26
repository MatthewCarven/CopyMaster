# CopyMaster

A file copier for Windows Explorer. Drag files with the **right** mouse button, drop them on a
folder, and the menu offers **CopyMaster Copy here** and **CopyMaster Move here**. The job runs in
CopyMaster's own window instead of Explorer's:

- a queue per physical disk: jobs on one disk take turns, and jobs on different disks run side by side;
- a live speed limit on each job (1 to 128 MB/s, or unlimited), with a lower default for network shares;
- verify after copy (every copy read back and compared);
- pause, resume and cancel, even in the middle of a large file;
- a conflict dialog showing both files, and identical files counted as "already there" rather than
  asked about;
- failed files skipped with the reason, to retry at the end;
- a check for room before starting, and a question if the disk fills up anyway;
- long paths, dates, attributes and alternate data streams kept; follows Windows' dark mode.

After **Copy** or **Cut** in Explorer, right-click a folder, a folder's background or a drive and choose
**CopyMaster Paste** (a cut becomes a move). On Windows 11 it's in the compact menu too, through a small
package. [PLAN.md](PLAN.md) has the design, [TODO.md](TODO.md) what's left, and
[WORKLOG.md](WORKLOG.md) how it got here.

## How it fits together

`CopyMasterShell.dll` (C++) runs inside Explorer and does as little as possible: it writes the job to
`%LOCALAPPDATA%\CopyMaster\jobs` and starts `CopyMaster.exe` (C#, .NET 10, WinForms), which does all
the copying. A crash or a hang inside Explorer takes Explorer down, so none of the copying happens there.
A second launch of CopyMaster.exe hands its job to the window that's already open.

| Folder | What |
|---|---|
| `src/CopyMaster.Engine` | the engine: jobs, the queue, copy, verify, conflicts, single instance |
| `src/CopyMaster` | `CopyMaster.exe`, the window |
| `src/CopyMaster.Cli` | `cmcopy.exe`, the engine from a terminal |
| `src/CopyMasterShell` | the Explorer extension |
| `tools/ShellHost` | drives the extension without Explorer, for tests |
| `tests/CopyMaster.Engine.Tests` | xUnit tests |
| `package` | the Windows 11 package's manifest and logos |
| `setup` | install, uninstall, and a check for anything left behind |
| `dev` | the dev deploy, test volumes, window screenshots and UI Automation checks |

## Building and installing

Windows 11 (or 10, without the compact menu), the .NET 10 SDK, and Visual Studio 2026 with the C++
desktop workload; the Windows SDK's makeappx for the package. From an elevated PowerShell:

```powershell
.\build.ps1                           # everything; -Configuration Release optional
dotnet test tests/CopyMaster.Engine.Tests
.\setup\install.ps1                   # build Release, install to %ProgramFiles%\CopyMaster, register
.\setup\uninstall.ps1                 # take it all out again (asks about your settings)
.\setup\check.ps1                     # list anything of CopyMaster's on the machine
```

`dotnet build` can't build the C++ projects, so `build.ps1` uses Visual Studio's MSBuild. Installing
writes to HKLM and Program Files and registers the package for all users, so it needs an elevated shell.
The package is unsigned (no certificate is made or trusted): Windows 11 accepts it with
`Add-AppxPackage -AllowUnsigned`, which Microsoft describes as meant for testing. Both scripts take
`-WhatIf`. Uninstalling while Explorer has the extension loaded asks whether to restart Explorer (which
closes every Explorer window); otherwise the DLL is deleted when Windows next restarts.
`.\dev\reload.ps1` redeploys a fresh build over an install while working on the extension.

From a terminal: `CopyMaster.exe copy|move --dest <folder> <source>...`, or
`cmcopy.exe copy|move --dest <folder> [--verify] [--limit <MB/s>] [--conflict skip|overwrite|rename|newer] <source>...`.

## Authors

Matthew Carven, and Claude (Anthropic's AI model), who wrote the code with him through Claude Code.

## License

MIT: use it, change it and share it freely, including in other projects, as long as the copyright
notice stays with it. See [LICENSE](LICENSE).
