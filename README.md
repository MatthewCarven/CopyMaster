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

Right-click **Paste** (phase 4) and the Windows 11 compact menu with an installer (phase 5) are still
to come. [PLAN.md](PLAN.md) has the design, [TODO.md](TODO.md) what's left, and
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
| `dev` | deploy script, test volumes, window screenshots and UI Automation checks |

## Building

Windows 11, the .NET 10 SDK, and Visual Studio 2026 with the C++ desktop workload.

```powershell
.\build.ps1                           # everything; -Configuration Release optional
dotnet test tests/CopyMaster.Engine.Tests
.\dev\reload.ps1                      # build Release, install to %LOCALAPPDATA%\Programs\CopyMaster, register
.\dev\reload.ps1 -Unregister          # take the Explorer extension out again
```

`dotnet build` can't build the C++ projects, so `build.ps1` uses Visual Studio's MSBuild. Registering
writes to HKLM, so it needs an elevated shell. `reload.ps1` restarts Explorer only when Explorer has an
older copy of the extension loaded (that closes every Explorer window).

From a terminal: `CopyMaster.exe copy|move --dest <folder> <source>...`, or
`cmcopy.exe copy|move --dest <folder> [--verify] [--limit <MB/s>] [--conflict skip|overwrite|rename|newer] <source>...`.

## Authors

Matthew Carven, and Claude (Anthropic's AI model), who wrote the code with him through Claude Code.
