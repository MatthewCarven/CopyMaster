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
- **Tests**: `dotnet test tests/CopyMaster.Engine.Tests` (~45 s, ~28 of them the setup tests, which start
  PowerShell 5.1 and 7 a dozen times; `--filter "FullyQualifiedName!~SetupScriptTests"` skips them). They make scratch folders
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
- **Installing (phase 5b): `.\setup\install.ps1`**, `uninstall.ps1`, `check.ps1`, all on `setup\CopyMasterSetup.psm1`
  (which knows every place CopyMaster touches; `dev\reload.ps1` and `dev\package.ps1` use it too). The install folder
  is **`%ProgramFiles%\CopyMaster`**: the exe's build output plus CopyMasterShell.dll, which starts the CopyMaster.exe
  beside it; registration points there, never at a bin folder (Explorer would lock it). Before 5b, `reload.ps1`
  deployed to `%LOCALAPPDATA%\Programs\CopyMaster` (the uninstaller and check still look there). Running the real
  install or uninstall on this laptop, registering or removing the package, or restarting Explorer: **ask Matthew
  first**. The tests run the scripts only with `-FilesOnly -Path <scratch>` and `-WhatIf`, and set
  `CM_TEST_NO_RESTART_DELETE=1` so a locked DLL is never really put in PendingFileRenameOperations. Every installed
  file is named `CopyMaster*`, and the uninstaller deletes only those (a test checks the build output still fits).
  Scripts are written for Windows PowerShell 5.1 too (no `??`, `?:`, `&&`; approved verbs, or 5.1 warns).
- **`.\dev\reload.ps1`** builds Release and deploys over the install (wherever the menus are registered from, else
  `%ProgramFiles%\CopyMaster`), registers (`-NoBuild`, `-Restart`, `-Unregister`, `-Configuration Debug`), and never
  touches the package.
