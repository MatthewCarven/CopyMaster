#pragma once

#include <windows.h>

#include <string>
#include <vector>

// Reading the files Copy or Cut put on the clipboard, without letting another program hang Explorer.
//
// The clipboard's data usually isn't there yet: Explorer, like most programs, puts a promise of each
// format on it (delayed rendering), and the first program to ask for one makes the owner produce it.
// That's a message sent to the owner's window, so a hung owner hangs whoever asks. So the menu only
// looks at the list of formats, and the click reads the data on a thread of its own, for a while.

// Files read from the clipboard.
struct ClipboardFiles
{
    std::vector<std::wstring> paths; // CF_HDROP, in order
    bool cut = false;                // "Preferred DropEffect" asks for a move, not a copy: Cut in Explorer
    DWORD sequence = 0;              // GetClipboardSequenceNumber for exactly what was read; 0 if not pinned down
};

// Whether the clipboard offers file paths (CF_HDROP). Only the list of formats: nothing is produced
// and no other program is asked, so it answers at once whatever state the owner is in.
bool ClipboardHasFiles() noexcept;

// Reads the clipboard's files on a thread of its own and waits for it at most `timeoutMs`. While it
// waits, the calling thread answers messages sent to it, and nothing else: the clipboard's owner may
// be a window on this very thread (files copied in the same Explorer window), and has to be able to
// produce its data. Errors:
//   DV_E_FORMATETC                     no files on the clipboard (it has changed since the menu)
//   HRESULT_FROM_WIN32(ERROR_TIMEOUT)  the owner didn't produce the data in time (the thread carries on,
//                                      and gives up the clipboard when the owner answers or goes away)
//   anything else                      the clipboard couldn't be opened (another program has it), ...
HRESULT ReadClipboardFiles(ClipboardFiles& files, DWORD timeoutMs) noexcept;
