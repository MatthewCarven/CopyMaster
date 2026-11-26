#pragma once

#include <windows.h>
#include <shlobj.h>
#include <wrl/implements.h>

#include <string>

#include "Clsids.h"
#include "Icons.h"

// "CopyMaster Paste" in the classic right-click menu (on Windows 11, under "Show more options"; the
// compact menu's is PasteCommand, and both share Paste.h).
//
// Registered under Directory\Background (a folder's background, and the Desktop's), Directory (a
// folder) and Drive\shellex\ContextMenuHandlers. Explorer makes one per right-click and calls
// Initialize, QueryContextMenu, then InvokeCommand if Paste was chosen, all on its UI thread.
//
// The item shows only when there's somewhere on disk to paste to and the clipboard offers files
// (CF_HDROP). Showing it reads nothing but the clipboard's list of formats. Choosing it reads the
// files and whether they were cut (Clipboard.h, which keeps a hung clipboard owner from hanging
// Explorer), then hands a job to CopyMaster.exe: a copy, or for a cut a move that carries the
// clipboard's sequence number, so CopyMaster.exe can empty the clipboard once the move has gone
// through, as Explorer does after pasting a cut. Nothing here touches the files themselves.
class __declspec(uuid(COPYMASTER_PASTE_CLSID)) PasteHandler final
    : public Microsoft::WRL::RuntimeClass<Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>, IShellExtInit,
                                          IContextMenu>
{
public:
    // IShellExtInit
    IFACEMETHODIMP Initialize(PCIDLIST_ABSOLUTE folder, IDataObject* selection, HKEY progId) override;

    // IContextMenu
    IFACEMETHODIMP QueryContextMenu(HMENU menu, UINT index, UINT firstId, UINT lastId, UINT flags) override;
    IFACEMETHODIMP InvokeCommand(CMINVOKECOMMANDINFO* info) override;
    IFACEMETHODIMP GetCommandString(UINT_PTR id, UINT type, UINT* reserved, CHAR* name, UINT size) override;

private:
    std::wstring m_destination; // empty: not somewhere we can paste to, so no item
    MenuBitmap m_bitmap;        // the icon beside the item, kept while the menu may show it
};
