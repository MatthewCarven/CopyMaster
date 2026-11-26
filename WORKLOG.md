# Worklog

## 2026-11-26: plan

**Plan.** Matthew asked for an Explorer extension that copies and moves files through
CopyMaster from the right-drag menu and from right-click Paste. PLAN.md has the design:
a tiny C++ shell extension inside Explorer that only writes a job file and starts
CopyMaster.exe, a C# WinForms engine that does the copying; six phases (0–5).

**Matthew's decisions:** C# + WinForms for the engine; the classic menu first (Paste under
"Show more options"), Windows 11's compact menu in phase 5; queue per drive; v1 gets a speed
limit (1–128 MB/s, then Unlimited), verify after copy, pause/resume, a conflict dialog, and
skip-errors-retry-later.

**Environment, checked:** the folder was empty and isn't a git repo (flagged in TODO.md for
Matthew). VS 2026 Community 18.12 with the x64 C++ tools and Windows SDK 10.0.26100; no ATL
(WRL instead). .NET SDKs 10.0.404 and 11.0.100. Only C: is local (48.7 GB free); the desktop
share is Z:. No code yet.

## 2026-11-26: phase 0 (skeleton)

**Built:** `CopyMaster.slnx` (written by hand: `dotnet sln add --solution-folder` mangled
it) with four projects. `CopyMaster.Engine` (net10.0) holds only a `CopyOperation` enum so
far. `CopyMaster` (WinForms, net10.0-windows) is an empty "CopyMaster" window. `CopyMasterShell`
(C++ DLL, v145, x64, static CRT, /W4 warnings-as-errors) is a WRL in-proc COM module that
exports `DllGetClassObject`, `DllCanUnloadNow`, `DllRegisterServer` and `DllUnregisterServer`
but serves no classes yet (registration returns E_NOTIMPL until phase 3). The tests project
has one smoke test. `build.ps1` finds VS's MSBuild with vswhere and builds the lot.

**Checked:** Debug and Release build with no warnings. `dotnet test`: 1 passed.
`dumpbin`: the four exports are there, and the DLL depends only on KERNEL32 and the
`api-ms-win-core-winrt-error` API set, so no VC++ runtime is needed. The two registration
stubs share one address (the linker folded the identical bodies), which ends once they
differ in phase 3. CopyMaster.exe opens its window and closes with exit code 0.

**Found:** `dotnet build CopyMaster.slnx` fails with MSB4278 on the vcxproj, so the solution
needs VS's MSBuild. WRL's `module.h` raises C4324 at /W4; disabled for the project. The C#
projects build Any CPU even under the solution's x64 platform.

**Next:** phase 1, the engine core and command line (TODO.md). Not committed: the folder
still isn't a git repo.

## 2026-11-26: phase 1a (engine core, cmcopy)

Phase 1 split in two: 1a here (everything testable on C:), 1b later (a VHDX for real
cross-volume moves and a full disk, the desktop share for SMB).

**Measured first:** `CopyFile2` with a progress callback copies in 1 MiB chunks (64 MiB and
1 GiB files) and 512 KiB chunks (a 6 MiB file) on C:. At a 1 MB/s limit, enforced by
sleeping in the chunk callback, chunks landed every 0.50 s and 6 MiB took 6.0 s. Smooth
enough, so `CopyFile2` it is; no copy loop of our own.

