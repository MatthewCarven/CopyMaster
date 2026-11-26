#pragma once

#include <windows.h>

#include <string>

// "CopyMaster Paste", whichever menu it's in: the classic right-click menu (PasteHandler) or Windows 11's
// compact one (PasteCommand). Both decide where to paste from what Explorer hands them; this is the rest.

inline constexpr wchar_t kPasteTitle[] = L"CopyMaster Paste";
inline constexpr wchar_t kPasteHelp[] = L"Pastes the files on the clipboard here with CopyMaster.";

// Whether to offer Paste: somewhere on disk to paste to, and files on the clipboard. Reads only the
// clipboard's list of formats, so it answers at once whatever state the clipboard's owner is in.
bool CanPaste(const std::wstring& destination) noexcept;

// Reads the clipboard's files (on a thread of its own, for at most 3 s: Clipboard.h) and hands
// CopyMaster.exe the job: a copy, or for a cut a move carrying the clipboard's sequence number. On
// failure, unless `quiet`, a message box owned by `owner` says why.
HRESULT PasteInto(const std::wstring& destination, HWND owner, bool quiet) noexcept;