- **Restarting Explorer closes all of Matthew's Explorer windows** and blanks his taskbar
  for a moment. `dev\reload.ps1` restarts it only when an Explorer has the DLL loaded (or
  with `-Restart`); say so before any run that may. Test the DLL through `tools\ShellHost`
  instead where possible: `ShellHost menu|invoke --dll <dll> --target <folder|desktop|::{clsid}>
  [--verb CopyMaster.CopyHere] <sources>`, `ShellHost cocreate <clsid>`. Copied beside the DLL
  as CopyMaster.exe, ShellHost catches the job in `received\` instead of copying anything.
- **Never touch Matthew's clipboard.** The paste handler is tested with `ShellHost isolated invoke
  --paste --dll <dll> --target <folder> [--background] --clip copy|cut|text|empty|hung
  [--verb CopyMaster.Paste] <files>`: `isolated` runs it on a window station of its own (the clipboard
  is per window station) and waits, through a job object, for everything started in there. ShellHost
  refuses `--paste` anywhere else. With `CM_SHELLHOST_ENGINE=<cmcopy.exe>` and `--wait-engine`, the fake
  engine runs the real cmcopy on the job (end to end, clipboard emptying included). The engine's own
  tests use `CopyJob.EmptyClipboard` (a seam), never the real clipboard.
- **Windows 11's compact menu needs a package** (phase 5a). `PasteCommand` (an IExplorerCommand) isn't in
  DllRegisterServer: `package\AppxManifest.xml` registers it, as a sparse package whose external location is
  the install folder. `install.ps1` (or `.\dev\package.ps1` alone) builds it (makeappx from the Windows SDK, from a
  mapping file listing the manifest and logos; `/nv` since the DLL and exe aren't in it) and registers it
  **unsigned** (`-AllowUnsigned`, OID in the Publisher: no certificate). Registering or removing it is a system
  change: ask Matthew first. Removing a package ends its processes, so the scripts refuse while CopyMaster.exe runs
  from the install folder. Developer Mode happens to be on here (Terminal Dev); nothing relies on it. The CLSID is
  in Clsids.h *and* the manifest, the family name's hash in the module (tests check both). Registered since
  2026-11-27 as `CopyMaster_1.0.0.0_x64__gzvqk2jf034f6`, external location `%LOCALAPPDATA%\Programs\CopyMaster`.
  Windows' per-user record of a package's external location: `HKCU\Software\Classes\Local Settings\...\AppModel\
  Repository\Packages\<full name>`, value `PackageRootFolder` (Get-AppxPackage doesn't show it).
- **`-WhatIf` reaches PowerShell 7's Appx compatibility layer**, which builds its proxy module in the global session
  state (seeing the script's `$WhatIfPreference`) and so fails to load under WhatIf. The scripts load the Appx
  cmdlets first with `$WhatIfPreference` off for a moment.
- **The package's Paste runs in a COM surrogate** (`DllHost.exe /Processid:{373E9CED-…}`, inside the package), not
  in Explorer, and **the CopyMaster.exe it starts runs inside the package** (parent dllhost). It shows in the
  classic menu too, so while the package is in, the classic handler's Paste keys are taken out
  (`setup\CopyMasterSetup.psm1`: install, `package.ps1` and `reload.ps1` do it). `ShellHost cocreate <clsid> local`
  activates it out of process without Explorer.
- **Matthew's Explorer has StartAllBack**, which replaces Windows 11's compact menu with the classic one. His
  menus are all classic; the compact menu can only be seen with StartAllBack's classic menu off (a sign-out).
- **Writing HKLM from the tool shell**: opening `HKLM\Software\Classes` itself for writing is refused; open the
  parent of the key you change (`...\shellex\ContextMenuHandlers`) instead. regsvr32 is unaffected.
- **ShellHost drives the command too**: `ShellHost isolated menu|invoke --command --dll <dll> --target <folder>
  [--background [--empty-items]] [--site <folder>] [--site-shellfolder] --clip ...`. The compact menu has no
  "no UI" flag, so every invoke runs a watcher that reads each message box (a `dialog` line) and closes it with
  WM_CLOSE (a WM_COMMAND IDOK, posted or sent, didn't end a box on the private desktop, where it never shows).
- **Icons**: `dotnet run --file dev\make-icons.cs` (from the repo folder) redraws `assets\CopyMaster.ico` and
  `package\Assets\*.png`; the outputs are committed. The DLL embeds the .ico as resource 101 (`"<dll>,-101"`).
- **The shell tests (`ShellExtensionTests`, `.Paste.cs`, `.Command.cs`) use the built DLL and ShellHost**, and skip
  if they're missing. `dotnet test` doesn't build C++: run `.\build.ps1` after changing the DLL. It
  does build cmcopy (a build-only reference) for the end-to-end paste tests.
- **Clipboard reads in the DLL go through `Clipboard.h`**: the menu asks only
  `IsClipboardFormatAvailable`; the click reads on a worker thread for at most 3 s. A clipboard owner
  that never answers WM_RENDERFORMAT would otherwise hang Explorer (`--clip hung` tests it).
- **The tool shell refuses `Remove-Item` with a wildcard or several paths** ("system path ... is
  blocked"), even in the scratchpad. Remove one `-LiteralPath` at a time.
- **regsvr32 is a GUI program**: PowerShell doesn't wait for it or set `$LASTEXITCODE`. Use
  `Start-Process -Wait -PassThru` and its `ExitCode` (`Invoke-Regsvr32` in CopyMasterSetup.psm1 does).
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
  pushed history. The repo is `master` on `origin`, https://github.com/MatthewCarven/CopyMaster
  (since 2026-11-26). Commit messages end with a `Co-Authored-By: Claude` trailer; README.md
  credits Matthew and Claude (his ask).
- **Commit messages from the PowerShell tool**: `git commit -F -` with a here-string as an argument
  doesn't feed stdin (git takes the text as a pathspec). Use `-m @'...'@` or `-F <file>`.
