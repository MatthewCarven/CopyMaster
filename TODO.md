# TODO

## For Matthew

- [x] **Right-drag on the Desktop once more** (the same-folder fix, deployed 2026-11-26 23:18):
      Matthew confirmed the CopyMaster items are gone for a Desktop-onto-Desktop drop. (Still
      unnoted: whether the items show once or twice on a real drop. To take the extension out:
      `.\dev\reload.ps1 -Unregister`.)
- [ ] Two leftovers on the Desktop from the first right-drag test, yours to delete or keep:
      `New folder (5) (2)` (a cancelled copy: 233 of 281 files) and
      `Gotye Somebody I used to know - ID-Error (2).mp3` (a complete duplicate).
- [x] **Right-drag files in Explorer** (phase 3's last step): it worked (2026-11-26), with no
      Explorer restart.
- [x] **Run real copies and moves through the window** (phase 2's last step). Build with
      `.\build.ps1`, then from the repo folder:
      `& ".\src\CopyMaster\bin\Debug\net10.0-windows\CopyMaster.exe" copy --dest <folder> <source>...`
      (or `move`). Run it again while the first is going to see the hand-over and the queue;
      copy the same files twice to get the conflict dialog. Jobs on Z: (or any share) now
      start at 8 MB/s on their own (Settings → "Speed limit on network shares"); **don't drag
      a Z: card's slider past 8**, or the laptop falls off the internet.
- [ ] Decide: **verify is on by default** for new jobs (it reads every copy back, so jobs take
      about twice as long). Settings turns it off.
- [x] **`git init` this folder**: Matthew ran it 2026-11-26; the first commit holds phases 0–3.
      Pushed to https://github.com/MatthewCarven/CopyMaster (`origin`, branch `master`). There's no
      licence file: that's yours to pick, if you want one.
- [ ] **Delete the empty folder `Z:\_CopyMasterTest`** (phase 1b's scratch folder on the
      desktop share). Claude's tool shell refuses to delete a folder at the top of a drive.

## Phase 0: skeleton, done 2026-11-26

- [x] `CopyMaster.slnx` with `src/CopyMaster.Engine` (classlib, net10.0), `src/CopyMaster`
      (WinForms, net10.0-windows), `src/CopyMasterShell` (C++ DLL, x64, v145, static CRT),
      `tests/CopyMaster.Engine.Tests` (xunit 2.9).
- [x] `.gitignore` (bin, obj, .vs, x64, *.user).
- [x] `build.ps1` builds everything, Debug and Release; `dotnet test` runs (1 smoke test).

## Phase 1a: engine core and command line, done 2026-11-26

- [x] Enumerate sources (files and folders, recursively, hidden and system too), plan
      destination paths. Links to folders inside a tree are skipped (noted).
- [x] `CopyFile2` measured and chosen: 512 KiB–1 MiB chunks on C:, a 1 MB/s limit lands a
      chunk every 0.50 s.
- [x] Rename-move within a volume (merging into an existing folder); copy + delete across
      volumes, falling back to it on ERROR_NOT_SAME_DEVICE; emptied source folders removed.
- [x] Keep dates (created and modified), attributes, alternate data streams (`Zone.Identifier`);
      folder dates and attributes too.
- [x] Copy through a temporary name, verified, then renamed into place.
- [x] Conflict policies: overwrite, skip, rename (`name (2).ext`), keep newer; Ask through
      `IJobPrompts` with "do this for all". (Same-folder copies kept both until 2026-11-26; now they
      do nothing, as same-folder moves always did: see phase 3.)
- [x] Errors: skip and record with the reason; `RetryFailed`. A folder that can't be made
      holds its contents for the retry. A failed source delete retries as a delete only.
- [x] Disk full: ask (retry / skip file / stop); no prompts → stop with a reason.
- [x] Speed limit, live-adjustable, covering verify reads.
- [x] Pause, resume, cancel (mid-file; cancel leaves nothing behind).
- [x] Verify: byte-for-byte against an unbuffered read; one re-copy on a mismatch.
- [x] Long paths (> 260 characters).
- [x] `cmcopy.exe` (src/CopyMaster.Cli): `copy|move --dest ... [--verify] [--limit] [--conflict]`,
      `--job <file>`, Ctrl+C cancels, exit codes 0/1/2/3. Job file format (`JobFile`).
- [x] 59 tests on C:.

## Phase 1b: the same, for real, done 2026-11-26

- [x] `dev\test-volumes.ps1`: two VHDXs (256 MB and 32 MB) via `diskpart`, mounted on folders
      (no drive letters, so nothing pops up in Explorer); `-Remove` detaches and deletes them.
- [x] Opt-in integration tests (skipped unless `CM_TEST_VOLUME`, `CM_TEST_SMALL_VOLUME`,
      `CM_TEST_SHARE`, `CM_TEST_SHARE_UNC` are set): real cross-volume moves both ways (dates,
      `Zone.Identifier` kept), a genuinely full disk three ways, verified copies and moves on
      the share. 10 tests, all passing.
- [x] SMB: 1 MiB chunks; 2 MB/s lands a chunk every 0.50 s. ~11 MB/s unlimited over Wi-Fi.
- [x] An unbuffered read over SMB reaches the desktop's disk (measured with its disk counters
      over SSH; matches [MS-SMB2]/[MS-FSA]). PLAN.md has the numbers.
- [x] Z: and `\\server\share` now count as one volume (`WNetGetConnection`), so a move between
      the two names is a rename (8 MB: 0.03 s, nothing copied), not a copy.
- [x] `Volumes.FreeSpace` (works on mount folders and shares; for phase 2's "won't fit" check).

## Phase 2: engine UI, done 2026-11-26

- [x] Matthew's run through the window: a copy (Windows' wallpapers, twice) and a move onto an
      identical copy, both right; the Details window showed a missing source correctly.

- [x] Queue window; per job: current file, bytes, speed, time left, items; overall % in the title.
- [x] Speed slider on each card, live (1 to 128 MB/s, then Unlimited); default in settings.
- [x] Pause / resume / cancel buttons.
- [x] Conflict dialog (both files' size and date, "newer" marked; Overwrite / Skip / Keep both /
      Keep newer / Cancel job; "do this for every conflict in this job"). Modeless: only that
      job waits. Disk-full dialog (try again / skip file / stop).
- [x] Details window (failures and notes, Copy list) with Retry failed; summary on each card.
- [x] Single instance: named mutex + named pipe; a second launch hands over its job and exits.
      `CopyMaster.exe copy|move --dest <folder> <source>...` and `--job <file>`.
- [x] Scheduler: queue per physical disk (disk extents), per server for shares, no starvation;
      reorder (▲▼), start now, pause (holds a waiting job), remove.
- [x] Settings: default speed limit (and one for network shares, 8 MB/s), verify on/off,
      default conflict policy.
- [x] Before starting, warn if the job won't fit (`IJobPrompts.StartWithoutRoom`).
- [x] Follow Windows' dark mode.
- [x] Tests: scheduler (12), queue with real copies (8), disks (6), hand-over (9), settings and
      units (24), room check (4), plus an opt-in VHDX disk-key test. 122 pass, 11 opt-in skipped; 129 with the VHDXs.
      With the identical-files and network-limit tests since: 140 pass, 11 opt-in skipped.
- [x] `dev\window-shots.cs` (renders every window state to PNGs, off-screen) and
      `dev\window-e2e.ps1` (drives the real exe with UI Automation: hand-over, a conflict, quitting).

## Identical files aren't conflicts (Matthew's idea), done 2026-11-26

- [x] Same size at the destination → compare byte for byte (destination read unbuffered).
      Identical: nothing copied, nothing asked, whatever the policy; counted as "already there".
- [x] A move with an identical destination deletes only the source, after `Volumes.DifferentFiles`
      proves they're two files (hard links and one share by two names are the same file).
- [x] "Comparing" phase in the window and cmcopy; "N already there" in both summaries.
- [x] 10 tests; switching off both same-file guards made the hard-link tests fail (either alone holds).
- [x] Matthew tried it in the window (2026-11-26): `C:\Windows\Web` copied to Temp a second time
      read "Nothing copied, 8 already there in 0 s".
- [x] Matthew: a move onto an identical copy: "Nothing moved, 8 already there", no question; the
      sources were deleted and `Temp\Web` still hash-matched `C:\Windows\Web`.
- [ ] Matthew: a real copy to Z:, to see the network-share limit (8 MB/s) start on its own.

## Ideas surfaced in phase 2 (not decided)

- [ ] **"Trust size and date" for identical files**, as a setting: much faster when re-running a
      big copy (no reading both sides), but only safe for copies. A move would still compare
      before deleting a source.

- [x] **A default speed limit for network shares** (8 MB/s), separate from the local one, so
      copies to Z: respect the household cap without touching the slider. Done 2026-11-26: a job
      whose resources include a `server:` key starts at the lower of the two limits; 8 tests.
- [ ] After a disk-full **Stop**, what the job never reached can't be retried (it isn't a failure);
      the job has to be run again (what's done now shows as "already there"). A "carry on" could re-plan.
- [ ] Taskbar progress (ITaskbarList3) as well as the % in the title.
- [ ] Phase 5, with UAC on: the pipe is `CurrentUserOnly`, which compares token owners, so an
      elevated and a non-elevated launch can't hand over to each other. Not an issue while UAC is off.

## Phase 3: shell DLL, right-drag, done 2026-11-26

- [x] WRL COM server: `DllGetClassObject`, `DllCanUnloadNow`, `DllRegisterServer`, `DllUnregisterServer`.
- [x] Drag-drop handler: "CopyMaster Copy here" / "CopyMaster Move here", only for `CF_HDROP`
      sources and file-system targets (not This PC, the Recycle Bin, Control Panel, libraries, zips).
- [x] Write the job file, start `CopyMaster.exe` from the DLL's own folder. Deployed layout:
      `%LOCALAPPDATA%\Programs\CopyMaster`, the exe's build output plus the DLL.
- [x] Register in HKLM + `Shell Extensions\Approved`. **HKCU turned out not to be ignored**: with UAC
      off the token has no split, and COM created an HKCU-only class (WORKLOG). HKLM kept anyway.
- [x] `dev\reload.ps1` (restarts Explorer only when it has the DLL loaded); `tools\ShellHost`.
- [x] The Desktop: its drop target is the namespace root, which the handler takes (C:\Users\Admin\Desktop);
      registered under `Folder` as well as Directory and Drive to be sure Explorer asks us.
- [x] Tests: 15 in `ShellExtensionTests` through ShellHost (round trip through `JobFile.Read`, `=` and
      spaces, long paths, every way of invoking, the places that get no items). 155 pass.
- [x] Matthew: right-drag files in Explorer (copy and move, to a folder and to the Desktop): it worked,
      and a first registration needed no Explorer restart. Whether the items showed once wasn't noted.
- [x] Matthew's note: a file and a folder copied from the Desktop onto the Desktop became `X (2)` beside
      the original, and the card ("Copy X to C:\Users\Admin\Desktop") read like a copy over itself. His
      call: **a drop into the items' own folder does nothing.** The DLL offers no items when every
      dragged item is already in the target folder; the engine makes a same-folder copy a note, as a
      same-folder move always was. 2 engine tests, 5 ShellHost tests; 161 pass. Deployed 23:18 (Explorer restarted).

## Ideas surfaced in phase 3 (not decided)

- [ ] `reload.ps1` could leave the DLL alone when the built one is byte-identical to the deployed one,
      so a change to the exe alone doesn't restart Explorer.
- [ ] An item on the Public Desktop (`C:\Users\Public\Desktop`) dragged onto the Desktop still gets the
      items (its folder isn't the user's Desktop folder), and a copy would show a second icon there.
- [ ] Icons on the two menu items (phase 5 has icons anyway).
- [ ] A right-drag out of the Recycle Bin: check what CF_HDROP holds there (probably the `$R...` files
      inside `$Recycle.Bin`, which would copy under those names). Not tried.
- [ ] 7-Zip's right-drag items are registered under Directory and Drive only, so they may not show on
      the Desktop. Nothing to do with CopyMaster; just a thing seen in the registry.

## Phase 4: right-click Paste

- [ ] Paste handler on `Directory\Background`, `Directory`, `Drive`; hidden when the clipboard
      has no files.
- [ ] Cut (`Preferred DropEffect` = move) becomes a move; empty the clipboard after the move
      if its sequence number hasn't changed.
- [ ] Matthew: paste a copy and a cut, into a folder's background, onto a folder, onto a drive.

## Phase 5: Windows 11 compact menu, installer

- [ ] `IExplorerCommand` Paste verb + sparse package signed with a self-signed certificate.
- [ ] Installer and uninstaller (uninstall leaves nothing behind); icons.
