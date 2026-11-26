#pragma once

#include <windows.h>
#include <shlobj.h>
#include <wrl/implements.h>

#include <string>
#include <vector>

#include "Clsids.h"

// "CopyMaster Copy here" and "CopyMaster Move here" in the right-drag menu.
//
// Explorer makes one of these for each right-button drop on a folder, drive or the Desktop
// (registered under Directory, Drive and Folder\shellex\DragDropHandlers). It calls Initialize with
// the target folder and the dragged items, then QueryContextMenu, then InvokeCommand if one of ours
// was chosen; all on its UI thread. The items show only for a real file-system folder as target and
// dragged items that carry file paths (CF_HDROP), and not when every item is already in that folder
// (a right-drag on the Desktop that lands on the Desktop, which only moves an icon; copying there does
// nothing in CopyMaster, as moving there never did). Choosing one hands a job to CopyMaster.exe and
// returns at once (JobLauncher.h); nothing here touches the files themselves, and nothing waits.
class __declspec(uuid(COPYMASTER_DRAGDROP_CLSID)) DragDropHandler final
    : public Microsoft::WRL::RuntimeClass<Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>, IShellExtInit,
                                          IContextMenu>
{
public:
    // IShellExtInit
    IFACEMETHODIMP Initialize(PCIDLIST_ABSOLUTE folder, IDataObject* data, HKEY progId) override;

    // IContextMenu
    IFACEMETHODIMP QueryContextMenu(HMENU menu, UINT index, UINT firstId, UINT lastId, UINT flags) override;
    IFACEMETHODIMP InvokeCommand(CMINVOKECOMMANDINFO* info) override;
    IFACEMETHODIMP GetCommandString(UINT_PTR id, UINT type, UINT* reserved, CHAR* name, UINT size) override;

private:
    std::wstring m_destination; // empty: not somewhere we can copy to, so no items
    std::vector<std::wstring> m_sources;
    bool m_allAlreadyThere = false; // every source is in m_destination: no items (a verb still works)
};
