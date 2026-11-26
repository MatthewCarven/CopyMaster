# CopyMaster: plan

An Explorer extension that copies and moves files with CopyMaster's own engine
instead of Explorer's, from two places:

- **The right-drag menu**: drag files with the right mouse button, drop on a folder,
  and the menu gets **CopyMaster Copy here** and **CopyMaster Move here**.
- **Right-click Paste**: after Copy or Cut in Explorer, right-click a folder, a folder's
  background or a drive and choose **CopyMaster Paste**. A cut becomes a move.

## Decisions (Matthew, 2026-11-26)

| Question | Decision |
|---|---|
| Engine language | C# on .NET 10 (LTS), WinForms. The shell extension is C++ (it has to be native). |
| Windows 11 menus | Classic first: Paste shows under "Show more options" and on Shift+right-click. The compact top-level menu comes in phase 5. The right-drag menu is still classic on Windows 11, so it needs nothing extra. |
| Several jobs at once | Queue per drive: jobs that share a disk wait their turn; jobs on different disks run side by side. |
| v1 extras | Speed limit (1 to 128 MB/s, then Unlimited), verify after copy, pause/resume, conflict dialog, skip errors and retry later. |

## Architecture

```
explorer.exe
 └─ CopyMasterShell.dll          C++, loaded inside Explorer, kept tiny
     ├─ drag-drop handler        right-drag → "Copy here" / "Move here"
     ├─ paste handler            right-click → "Paste" (clipboard file list), the classic menu
     └─ paste command            the same in Windows 11's compact menu (via a sparse package)
          │  writes a job file:  %LOCALAPPDATA%\CopyMaster\jobs\<guid>.cmjob
          │  starts:             CopyMaster.exe --job <file>
          ▼
CopyMaster.exe                   C#, its own process
 ├─ single instance              a second launch hands its job over a named pipe, then exits
 ├─ scheduler                    one queue per physical disk (and per server for shares)
 ├─ job runner                   list files → conflicts → copy → verify → delete sources (moves)
 └─ UI                           queue, progress, speed slider, pause, conflicts, errors
```

**Why two parts.** Anything inside explorer.exe that crashes or hangs takes Explorer
with it (Matthew's Explorer crashed on 2026-11-01 because of a third-party extension
DLL). So the DLL only gathers {operation, destination, sources}, writes them down and
starts the engine. It never touches file contents. Microsoft doesn't support .NET code
inside Explorer, so the DLL is C++. Everything worth testing lives in the engine, which
also runs from a command line, so most of the testing never involves Explorer.

### The shell DLL

