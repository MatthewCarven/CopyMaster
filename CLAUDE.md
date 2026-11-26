# CopyMaster: notes for Claude

Read PLAN.md (design and phases), TODO.md and WORKLOG.md first.

- **Two parts**: `CopyMasterShell.dll` (C++, inside Explorer) only writes a job file and
  starts `CopyMaster.exe` (C#, .NET 10, WinForms), which does all the copying. Keep logic
  out of the DLL: a crash or hang there takes Explorer down.
- **Build everything with `.\build.ps1`** (`-Configuration Release` optional): VS 2026's
  MSBuild on `CopyMaster.slnx`, found through vswhere. `dotnet build` on the solution fails
  with MSB4278 (it can't build the .vcxproj). For C# alone: `dotnet build src/CopyMaster`,
  `dotnet test tests/CopyMaster.Engine.Tests`.
- **Outputs**: the C# projects are Any CPU even under the solution's x64 platform, so
  `src\CopyMaster\bin\Debug\net10.0-windows\CopyMaster.exe`. The DLL goes to
  `src\CopyMasterShell\bin\x64\Debug\CopyMasterShell.dll` and ShellHost to
  `tools\ShellHost\bin\x64\Debug\ShellHost.exe` (set in each vcxproj, the same whether built
  alone or from the solution).
- **The DLL uses the static CRT** (`/MT`, `/MTd`): it depends only on KERNEL32 and the
  `api-ms-win-core-winrt-error` API set (from runtimeobject.lib, for WRL). So **no DllMain
  calling `DisableThreadLibraryCalls`**; Microsoft's docs say not to with the static CRT.
  Check with `dumpbin /dependents` after adding libraries.
- **/W4 with warnings as errors**, except C4324, which fires inside the SDK's `wrl/module.h`.
- **Write `CopyMaster.slnx` by hand.** `dotnet sln add --solution-folder` with two projects at
  once put one under the wrong folder and dropped the other.
- **Tests**: `dotnet test tests/CopyMaster.Engine.Tests` (~4 s). They make scratch folders
  under `%TEMP%\CopyMasterTests`. Timing tests (speed limit, pause, cancel) are in the
  "Timing" collection, which doesn't run in parallel. Test seams on `CopyJob` (internal):
  `ForceCrossVolume`, `BeforeCopy` (throw a disk-full IOException), `BeforeVerify` (corrupt
  the temp file). Use `await task.WaitAsync(...)`, not `task.Wait()`: xUnit1031 warns.
- **Integration tests are opt-in** (IntegrationTests.cs; skipped unless set):
  `CM_TEST_VOLUME` / `CM_TEST_SMALL_VOLUME` from `.\dev\test-volumes.ps1` (VHDXs mounted on
  folders; `-Remove` afterwards), `CM_TEST_SHARE` (a scratch folder on a share, e.g.
  `Z:\_CopyMasterTest`) and `CM_TEST_SHARE_UNC` (the same by `\\server\share`). Writing to the
  desktop share needs Matthew's OK each time. The tool shell won't `Remove-Item` a folder at
  the top of a drive (`Z:\_x`), so use a scratch folder one level down, or leave it for Matthew.
- **The desktop is reachable over SSH** for measuring its side: `ssh -o BatchMode=yes -o
  LogLevel=ERROR Admin@id10terror-desktop "powershell -NoProfile -EncodedCommand <base64>"`.
  The share is `D:\-=Chess Bruteforcer=-` there; its disk counter is `\PhysicalDisk(1 D:)`.
- **The queue and single instance live in the engine** (testable without a window):
  `Scheduling/` (Disks, Scheduler, JobQueue) and `Instance/HandOver.cs` (mutex, pipe server and
  client). `src/CopyMaster` is only the window: built in code, no designer, so WFO1000 is
  suppressed there. WinForms dark mode isn't experimental in .NET 10 (no WFO5001 pragma needed).
- **CopyMaster.exe**: `copy|move --dest <folder> <source>...` or `--job <file>`; a second launch
  hands its job to the first over the pipe and exits. Settings in
  `%LOCALAPPDATA%\CopyMaster\settings.json`, UI exceptions in `errors.log` beside it.
- **Looking at the window without a person**: `dotnet run --file dev\window-shots.cs -- <out> [dark|light]`
  renders every card state and dialog to PNGs, hosted off-screen and never activated (nothing on
  Matthew's screen). `.\dev\window-e2e.ps1` drives the real exe with UI Automation (hand-over, a
  conflict, quitting); its window does show for ~15 s and takes focus, and it puts any real
  settings.json aside and back.
- **A crash in CopyMaster.exe** leaves its stack in the Application event log (provider ".NET
  Runtime"); a WinExe has no console to print it.
- **A running CopyMaster.exe locks its own bin folder.** If Matthew has the window open from
  `src\CopyMaster\bin\Debug`, `build.ps1` retries for 10 s and can't replace its DLLs (MSB3026).
  Check `Get-Process CopyMaster` first, and ask him to close it; never kill it (it may be copying).
- **Never delete a source because its destination compares identical** without
  `Volumes.DifferentFiles`: a hard link, or one share by two names, *is* the destination.
- **cmcopy.exe** (`src/CopyMaster.Cli`) runs the engine from a terminal. Its progress line
  shows only in a real console; check it in the Terminal panel, not through the tool shell.
- **HRESULT masks must be cast to int**: `hr & 0xFFFF0000` widens to long (0xFFFF0000 is a
  uint) and never equals an int. Use `Win32.CodeFromHResult`.
- **Temporary files are `.~cm<8 hex>.tmp`** beside the destination. Short on purpose: a
  suffix on the real name could push a long name past 255 characters.
- **Target net10.0-windows** (the engine P/Invokes Windows-only APIs). A .NET 11 SDK (11.0.100) is installed beside 10.0.404, so `dotnet new`
  picks net11.0 unless told `-f net10.0`.
- **Nothing on this laptop runs un-elevated: UAC is off** (`EnableLUA = 0`). Explorer runs
  elevated too, but its token isn't split (elevation type "default"), so COM honours HKCU
  class registrations here after all (checked in phase 3, WORKLOG). The extension registers
  in HKLM anyway, which works either way.
- **The deployed layout is `%LOCALAPPDATA%\Programs\CopyMaster`**: the exe's build output plus
  CopyMasterShell.dll, which starts the CopyMaster.exe beside it. Registration points there,
  never at a bin folder (Explorer would lock it). `.\dev\reload.ps1` builds Release, deploys,
  registers (`-NoBuild`, `-Restart`, `-Unregister`, `-Configuration Debug`).
- **Restarting Explorer closes all of Matthew's Explorer windows** and blanks his taskbar
  for a moment. `dev\reload.ps1` restarts it only when an Explorer has the DLL loaded (or
  with `-Restart`); say so before any run that may. Test the DLL through `tools\ShellHost`
  instead where possible: `ShellHost menu|invoke --dll <dll> --target <folder|desktop|::{clsid}>
  [--verb CopyMaster.CopyHere] <sources>`, `ShellHost cocreate <clsid>`. Copied beside the DLL
  as CopyMaster.exe, ShellHost catches the job in `received\` instead of copying anything.
- **The shell tests (`ShellExtensionTests`) use the built DLL and ShellHost**, and skip if
  they're missing. `dotnet test` doesn't build C++: run `.\build.ps1` after changing the DLL.
- **regsvr32 is a GUI program**: PowerShell doesn't wait for it or set `$LASTEXITCODE`. Use
  `Start-Process -Wait -PassThru` and its `ExitCode` (reload.ps1 does).
- **PIDLs are `__unaligned` on x64**: `unique_ptr<ITEMIDLIST>` raises C4090 (an error here);
  give the deleter `using pointer = PIDLIST_ABSOLUTE`.
- **The `=` in `-=Programming=-` breaks link.exe's `/LIBPATH`** (LNK1104/LNK1181 on folders
  under it). Link only Windows SDK libraries, or pass full paths to .lib files.
- **VS 2026 Community** (18.12) has the x64 C++ tools and Windows SDK 10.0.26100, but no ATL.
  Use WRL (`wrl/module.h`, `wrl/implements.h`) for COM.
- **Only C: is a local volume** here. Cross-volume tests need a VHDX attached with `diskpart`.
  The desktop's share is mapped as Z:.
- **Matthew's Windows is in dark mode**; the UI should follow it.
- **Pushing is fine without asking** (root CLAUDE.md, 2026-11-03). Never force-push or rewrite
  pushed history.
