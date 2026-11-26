#pragma once

#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>

#include <string>
#include <vector>

// Where a handler would copy to. Each reads only what the shell already knows about its items (their
// IDLists carry the attributes the folder view listed them with): no disk access, nothing to hang on.
// May throw std::bad_alloc.

// The folder's file-system path, or empty if it isn't a folder on disk: This PC, Control Panel, the
// Recycle Bin, a library, a phone, or a .zip (which shows as a folder but is a file, SFGAO_STREAM).
// The namespace root (the Desktop's own target) gives the Desktop folder.
std::wstring FileSystemFolder(PCIDLIST_ABSOLUTE folder);
std::wstring FileSystemFolder(IShellItem* item);

// The item a right-click was on, from the selection Explorer hands a context menu handler: its path if
// exactly one item is selected and it's a folder on disk (a folder, or a drive), else empty.
std::wstring SelectedFolder(IDataObject* selection);

// The paths in a CF_HDROP, in order (a drag's, or the clipboard's).
std::vector<std::wstring> DropFiles(HDROP drop);