**Built (src/CopyMaster.Engine):**
- `Native/`: CopyFile2, MoveFileEx, volume names, file IDs, SHChangeNotify (LibraryImport);
  `\\?\` paths for long names; Win32 error helpers.
- `Control/`: `RateLimiter` (bytes/s, live, half a second of burst after idling, waits in
  100 ms slices so a raised limit bites at once), `JobControl` (pause, resume, cancel, limit).
- `Copying/`: `FileCopier` (CopyFile2; cancel makes it delete the partial file),
  `Verifier` (byte-for-byte, the copy read unbuffered with sector-aligned buffers).
- `Planning/`: `JobPlanner` lists sources into ordered items: make folder, copy file, rename.
- `Jobs/`: `CopyJob` runs the plan: conflicts (policy or `IJobPrompts`), temp name → verify →
  rename into place, created dates, folder dates and attributes at the end, moves, empty
  source folders removed, failures recorded and retryable, disk-full prompt. `JobFile` is
  the DLL's hand-over format. `Units` formats sizes as Explorer does.
- `src/CopyMaster.Cli` → `cmcopy.exe`, a console front end. Not part of CopyMaster.exe because
  a windowed exe can't be waited on or print to a terminal.

**Decisions on the way:** verify compares bytes directly instead of hashing (both files are
read either way); the created date is kept too (CopyFile2 keeps only the modified date);
copies go through a temporary name so a file appears only complete and verified; a link to
a folder at the top of a copy is followed, links further in are skipped (no loops), and a
link is never followed for a cross-volume move (it would delete what it points at).

**Tests:** 59, all passing, stable over 4 runs (~3–4 s). They found one real bug:
`CodeFromHResult` masked with a bare `0xFFFF0000`, a uint, so the comparison widened to long
and never matched. Disk-full went unrecognised and error messages fell back to hex.

**By hand:** cmcopy's help, bad arguments (exit 2), a verified copy at 16 MB/s (20 MB in
2.5 s: the limit covers the verify read too), a re-run skipping conflicts, a move by
rename, a job file with a missing source (exit 1), hashes equal afterwards. In the Terminal
panel the progress line drew in place ("Verifying 0/1  33 MB of 48 MB  3.99 MB/s  demo.bin")
and 24 MB verified at 4 MB/s took 12.0 s.

**Not done here (1b):** real cross-volume moves, a real full disk, anything over SMB.

## 2026-11-26: phase 1b (real volumes, a full disk, the desktop share)

Matthew OK'd a temporary VHDX and a scratch folder on the desktop share (Z:).

**Test volumes:** `dev\test-volumes.ps1` makes a 256 MB and a 32 MB VHDX with `diskpart`,
mounted on empty folders under `%TEMP%\CopyMasterTestVolumes` rather than drive letters (so
Explorer shows nothing). `-Remove` detaches and deletes them. The mount folders show up as
junctions to `Volume{...}`, so the planner would skip a mounted volume inside a copied tree as
a link: the safe choice.

**Integration tests (10, opt-in through environment variables, skipped otherwise):** moves to
and from another volume (one copy per file, dates and `Zone.Identifier` kept, verify on the
way back); a full disk three ways (a too-big file fails alone and gives its space back; with
no prompts the job stops and keeps what it copied, byte-identical; freeing room then Retry
finishes); on the share, a verified copy, moves both ways, chunk timing, one share by two
names. All 69 tests pass with every place set; 59 pass and 10 skip with none.

**Measured:** `CopyFile2` reserves a file's whole size first, so a too-big file failed in
~50 ms instead of partway. Over SMB it copies in 1 MiB chunks; unlimited, 64 MB took 5.9 s
(10.9 MB/s, the laptop's Wi-Fi); at 2 MB/s a chunk landed every 0.50 s. 20 MB copied and
verified to the share: 4.8 s.

**One share by two names:** a move from `Z:\...` to `\\id10terror-desktop\chessbruteforcer\...`
was planned as cross-volume and copied (8 MB in 0.16 s; the server did the copying, faster
than Wi-Fi). `Volumes.Key` now maps a mapped drive to its share (`WNetGetConnection`); the
same move became a rename, 0.03 s, nothing copied, and MoveFileEx accepted the two spellings.

**Does verify over SMB reach the disk? Yes.** Watching the desktop's `PhysicalDisk(1 D:)`
counters over SSH while the laptop read files unbuffered: a 128 MB file written a moment
before (still in the desktop's cache) made D: read 127.2 MB, after writing out 48 MB of its
cached writes; an old 102 MB table read 100.6 MB, and again 100.4 MB on a second read. A
background research agent found the documented reason: [MS-SMB2] 3.3.5.9 opens the file
with the client's CreateOptions (FILE_NO_INTERMEDIATE_BUFFERING included), and [MS-FSA]
2.1.5.3 has such a read flush cached data, then read from disk. (Not found in the docs: that
FILE_FLAG_NO_BUFFERING maps to that bit; the measurement covers it.)

**Cleanup:** VHDXs detached and deleted, test folders gone. The empty `Z:\_CopyMasterTest` is
left for Matthew: the tool shell refuses Remove-Item on a folder at the top of a drive.

## 2026-11-26: phase 2 (the window, the queue, single instance)

Done in one session; nothing left for a 2b except Matthew's run.

**Engine side (testable, no UI):**
- `Scheduling/Disks`: a job's resources. `disk:N` per physical disk under each volume
  (`IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` on `\?\Volume{...}`, opened with no access rights),
  `server:NAME` for `\server\...` and `\?\UNC\...` (parsed, no network) and for mapped drives
  (`WNetGetConnection`), `volume:...` if Windows won't say. A move within one volume is only
  renames, so it needs no disk and never queues.
- `Scheduling/Scheduler<T>`: the rule. Each disk is claimed by the job running on it, else by the
  first job queued for it; a waiting job starts only if none of its disks is claimed, and claims
  them whether it starts or not, so it's only ever overtaken by jobs sharing no disk with it.
  Held jobs neither start nor claim; Start now ignores claims. Each waiting job records who it
  waits for (the card says "After: ...").
- `Scheduling/JobQueue`: runs jobs on threads of their own as the scheduler allows; pause (holds a
  waiting job, pauses a running one, which keeps its disks), resume, start now, cancel, remove,
  move up/down, retry failed (requeued in place), clear finished. Latest progress kept for the
  window to poll.
- `Instance/HandOver`: `SingleInstance` (a `Local\` mutex; whoever creates it is the instance),
  `HandOverServer`/`HandOverClient` on a `CurrentUserOnly` pipe named by user SID and session.
  Length-prefixed UTF-8; the job travels in `JobFile` format, or "show". The client waits for a
  pipe that's still starting, and calls `AllowSetForegroundWindow` for the server so it can come
  to the front. Once a request is sent, a lost reply is reported, not retried (it might queue twice).
- `AppSettings` (`%LOCALAPPDATA%\CopyMaster\settings.json`, speed in MB/s; broken → defaults:
  unlimited, verify on, ask), `SpeedStops`, `Units.Duration`/`TimeLeft` (rounded so it doesn't flicker).
- `CopyJob` asks `IJobPrompts.StartWithoutRoom` (a default interface method, true) after planning
  when the files to copy exceed the destination's free space; no stops it with a reason.

**Window (src/CopyMaster, built in code, no designer):** a card per job (title, status, current file
or summary, our own progress bar, bytes · speed · time left · items, the speed slider, and the
buttons for its state); overall % in the title. Conflict, disk-full and not-enough-room dialogs are
modeless and owned by the window: the job's thread blocks on a TaskCompletionSource, cancelling the
job closes its dialog, other jobs and the window carry on. Details window with Copy list and Retry
failed. Settings dialog. Quitting with unfinished jobs asks, cancels, and waits for them to stop
before closing (no temp files left). `CopyMaster.exe copy|move --dest <folder> <source>...` and
`--job <file>`; a job file in `%LOCALAPPDATA%\CopyMaster\jobs` is deleted once queued. UI exceptions
go to `errors.log` with one dialog, so a window bug doesn't take running copies down.

**Found:** WinForms dark mode isn't experimental any more in .NET 10 (no WFO5001; the pragmas went).
WFO1000 (designer serialization) fires on every public property of a code-built control under
warnings-as-errors; suppressed for the project. The first real run crashed on close: the pipe server
was disposed twice (once when quitting starts, once by `using`), and the second `Cancel` hit a
disposed CTS. Found from the .NET Runtime event in the Application log; `Dispose` is idempotent now,
with a test.

**Checked:** `.\build.ps1` clean. 122 tests pass, 11 opt-in skipped, three runs; with the VHDXs from
`dev\test-volumes.ps1`, 129 pass (the VHDX has its own disk key), VHDXs removed after.
`dev\window-shots.cs` rendered every card state and dialog, dark and light (hosted off-screen, never
activated; PrintWindow gave black patches off-screen, DrawToBitmap didn't). `dev\window-e2e.ps1`
drove the real exe with UI Automation, 11 checks: a second launch hands over in ~340 ms and exits 0,
a `--job` file is taken and deleted, one process, copies hash-match, a conflict answered Overwrite
in its dialog, quitting mid-copy asks, exits 0 and leaves no temp file.

**Not done:** Matthew's run (TODO). Nothing touched Z:. Not committed: the folder still isn't a git repo.

## 2026-11-26: identical files aren't conflicts (Matthew's idea)

Matthew: if the files compare the same, there's no need to copy or move, or to ask about
deleting the source. Done in the engine (`CopyJob`), with the window and cmcopy showing it.

**How:** when a file is already at the destination, not the same file, and the same size,
`Identical` compares the two byte for byte with the verifier (destination read unbuffered, from
its disk). Identical: nothing written, no conflict prompt, whatever the policy ("keep both" makes
no duplicate), counted in the new `JobResult.Identical` ("N already there"). For a move, the
source is deleted, but only after `Volumes.DifferentFiles` proves the two are different files;
if either can't be identified, the source stays (skipped, with a note). A delete that fails
retries as a delete only and keeps the counters right (`FailedItem.Identical`). Different sizes
cost nothing; an unreadable destination counts as different. New `JobPhase.Comparing`; the bar's
total grows by the bytes compared, so it still ends at 100%.

**Why two guards:** a hard link (or one share by two names) compares identical because it *is*
the destination, and deleting the "source" would delete the only copy. `SameFile` catches it
first; `DifferentFiles` is the second check right before the delete. Switching off `SameFile`
alone left the hard-link tests passing; switching off both made them fail (both reverted).

**Tests:** 10 new (`IdenticalTests`), 132 pass, 11 opt-in skipped.

**Note:** Matthew had CopyMaster.exe open from `src\CopyMaster\bin\Debug` (since 20:17), so
`build.ps1` couldn't replace the window's engine DLL (MSB3026, locked); cmcopy and the tests got
the new engine. Rebuild once the window is closed.

**Matthew's first run of the window (same evening):** after the rebuild he copied `C:\Windows\Web`
to Temp again; the card read "Nothing copied, 8 already there in 0 s", green and Done, in dark mode.
So phase 2's window has had a real copy from him; a real move is still to come.

## 2026-11-26: a speed limit of its own for network shares

Parent session's question, answered yes: copies to Z: should respect the household's ~8 MB/s
cap without anyone touching the slider.

**How:** `AppSettings.NetworkSpeedLimit` (default 8 MB/s, `NetworkSpeedLimitMBps` in
settings.json; `null` there means Unlimited, and a file from before the setting gets 8). A job
uses the network if any of its scheduler keys is `server:` (`Disks.UsesNetwork`): a UNC path or
a mapped drive, as source or destination. Such a job starts at the lower of the two limits
(`ToJobOptions(network)`); its card's slider shows that and can still be moved. `JobQueue.Add`
gained an overload that picks the options once the resources are known, so the disks are looked
up once. A remote drive letter that `WNetGetConnection` can't name now keys as `server:Z:`
rather than `volume:`, so it still counts as the network. A move within one share is renames
(no keys), so it isn't limited, and needs no limit.

**Settings dialog:** a second slider, "Speed limit on network shares", with a line explaining
the lower-of-the-two rule. Rendered with `dev\window-shots.cs` (dark): fits, nothing clipped.

**Tests:** 8 new (settings round trip, an old file, unlimited kept, the lower-limit rule, which
jobs use the network, a queued share job's starting limit). `.\build.ps1` clean; 140 pass,
11 opt-in skipped.
His first move try reported "It isn't there any more" for `Temp\Web-spare\Web`, correctly: my prep
command (`Copy-Item C:\Windows\Web $env:TEMP\Web-spare -Recurse`) made Web-spare itself the copy
(Screen, Wallpaper inside, no Web folder), because Copy-Item to a missing folder names the copy after
it. The move-onto-identical run is still to do. Also from the phase 2 session: a separate speed limit
for network shares (8 MB/s default, in Settings); 140 tests pass; not yet tried with a real copy to Z:.
Then the move, from the existing Web-spare: "Move 2 items to ...\Temp\Web: Nothing moved, 8 already
there in 0 s", no question. Checked after: Web-spare empty (its emptied folders removed), Temp\Web's 8
files still hash-match C:\Windows\Web. **Phase 2 done**: Matthew has run a real copy and a real move
through the window. Next: phase 3 (the shell DLL, right-drag).

## 2026-11-26: phase 3 (the shell DLL, right-drag): done

A side session of the phase 2 one. Built in one go, so no 3a/3b split; only Matthew's right-drags
are left.

**Built (src/CopyMasterShell, WRL):**
- `DragDropHandler` (`IShellExtInit` + `IContextMenu`; CLSID `{EA68F6FA-3C02-46D3-917E-FEE4FF327E27}`
  in `Clsids.h`, shared with ShellHost). Initialize keeps the target's file-system path and the
  CF_HDROP paths, nothing else. QueryContextMenu adds **CopyMaster Copy here** and **CopyMaster Move
  here** only when it has both (and never for CMF_DEFAULTONLY). InvokeCommand takes a menu offset, or
  the verbs `CopyMaster.CopyHere` / `CopyMaster.MoveHere` (Unicode or ANSI). A target counts when its
  shell item is a folder, isn't a stream (a .zip shows as a folder) and has a SIGDN_FILESYSPATH. That's
  read from what the shell already knows: no disk access, nothing to hang on.
- `JobLauncher`: looks for CopyMaster.exe beside the DLL first (a broken install leaves no job file),
  writes `%LOCALAPPDATA%\CopyMaster\jobs\<GUID>.cmjob` (UTF-8, LF, no BOM, CREATE_NEW), then starts
  `"<dll folder>\CopyMaster.exe" --job "<file>"` with CreateProcess, in its own folder, and doesn't
  wait. A name that isn't valid UTF-16 (a lone surrogate, which NTFS allows) refuses the job: UTF-8
  can't carry it. On failure: a message box (unless CMIC_MASK_FLAG_NO_UI), and no job file left.
- `Registration`: HKLM `Software\Classes\CLSID\{...}` (InprocServer32, Apartment),
  `shellex\DragDropHandlers\CopyMaster` under Directory, Drive and Folder, and Shell Extensions\Approved.
  All or nothing, then SHCNE_ASSOCCHANGED so Explorer drops cached lookups.
- Every COM method catches; nothing throws past the boundary.

**Deployed layout:** `%LOCALAPPDATA%\Programs\CopyMaster` (beside VS Code and Ollama) holds the exe's
build output plus the DLL. `dev\reload.ps1` builds Release, copies, registers with regsvr32, and
restarts Explorer only if an Explorer has the DLL loaded. A loaded DLL can be renamed though not
overwritten, so the old one is moved aside as `*.old` (deleted on a later run) and the copy never
races Explorer. Release, because a Debug CRT assertion would put a dialog inside Explorer.

**The Desktop:** the target Explorer's desktop window has is the namespace root (an empty PIDL). Its
shell item has SFGAO_FILESYSTEM | FOLDER | FILESYSANCESTOR and SIGDN_FILESYSPATH
`C:\Users\Admin\Desktop`, so the handler takes it. Which key Explorer looks up the root's handlers under
is beyond ShellHost; `Folder` covers it (Windows' own zip right-drag handlers live there, and 7-Zip's,
under Directory and Drive only, may not show on the Desktop). Explorer should load one CLSID once
however many keys name it: Matthew's run will show one pair of items, or two.

**HKCU isn't ignored on this laptop.** With UAC off, this shell's token and Explorer's are both
elevation type "default" (no split token), High integrity, elevated. A CLSID registered only in HKCU:
CoCreateInstance from ShellHost succeeded (0x0); registered nowhere, 0x80040154; HKLM only, 0x0. So
COM's "elevated processes ignore per-user classes" rule doesn't apply to a default-type token.
HKLM anyway: it works with UAC on or off, elevated or not, and suits a machine-wide installer. (Not
tried in Explorer itself; the shellex keys come through the merged HKCR view either way.)

**tools\ShellHost** (C++ console, static CRT, in the solution): loads the DLL without the registry,
gives it a CF_HDROP data object (or one without file paths, or the shell's own via BHID_DataObject),
runs Initialize, QueryContextMenu and InvokeCommand, and prints what it saw. `cocreate <clsid>` checks
registrations. Copied as CopyMaster.exe beside the DLL and started with `--job`, it moves the job file
into `received\` with its command line.

**Tests:** `ShellExtensionTests`, 15, skipped until `.\build.ps1` has built the DLL and ShellHost. Job
files round-trip through `JobFile.Read` with `=`, spaces, punctuation, accents, CJK, a folder and a
300+ character path; copy and move by menu offset, Unicode verb, ANSI verb and the shell's own data
object; the desktop root gives the Desktop folder; a drive root takes the items; This PC, the Recycle
Bin, Control Panel, a library, a .zip and a drag without file paths get none; no CopyMaster.exe gives
0x80070002 and no job file. The scratch app folder has `=`, spaces and a comma, so the command line's
quoting is tested too. 155 pass, 11 opt-in skipped; the shell tests also pass on the Release build.

**Checked:** `.\build.ps1` clean in Debug and Release, no warnings. `dumpbin /dependents`: KERNEL32,
USER32, ADVAPI32, SHELL32, ole32 and api-ms-win-core-winrt-error only, so no VC++ runtime. The Release
DLL is 194 KB. Deployed and registered for real at 21:05; Explorer wasn't restarted (it had never
loaded the DLL), and COM creates the class from HKLM.

**Found:** regsvr32 is a GUI program, so PowerShell neither waits for it nor sets `$LASTEXITCODE`: the
first reload "failed" with an empty exit code. `Start-Process -Wait -PassThru` reads it. PIDLs are
`__unaligned` pointers on x64, so `unique_ptr<ITEMIDLIST>` raises C4090 (an error under /WX); a deleter
with `using pointer = PIDLIST_ABSOLUTE` fixes it.

**Matthew's run (about 23:05): it worked.** Checked afterwards:
- **No Explorer restart was needed.** Explorer has run since 2026-11-25 19:41 and now has
  `%LOCALAPPDATA%\Programs\CopyMaster\CopyMasterShell.dll` loaded: a first registration is picked up live.
- **The DLL started the deployed exe**: `CopyMaster.exe` from the deploy folder has run since 23:06:52
  (the `bin\Debug` window had been closed, so it became the first instance).
- The jobs folder is empty (every job file read and removed), there's no `errors.log`, and the
  Application log has no .NET Runtime or Application Error events since 21:00.
- Not noted: whether the items showed once or twice, and where in the menu they sat.

**His note: a copy onto the Desktop "started copying over itself".** He right-dragged a file and a
folder on the Desktop onto the Desktop and chose Copy here. The engine did what it was built to do with
a same-folder copy: `Gotye … (2).mp3` and `New folder (5) (2)` beside the originals (the folder was
cancelled after ~10 s with 233 of its 281 files; no temporary files left). Nothing was overwritten. But
the card said "Copy “…mp3” to C:\Users\Admin\Desktop", which reads as a copy onto itself, and a
right-drop on the Desktop is mostly an icon being moved. Offered: hide the items, name the copy
Windows' way ("X - Copy") and show it on the card, or both. **His call: do nothing at all.**
- Engine (`JobPlanner`): an item whose folder is the destination is a note, "Already in that folder",
  for a copy as for a move (moves always were). `UniqueNames` stays for "keep both" on conflicts.
- DLL: `Initialize` notes whether every dragged item sits directly in the target folder (strings only,
  without case, trailing backslashes ignored so `C:\x` is in `C:\`); `QueryContextMenu` then adds
  nothing. A verb still reaches the engine, which notes it. A mixed drag (some items from elsewhere)
  keeps the items, and the engine copies only the others.
- Tests: `A_copy_into_its_own_folder_does_nothing` (replaces `…keeps_both`),
  `A_copy_from_two_folders_skips_only_what_is_already_there`; ShellHost: a drop into the items' own
  folder (CF_HDROP, the shell's own data object, upper-cased paths), the top of a drive, and a mixed
  drag that still gets the items. 161 pass, 11 opt-in skipped; Debug and Release build clean; the DLL's
  dependencies are unchanged.
- The empty window's text no longer says "once the shell extension is in".
- **Deployed at 23:18**, once Matthew had closed the window and said to: `reload.ps1` built Release,
  set the loaded DLL aside as `.old`, registered, and restarted Explorer (back as pid 23092). The
  deployed DLL, engine and window match the build byte for byte. ShellHost on the deployed DLL with the
  shell's own data object: a drop into the items' folder gets no items, another folder gets both.
  **Matthew checked in Explorer: a Desktop-onto-Desktop right-drag no longer shows the CopyMaster
  items.** The restarted Explorer (23:18:12) has the new `CopyMasterShell.dll` loaded.

The two leftovers stay on the Desktop for Matthew. Not committed: the folder still isn't a git repo.

## 2026-11-26: git

Matthew ran `git init` (branch `master`). The first commit (`2c47e86`) holds everything through phase 3:
76 files, source and docs only (the `.gitignore` from phase 0 keeps out `bin/`, `obj/` and `.vs/`).
He then gave the remote: `origin` is https://github.com/MatthewCarven/CopyMaster, which was empty;
`master` pushed and tracking `origin/master`. From here on, work is committed and pushed as it's done
(root CLAUDE.md). At his asking ("put your name on it too"), a README crediting Matthew and Claude; the
commits already carry a `Co-Authored-By: Claude` trailer.

## 2026-11-27: Matthew's screenshot

The open phase 3 question is answered: right-dragging onto a folder on the Desktop, the menu reads 7-Zip,
**CopyMaster Copy here**, **CopyMaster Move here**, then Windows' Copy here, Move here (the default),
Create shortcuts here. So the items show once, though `Directory`, `Drive` and `Folder` all name the CLSID:
Explorer loads a handler once however many keys list it. Nothing to change.

The same screenshot shows a real queue: Copy "Westworld" (18.9 GB, 134 items) into
`Desktop\-=Programming=-` at 304 MB/s with verify on (18%, about 2 min left), and Copy "Music" to the
same place waiting for its disk ("After: Copy 'Westworld'…"), as the queue per disk should. The title
reads "18% · CopyMaster"; Task Manager shows C: at 100%.

## 2026-11-27: phase 4 (right-click Paste): built

A side session. Built in one go; only Matthew's pastes in Explorer are left (and the deploy, which restarts
Explorer because it has phase 3's DLL loaded).

**Built (src/CopyMasterShell):**
- `PasteHandler` (`IShellExtInit` + `IContextMenu`, CLSID `{355681EC-9621-450F-8449-6B5769A1149B}`):
  **CopyMaster Paste**, verb `CopyMaster.Paste`. Initialize takes the folder (a background: Initialize gets
  no selection) or the selection (a right-click on a folder or drive: exactly one item, a file-system folder,
  from the shell's IDList array, so nothing is parsed and nothing waits on a network). QueryContextMenu adds
  the item only when `IsClipboardFormatAvailable(CF_HDROP)`: the list of formats, never the data.
- `Clipboard.*`: the click reads `CF_HDROP` and `Preferred DropEffect` with the plain Win32 clipboard on a
  thread of its own (`CreateThread`, holding a reference to the DLL, ending in `FreeLibraryAndExitThread`),
  and waits at most 3 s. The UI thread meanwhile runs only sent messages (`MsgWaitForMultipleObjectsEx` with
  `QS_SENDMESSAGE`, then `PeekMessage(PM_QS_SENDMESSAGE)`): the clipboard's owner may be OLE's window on that
  same thread, and has to answer WM_RENDERFORMAT. OLE's own `OleGetClipboard` wasn't used there: a call into
  another apartment comes as a posted message, which that wait wouldn't run. Cut = `DROPEFFECT_MOVE` without
  `DROPEFFECT_COPY`. The sequence number is read before opening and after closing, and kept only if it held
  still (up to three reads), so it names exactly what was read.
- `JobLauncher` writes `clipseq=<n>` for cuts. `ContextMenu.*` (the item table, AddMenuItems, FindMenuItem,
  MenuItemString) and `Folders.*` (FileSystemFolder, SelectedFolder, DropFiles) came out of
  `DragDropHandler.cpp` for both handlers to share; `SystemMessage` moved to `Module`.
- `Registration` now registers both classes: the paste handler under `Directory\Background`, `Directory` and
  `Drive\shellex\ContextMenuHandlers\CopyMaster`, plus Approved. `Directory\Background` serves the Desktop's
  background: Windows' own "New" and NVIDIA's desktop items are registered there on this laptop, and
  `DesktopBackground\shellex\ContextMenuHandlers` holds only the slideshow item.

**Engine:** `CutPaste.Completes(spec, result)`: a move with a clipboard number, no failures, and something moved
or already there (so a cut pasted into its own folder stays on the clipboard). `CutPaste.EmptyClipboardIfUnchanged`
opens the clipboard, compares the number while it's open, empties it; on its own thread, for at most 2 s
(emptying sends WM_DESTROYCLIPBOARD to the old owner). `CopyJob` calls it at the end of `Run` and `RetryFailed`
(seam: `EmptyClipboard`), so CopyMaster.exe and cmcopy both finish a cut; `JobResult.Clipboard` says what
happened. The source isn't sent `CFSTR_PASTESUCCEEDED`: the files have moved already. The window's empty text
mentions Paste.

**No test touches the real clipboard.** It belongs to the window station, so `ShellHost isolated <command>`
makes a window station (`CopyMasterTest.<pid>`) and a desktop, runs `ShellHost <command>` there, and waits for
everything started inside (a job object; at first it waited only for its child, the window station went with
it, and the fake engine the DLL had just started never ran). ShellHost refuses `--paste` elsewhere, and loads
the handler before it touches any clipboard, so its guard test can't clobber Matthew's even if the guard broke.
`--clip copy|cut` puts files there through OLE as Explorer does (its own data object, or with `--shell-data` the
shell's); `text`, `empty`; `hung` has a window on another thread promise CF_HDROP and never read its messages.
`--clip-before-invoke` / `--clip-after-invoke` change the clipboard around the click. With
`CM_SHELLHOST_ENGINE=<cmcopy.exe>`, the fake engine waits for `<job>.go`, runs the real cmcopy on the job, and
writes its exit code to `<job>.engine`; `--wait-engine` drives that and prints the clipboard afterwards.

**Measured on the private window station:**
- Producing delayed formats didn't move the sequence number: OLE's data object, or the shell's, read through
  WM_RENDERFORMAT, left it as it was (5 before and after; 10 for the shell's). The read-until-still loop
  settles on its first pass.
- A hung owner: the menu still came in about 50 ms (the whole ShellHost run), and the click gave up at
  3000 ms with 0x800705B4 (ERROR_TIMEOUT).
- End to end: a cut through the DLL and the real cmcopy moved the files and left the clipboard empty (5 → 6);
  with text copied after the click, the files moved and the text stayed (10, untouched).

**Tests:** 37 new (198 pass, 11 opt-in skipped). `CutPasteTests` (10, the seam): a cut that moves everything
empties once with its number; a copy, a move with no number, a failure, a cancel, a missing source and a cut
into its own folder don't; a retry that finishes does; files already there count. `ShellExtensionTests.Paste.cs`
(27 with theory cases): a cut on a background is a move with `clipseq` equal to the clipboard's number (unchanged
by the read); a copy on a folder (the shell's data object) has none; every way of invoking; the Desktop's
background gives the Desktop folder; a drive takes it; This PC, the Recycle Bin, Control Panel, a library and a
.zip get none, as item or background; two selected folders get none; text or nothing on the clipboard gets
none; the hung owner (item shown, click gives up at 3 s); the clipboard losing its files before the click
(0x80040064, no job); no CopyMaster.exe; the guard; and three end to end through cmcopy (cut empties, a changed
clipboard is left alone, a copy leaves it as it was). The tests project builds cmcopy (a build-only reference).

**Checked:** `.\build.ps1` clean in Debug and Release; the 47 shell tests pass on Release too. `dumpbin
/dependents`: KERNEL32, USER32, ADVAPI32, SHELL32, ole32 and api-ms-win-core-winrt-error, as before. The
Release DLL is 211 KB (was 194). The phase 3 tests pass unchanged after the split into ContextMenu and Folders.

**Found:** the Bash tool's heredoc ate backslashes again (`"\\n"` in a Python patch, `.\dev\reload` became
`.\dev` + CR + `eload`): patches with backslashes go through a file written by the Write tool. The tool shell
refuses `Remove-Item` with a wildcard or a list of paths ("system path ... is blocked"), even in the scratchpad.
A `cd` inside a Bash call moved the session's working directory once more.

**Deployed at 01:44** (2026-11-27), at Matthew's word: `reload.ps1` built Release, set the loaded DLL aside as
`.old`, registered, and restarted Explorer (back as pid 6732). The deployed DLL, engine and window match the
build byte for byte; HKLM has the three paste keys and the drag-drop ones; `ShellHost cocreate` creates both
classes from the registry; on a private window station the deployed DLL offers CopyMaster Paste on the Desktop's
background (the Desktop folder) for files, and nothing for text. The new Explorer loads the DLL on its first
right-click. Left: Matthew's pastes in Explorer.

**Matthew's first paste (01:46): it worked.** He copied the Desktop's Music folder (Explorer's classic menu,
Copy), right-clicked the background of `Desktop\-=Programming=-`, and the classic menu showed **CopyMaster
Paste** between "Open Git Bash here" and "Give access to". It started the deployed CopyMaster.exe (pid 18920,
01:46:43, the same second `-=Programming=-\Music` was made): Copy "Music", 24.5 GB, 3,093 items, verifying,
863 MB/s. Explorer (pid 6732) has the new DLL loaded; the jobs folder is empty, there's no `errors.log`, and the
Application log has no crash. C: had 44.2 GB free mid-copy. Still to try: a cut (a move, then Explorer's Paste
greyed), a right-click on a folder itself, and the Desktop's background.

**Then the rest (01:52): "Done, all worked."** Matthew tried a cut, a right-click on a folder itself and
the Desktop's background. Afterwards: no `errors.log`, the jobs folder empty, no crash events in the
Application log. **Phase 4 is done.** Next is phase 5 (Windows 11's compact menu, an installer, icons), and
the open ideas in TODO.md, such as whether a copy pasted into its own folder should make "X - Copy".

## 2026-11-27: phase 5a (Windows 11's compact menu, icons): built

A side session. Phase 5 split in two: **5a**, the compact menu and the icons (this), and **5b**, the
installer and uninstaller. 5a is built and tested; the package isn't registered yet and nothing is deployed
(both wait for Matthew's yes: the package goes in for all users, and the deploy restarts Explorer).

**Found out first:**
- **Developer Mode is already on** here (`AllowDevelopmentWithoutDevLicense` = 1; Matthew's Terminal Dev is a
  development registration). Not relied on.
- **Windows 11 installs an unsigned package** (`Add-AppxPackage -AllowUnsigned`) if its Publisher carries
  `OID.2.25.311729368913984317654407730594956997722=1`; one with code in it needs an elevated shell and is
  installed for all users. So no certificate is made or trusted, and there's no certificate for the
  uninstaller to remove. Microsoft calls it a testing feature ("don't distribute widely"); for this laptop
  it's the simplest thing. If `-AllowUnsigned` turns out not to mix with `-ExternalLocation`, the fallback is
  a self-signed certificate in `LocalMachine\TrustedPeople` (its private key deleted after signing), which
  needs Matthew's yes of its own.
- **Three sparse packages with compact-menu commands are installed here** (Notepad++, VS Code, TreeSize), all
  `mediumIL`/`win32App`, `runFullTrust` + `unvirtualizedResources`, `com:SurrogateServer` with
  `ThreadingModel="STA"`. TreeSize registers `Drive` through `desktop10:ItemType`; makeappx's schema check
  accepts that (and rejects a misspelt `desktop10` element, so the check is real).
- **In process or in a surrogate?** Unsettled. Microsoft's pages don't say. PowerToys' developer docs say
  Windows 11 handlers run in DllHost ("attach to DllHost.exe when the context menu is triggered"); Notepad++'s
  crash reports name `NppShell.dll` faulting in explorer.exe (maybe its classic registration). None of the
  three DLLs was loaded in Explorer or any dllhost when looked at (they load when the menu opens). To check
  once ours is registered.
- **With both kinds registered, "Show more options" shows duplicates** (PowerToys' docs, for its modules that
  register both). To check with ours.

**Built (src/CopyMasterShell):**
- `PasteCommand` (`IExplorerCommand` + `IObjectWithSite`, CLSID `{373E9CED-1AAA-4486-9EC6-DB3A03E8E315}`), not
  in DllRegisterServer: the package registers it. Destination: the one selected folder or drive, else (no
  selection, or an empty one) the site's `SID_SFolderView` → `IFolderView::GetFolder` as an `IShellItem`, else
  `IPersistFolder2::GetCurFolder`. `GetState` = `CanPaste` (never E_PENDING), `Invoke` = `PasteInto` with the
  site's window (`IOleWindow`) as the message box's owner, `GetIcon` = `"<dll>,-101"`, `ECF_DEFAULT`, no
  tooltip, no subcommands. Its own `CoTaskString`, so no SHLWAPI.dll.
- `Paste.*`: `kPasteTitle`, `CanPaste`, `PasteInto` (the clipboard read, the message boxes, StartJob), out of
  `PasteHandler`, which now calls them.
- `Icons.*` and `CopyMasterShell.rc`: the icon as resource 101, `IconLocation()`, and `MenuBitmap` (LoadImage at
  SM_CXSMICON, GetIconInfo/GetDIBits, premultiplied into a 32-bit DIB section; one per handler, deleted with
  it). `AddMenuItems` takes the bitmap. A version block names the DLL ("CopyMaster Explorer extension").
- DLL imports: GDI32 joins KERNEL32, USER32, ADVAPI32, SHELL32, ole32 and api-ms-win-core-winrt-error; still no
  VC++ runtime. Release DLL 378 KB (was 211), mostly the icon.

**The package (`package\AppxManifest.xml`, `dev\package.ps1`):** Name `CopyMaster`, x64, unsigned publisher,
`AllowExternalContent`, both write virtualizations off, `AppListEntry="none"`, `Executable="CopyMaster.exe"`,
`Directory` / `Directory\Background` (desktop5) and `Drive` (desktop10) naming the CLSID, `com:Class
Path="CopyMasterShell.dll"`. The script: `-BuildOnly` (makeappx `/nv` into `package\bin`), default build and
register (`-ExternalLocation` = the deploy folder, replacing any CopyMaster package), `-Unregister`, `-Status`;
it refuses while CopyMaster.exe runs from the deploy folder, since removing a package ends its processes.

**Icons:** `dev\make-icons.cs` draws a light sheet behind a blue one with a white arrow, from shapes on a
256-unit grid at each size (outlines at least a pixel), and writes `assets\CopyMaster.ico` (16 to 128 px as
32-bit DIBs with masks, 256 as PNG) and the package's three logos. CopyMaster.exe (ApplicationIcon, and
`AppIcon.cs` loading the embedded .ico for the main window, dialogs and Details) and cmcopy carry it.

**ShellHost:** `--command` drives the IExplorerCommand: a fake site (`IServiceProvider` + `IOleWindow` with a
hidden window of its own) whose `IFolderView` gives the folder (or, `--site-shellfolder`, only its
`IShellFolder`); `--background` (no selection), `--empty-items`, `--site <folder>`. Prints `state`, `title`,
`icon`, `icons`, `canonical`, `flags`. A **message-box watcher** on every invoke reads each box's text and
whether it's owned, and closes it: the compact menu has no "no UI" flag. Classic items print a `bitmap` line.
`cocreate <clsid> local` activates out of process (for the packaged surrogate, once registered).

**Found:** a message box on the private desktop never became visible (a dialog shows itself the first time its
thread's queue runs dry), and a posted *or sent* WM_COMMAND IDOK didn't end it; a posted WM_CLOSE did (an
OK-only box returns IDOK for that, as for Esc). The watcher looks for `#32770` with the 0xFFFF text control,
visible or not.

**Tests:** 29 new in `ShellExtensionTests.Command.cs` (227 pass, 11 opt-in skipped): a cut on a folder (move,
`clipseq` = the clipboard's number); a background four ways (no selection, empty, shell-folder-only view, both);
the selection beats the view; the Desktop's background; a drive; This PC, the Recycle Bin, Control Panel, a
library and a .zip get none; two folders get none; text or nothing on the clipboard hides it; the hung owner
(shown, then the click gives up at 3 s and says so); the clipboard losing its files (message owned by the site's
window); no CopyMaster.exe (message names the path, no job file); title, icon location, canonical name, flags;
the classic items' 32-bit bitmaps; end to end through cmcopy (moved, clipboard emptied); the manifest against
Clsids.h (verbs, class, Path, Executable, AppListEntry, the OID, the logos) and makeappx's schema check; the
icon in the DLL and both exes. Debug and Release build clean; the 76 shell tests pass on Release too.