- An in-process COM server written with WRL (in the Windows SDK; ATL isn't installed
  and isn't needed). Each handler is `IShellExtInit` + `IContextMenu`. x64 only.
- **Drag-drop handler**: registered under `Directory\shellex\DragDropHandlers\CopyMaster`
  and the same under `Drive` and `Folder` (built in phase 3). `Initialize` receives the
  target folder and the dragged items' data object. The items are added only when the
  target is a real file-system folder (a folder, not a stream such as a .zip, with a
  file-system path) and the data object carries `CF_HDROP` (real file paths). The Desktop's
  drop target is the namespace root, whose file-system path is the Desktop folder, so it
  qualifies; `Folder` makes sure Explorer asks us there (Windows' own zip right-drag handlers
  are under `Folder`).
- **Paste handler** (built in phase 4; CLSID `{355681EC-9621-450F-8449-6B5769A1149B}`):
  registered under `Directory\Background\shellex\ContextMenuHandlers` (paste here; this key
  serves the Desktop's background too, as Windows' own "New" and NVIDIA's desktop items show),
  `Directory\shellex\ContextMenuHandlers` (paste into that folder) and
  `Drive\shellex\ContextMenuHandlers`. On a background, Initialize gets the folder; on an item,
  the selection, which must be one file-system folder or drive (as for Explorer's own Paste). If
  the clipboard holds no files, the item is hidden rather than greyed.
- **The clipboard can't be allowed to hang Explorer.** Its data usually isn't there yet:
  Explorer's Copy, like most programs', promises each format (delayed rendering), and the first
  reader makes the owner produce it with a message sent to the owner's window, so a hung owner
  hangs the reader. So showing the menu asks only `IsClipboardFormatAvailable(CF_HDROP)`, which
  reads the list of formats. The click reads `CF_HDROP` and `Preferred DropEffect` on a thread of
  its own, for at most 3 s; the UI thread meanwhile answers only messages sent to it (the owner
  may be a window on that very thread: files copied in the same Explorer window), nothing posted
  and no input. A timeout says so in a message box. Cut means `DROPEFFECT_MOVE` without
  `DROPEFFECT_COPY` (Explorer's Copy offers COPY | LINK), so a program offering both is copied.
- **Cut, then paste**: Explorer empties the clipboard once a cut has been pasted. The move
  finishes later, in the engine, so the DLL records the clipboard's sequence number
  (`GetClipboardSequenceNumber`) in the job (`clipseq`, for cuts only). It's taken before opening
  the clipboard and after closing it, and kept only if it held still, so it names exactly what was
  read (producing a delayed format could move it on; in practice it didn't). When the move has
  gone through (no failures, and something moved or was already there), the engine opens the
  clipboard, and empties it only if the number is still the same (so it never wipes something
  Matthew copied since); on a thread of its own, for at most 2 s, since emptying tells the old
  owner. A cut pasted into its own folder moves nothing, so it stays on the clipboard. A retry
  that finishes the move empties it then. The source isn't told (`CFSTR_PASTESUCCEEDED`): the
  files have moved already, and Explorer clears its cut look when the clipboard changes.
- **Paste a copy into its own folder** does nothing, as a right-drag there does (Explorer would
  make "X - Copy"). The DLL can't hide the item for it: knowing where the files are means reading
  the clipboard, which showing the menu mustn't do.
- **Windows 11's compact menu** (phase 5; CLSID `{373E9CED-1AAA-4486-9EC6-DB3A03E8E315}`): Windows 11
  shows only packaged apps' commands at the top level, as `IExplorerCommand`s. So `PasteCommand`
  (`IExplorerCommand` + `IObjectWithSite`) lives in the same DLL, sharing `Paste.*` with the classic
  handler, and a **sparse package** (`package\AppxManifest.xml`: `windows.fileExplorerContextMenus` for
  `Directory`, `Directory\Background` and `Drive`, plus `com:SurrogateServer`/`com:Class` naming the DLL)
  gives CopyMaster the identity. The package holds only the manifest and logos; its *external location*
  is the deploy folder, so the DLL and exe stay where they are and a new DLL needs no new package. It's
  **unsigned**: the Publisher carries the OID Windows 11 accepts with `Add-AppxPackage -AllowUnsigned`, so
  no certificate is made or trusted (it needs an elevated shell, and is installed for all users).
  `dev\package.ps1` builds it with makeappx (`/nv`: the DLL and exe aren't in it) and registers it.
  - Where to paste: the one selected folder or drive, or with no selection (a background; Explorer may
    also give an empty one) the folder the site's `IFolderView` shows, as an `IShellItem` or failing that
    through `IPersistFolder2`. The selection wins over the view. `GetState` shows it only for a folder on
    disk with files on the clipboard (the list of formats only, so never `E_PENDING`); `Invoke` is the
    classic click (worker thread, 3 s). There's no "no UI" flag, so failures always show a message box,
    owned by the site's window.
  - **It runs in a surrogate** (seen 2026-11-27): each menu starts `DllHost.exe
    /Processid:{373E9CED-…}` (launched by COM's svchost) inside the package, with our DLL loaded; an
    unpackaged process can't create the class in-process (0x80040154). The clipboard is the same there
    (one session and window station), so nothing about reading it changes, and the 3 s limit still holds.
    **CopyMaster.exe started from it runs inside the package** (parent dllhost.exe, package identity), so
    removing or replacing the package would end it, mid-copy or not: the scripts refuse while it runs.
    Its window still came to the front, its job file was found (write virtualization is off), and a launch
    from the package handed its job to a CopyMaster.exe started from Explorer (outside the package). A way
    out, not taken yet: have Explorer start it (IShellDispatch2::ShellExecute through the shell's windows).
  - **The package's Paste shows in the classic menu too**, beside the other packaged apps' items, so with
    both registered "Show more options" had two (as PowerToys' docs warn). So while the package is in, the
    classic handler's three Paste keys come out (`dev\CopyMasterSetup.psm1`, used by `package.ps1` and
    `reload.ps1`; registry only, no Explorer restart) and the package's Paste serves both menus. Without the
    package (Windows 10, or unregistered), regsvr32 puts them back.
  - **Matthew's laptop has no compact menu to show**: StartAllBack, loaded in Explorer, swaps Windows 11's
    compact menu for the classic one (turning that off needs a sign-out). So his menus are all classic,
    where the package's Paste shows once, with its icon.
  - **The right-drag menu** is classic on Windows 11 too, so the drag-drop handler needs nothing more.
    Nothing else belongs in the compact menu for now: Copy here / Move here exist only for a drag.
- **Icons**: `assets\CopyMaster.ico` (from `dev\make-icons.cs`, drawn from shapes at each size, 16 to
  256 px) is the exe's icon, the windows', and the DLL's resource 101. The compact menu gets it as
  `"<dll>,-101"` (`GetIcon`); the classic items as a 32-bit premultiplied bitmap at the small-icon size
  (`MenuBitmap`, one per handler, since a handler outlives the menu it fills).
- **Job file**: UTF-8 lines: the operation, the destination, then one source path per
  line, plus the clipboard sequence number for cut-pastes. Windows file names can't
  contain line breaks, so lines are safe and need no JSON code in C++.
- **Registration goes in HKLM.** It works whether Explorer is elevated or not, and suits a
  machine-wide installer. (The plan assumed HKCU would be ignored because Explorer runs
  elevated here. Phase 3 found otherwise: with UAC off the token isn't split (elevation
  type "default"), and COM created a class registered only in HKCU. With UAC on, an
  elevated process would ignore HKCU.) The CLSIDs also go in `Shell Extensions\Approved`.
  `DllRegisterServer` / `DllUnregisterServer`, so `regsvr32` works and an installer can
  use it later.
- **Deployed layout**: one folder holds CopyMaster.exe with its files and
  CopyMasterShell.dll; the DLL starts the CopyMaster.exe beside it. For now that folder is
  `%LOCALAPPDATA%\Programs\CopyMaster`, filled by `dev\reload.ps1`; phase 5's installer may
  move it to Program Files. Never the build output: Explorer would lock the bin folder.
- **Dev loop**: once Explorer loads the DLL, that copy keeps running until Explorer exits.
  `dev\reload.ps1` builds Release, moves the old DLL aside (a loaded DLL can be renamed,
  not overwritten), copies the new build in, registers it, and restarts Explorer only if
  an Explorer has the DLL loaded. **A restart closes every open Explorer window and blanks
  the taskbar for a moment**, so Claude says so before running it. `tools\ShellHost` is a
  small C++ program that loads the DLL and drives its handlers with a fake data object, so
  most DLL changes can be tested without restarting Explorer at all.
- **No test touches the real clipboard.** The clipboard belongs to the window station, so
  `ShellHost isolated` makes a window station and desktop of its own and runs the paste tests
  there (ShellHost refuses to set the clipboard anywhere else). A job object keeps that window
  station until everything started inside it has ended, the engine included. For the end-to-end
  tests the fake engine hands the job to the real cmcopy.exe, which moves the files and empties
  (or leaves) the private clipboard.

### The engine (CopyMaster.exe)

- **Copy or move?** A move within one volume is a rename (`MoveFileEx`): instant, no
  data copied. A move to another volume is copy, then verify (if on), then delete the
  source, then remove emptied source folders. Sources are deleted permanently, as
  Explorer does for moves, and never before their copy has verified.
- **Copy primitive: `CopyFile2`** (decided in phase 1). It keeps the modified date,
  attributes and alternate data streams, including `Zone.Identifier` (the
  downloaded-from-the-internet mark, which must survive a copy); we set the created date
  ourselves. Speed limit and pause block inside its per-chunk progress callback. Measured
  on C:: 1 MiB chunks for big files, 512 KiB for a 6 MiB one, and at 1 MB/s a chunk
  every 0.50 s, so the limit is smooth. Over SMB to the desktop: 1 MiB chunks too, and
  at 2 MB/s a chunk every 0.50 s. It reserves a file's full size before copying, so a file
  too big for the disk fails at once instead of partway. Within one share it has the
  server copy the data itself (8 MB in 0.16 s over a ~11 MB/s Wi-Fi link).
- **Every file goes through a temporary name** (`.~cm<random>.tmp` beside its
  destination): copied, verified there, then renamed into place. A file shows up under
  its real name only when it's complete and checked; an overwrite replaces the old file
  in one step, so a failed copy never destroys it.
- **Speed limit**: a slider on each job, adjustable while it runs, plus a default for new
  jobs in settings, and a second default for jobs that read from or write to a network share
  (8 MB/s; such a job starts at the lower of the two). Stops: 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128 MB/s, then
  Unlimited. "MB" means 1,048,576 bytes, as Explorer does, so the numbers match Explorer's.
  The limit covers verify reads too, because the usual reason for a limit is a network
  link (the household cap; copies to the desktop share).
- **Verify**: compare each copy with its source byte for byte, the copy read back
  **unbuffered** (`FILE_FLAG_NO_BUFFERING`), so it comes from the disk and not the
  file cache. A cached read would have passed the bad-MiB copy found on the desktop on
  2026-11-02. (Planned as a hash; both files have to be read anyway, so comparing them
  directly is exact and needs no package.) A mismatch is copied once more; a second
  mismatch fails the file. **Over SMB to a Windows server this reaches the server's disk
  too** (settled in phase 1b): reading back a just-written 128 MB file unbuffered made the
  desktop's D: read 127.2 MB (and first flush the rest of its cached writes), and a file
  read twice hit the disk both times. That matches [MS-SMB2] 3.3.5.9 (the server opens
  the file with the client's create options, FILE_NO_INTERMEDIATE_BUFFERING included) and
  [MS-FSA] 2.1.5.3 (such a read writes cached data out, then reads from disk). Only the
  drive's own internal cache is beyond reach; a non-Windows server (a NAS) may differ.
- **Queue per drive**: each job needs a set of resources, one per physical disk its
  sources and destination are on (volume → disk via `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS`,
  so two partitions of one disk share a queue). Network paths are keyed by server; a
  mapped drive letter counts as its share (`WNetGetConnection`, as `Volumes.Key` does). A job
  starts when none of its resources is busy and no job queued before it wants one of
  them (so nothing waits forever). The queue can be reordered, a job can be started now
  anyway, paused or removed. (Built in phase 2.) Pausing a job that hasn't started holds
  it: it neither starts nor holds up the jobs behind it. A paused running job keeps its
  disks; Start now on another job runs that one beside it. A move within one volume is
  only renames, so it takes no disk time and doesn't queue at all.
- **Conflicts**: a dialog showing both files' size and date, with Overwrite, Skip,
  Rename (keep both, as `name (2).ext`) and Keep newer, plus "Do this for every
  conflict in this job". Only that job waits for the answer; others keep running. A
  default policy (Ask, or one of the four) lives in settings.
- **Identical files aren't conflicts** (Matthew, 2026-11-26): if the file already at the
  destination is the same size, it's compared byte for byte (read back from its disk, as
  verify does). Identical means nothing is copied and nothing is asked, whatever the
  policy (even "keep both" makes no duplicate). A move then only deletes the source, but
  only once the two are proven to be different files: a hard link, or one share by two
  names, compares identical because it *is* the destination. Counted as "already there".
  Unreadable counts as different (the usual conflict). The cost: re-running a big copy
  reads both sides of every equal-size file instead of skipping at once.
- **Into its own folder does nothing** (Matthew, 2026-11-26): copying or moving an item into
  the folder it's already in is a note ("Already in that folder"), not a duplicate. A
  right-drag on the Desktop that lands on the Desktop is usually an icon being moved; the
  first version made `name (2)` beside the original, which looked like a copy over itself.
  The right-drag menu doesn't offer the CopyMaster items when every dragged item is already
  in the target folder; the engine's rule covers the command line and a mixed drag.
- **Errors**: a file that fails (locked, access denied) is logged with the reason and
  skipped. At the end: **Retry failed (n)** or **Done**. A full disk is different: the
  job pauses and asks, rather than skipping every remaining file.
- **Pause / resume / cancel**: per job, including in the middle of a large file. Cancel
  leaves no half-written files behind (a partial file is deleted).
- **Long paths**: paths beyond 260 characters work (`\\?\` prefix in the Win32 calls).
- **Explorer refresh**: `SHChangeNotify` on destination folders when a job finishes.
- **Command line**: `cmcopy.exe copy|move --dest <dir> [--verify] [--limit <MB/s>]
  [--conflict skip|overwrite|rename|newer] <sources...>`, or `--job <file>`. A console
  program of its own, because CopyMaster.exe is a windowed app (a terminal can't wait on
  it or see its output). CopyMaster.exe takes `--job <file>` from the DLL, and
  `copy|move --dest <dir> <sources...>` from a terminal (options from its settings).
- **UI**: the queue, and per job the current file, bytes done, speed, time left, the
  speed slider, pause, cancel, the error list and a summary at the end. Follows Windows'
  dark mode (Matthew runs dark mode; .NET's `Application.SetColorMode`). Settings hold
  what new jobs start with: speed limit (Unlimited), speed limit on network shares (8 MB/s),
  verify (on), conflicts (Ask). A job uses a share if any of its scheduler keys is `server:`
  (a UNC path, or a mapped drive).
- **Not enough room**: before copying, if the files add up to more than the destination
  has free, the job asks whether to start anyway (conflicts skipped or overwritten would
  need less; a disk that fills up still pauses and asks).

### Layout

```
CopyMaster.slnx
src/CopyMaster.Engine/            C# library: jobs, scheduler and queue, copy, verify, conflicts,
                                  single instance and the pipe (Instance/). No UI.
src/CopyMaster/                   C# WinForms exe: the window, dialogs, --job, copy|move
src/CopyMaster.Cli/               cmcopy.exe: the engine from a terminal
src/CopyMasterShell/              C++ DLL (vcxproj): drag-drop and paste handlers, the compact menu's
                                  Paste command, registration, the icon
package/                          the sparse package: AppxManifest.xml and its logos (phase 5)
assets/CopyMaster.ico             the icon, made by dev/make-icons.cs (phase 5)
tests/CopyMaster.Engine.Tests/    xunit
tools/ShellHost/                  C++ test program that drives the DLL without Explorer (phase 3)
dev/reload.ps1                    restart Explorer with a fresh DLL build (phase 3)
dev/package.ps1                   build and register the sparse package (phase 5)
dev/make-icons.cs                 draws the icon and writes the .ico and the package's logos (phase 5)
dev/test-volumes.ps1              two VHDXs for the cross-volume and disk-full tests
dev/window-shots.cs               renders the window's states and dialogs to PNGs, off-screen
dev/window-e2e.ps1                drives the real CopyMaster.exe with UI Automation
build.ps1                         build everything with VS 2026's MSBuild
```

`dotnet build` can't build the vcxproj, so the whole solution builds with VS 2026's
MSBuild (`build.ps1`). `dotnet build`/`dotnet test` still work on single C# projects.

## Phases

Each is about one session. Phases 1 and 2 never touch Explorer.

| Phase | Work | Done when |
|---|---|---|
| 0 | Skeleton: solution, empty projects, CLAUDE.md, TODO, WORKLOG | Everything builds, `dotnet test` runs |
| 1a | Engine core and command line: copy, rename-move, cross-volume move, conflict policies (no dialog yet), skip-and-retry errors, speed limit, pause/cancel, verify, long paths, metadata and `Zone.Identifier` | Done 2026-11-26: 59 tests pass on C: |
| 1b | The same for real: across volumes (a VHDX), to the desktop share (chunk sizes, the SMB verify question), a genuinely full disk | Done 2026-11-26: 10 opt-in integration tests pass on two VHDXs and the share |
| 2 | Engine UI: queue window, progress, live speed slider, pause, conflict dialog, error list, summary, single instance + named pipe, queue per drive, settings, dark mode | Matthew has run real copies and moves through the window. Built 2026-11-26 (122 tests, an end-to-end UI Automation check); his run is still to come |
| 3 | Shell DLL, right-drag: handler, job file, launching the engine, registration, `dev\reload.ps1`, ShellHost | Done 2026-11-26: Matthew right-dragged files in Explorer and got CopyMaster (no Explorer restart needed). A drop into the items' own folder now does nothing (20 ShellHost tests) |
| 4 | Right-click Paste: handler on folder, background and drive, clipboard read, cut → move, clipboard emptied after a cut | Done 2026-11-27: Matthew pasted a copy and a cut in Explorer, on a folder, a folder's background and the Desktop (37 new tests, ShellHost on a private window station, end to end through cmcopy) |
| 5a | Windows 11 compact menu (`IExplorerCommand` + an unsigned sparse package), icons | Paste appears in the compact menu. Built and deployed 2026-11-27 (29 new tests): Matthew pasted with the package's Paste in his menus (classic, through StartAllBack), once, with its icon. The compact menu itself waits for a sign-out with StartAllBack's classic menu off |
| 5b | Installer and uninstaller | Uninstall leaves nothing behind |

Testing moves between volumes needs a second volume: the laptop has only C:. A small
VHDX made and attached with `diskpart` works on Windows Home (no Hyper-V needed).

## Risks and open questions

- ~~HKLM vs HKCU registration with Explorer elevated~~: settled in phase 3 (HKLM; HKCU would
  work here too, since UAC is off).
- ~~The Desktop as a paste target~~: `Directory\Background` serves the Desktop's background (Windows'
  own "New" and NVIDIA's desktop items are registered there); ShellHost shows the root gives the
  Desktop folder, and Matthew's paste on the Desktop worked (2026-11-27).
- **The `=` in `-=Programming=-` breaks link.exe's `/LIBPATH`** (found building Terminal,
  2026-10-16). The DLL links only Windows SDK libraries, which live outside this folder.
  Don't add a static-library project here without allowing for that.
- Hand tests in Explorer are Matthew's, or Claude's through computer use, with
  Matthew's go-ahead.
- Disk: C: had 48.7 GB free on 2026-11-26. Big-file tests use temp folders and clean up
  after themselves.

## Out of scope (for now)

- Taking over plain left-drag and Ctrl+V. That means hooking into Explorer, which is much
  riskier than a menu item.
- Sources with no real file paths: zip folders, phones (MTP), FTP.
- Explorer's Ctrl+Z undo.
- Copying NTFS permissions. Explorer's copies don't either (a copy takes the destination
  folder's permissions); a same-volume move keeps them, as ours will.
