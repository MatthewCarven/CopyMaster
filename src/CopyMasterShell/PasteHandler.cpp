#include <windows.h>
#include <shlobj.h>
#include <wrl/module.h>

#include <new>
#include <string>

#include "ContextMenu.h"
#include "Folders.h"
#include "Paste.h"
#include "PasteHandler.h"

CoCreatableClass(PasteHandler);

namespace
{

constexpr MenuItem kItems[] = {
    {kPasteTitle, L"CopyMaster.Paste", "CopyMaster.Paste", kPasteHelp, "Pastes the files on the clipboard here with CopyMaster."},
};

} // namespace

IFACEMETHODIMP PasteHandler::Initialize(PCIDLIST_ABSOLUTE folder, IDataObject* selection, HKEY)
{
    m_destination.clear();
    try
    {
        // A right-click on a folder or drive hands over the selection: paste into that. On a folder's
        // background (or the Desktop's) there's no selection, just the folder: paste here.
        if (selection != nullptr) m_destination = SelectedFolder(selection);
        else if (folder != nullptr) m_destination = FileSystemFolder(folder);
        return S_OK;
    }
    catch (const std::bad_alloc&)
    {
        m_destination.clear();
        return E_OUTOFMEMORY;
    }
    catch (...)
    {
        m_destination.clear();
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP PasteHandler::QueryContextMenu(HMENU menu, UINT index, UINT firstId, UINT lastId, UINT flags)
{
    if ((flags & CMF_DEFAULTONLY) || !CanPaste(m_destination)) return kNoMenuItems;
    return AddMenuItems(menu, index, firstId, lastId, kItems, m_bitmap.Get());
}

IFACEMETHODIMP PasteHandler::InvokeCommand(CMINVOKECOMMANDINFO* info)
{
    if (info == nullptr || info->cbSize < sizeof(CMINVOKECOMMANDINFO)) return E_INVALIDARG;
    if (FindMenuItem(info, kItems) < 0) return E_FAIL; // not ours
    if (m_destination.empty()) return E_UNEXPECTED;
    return PasteInto(m_destination, info->hwnd, (info->fMask & CMIC_MASK_FLAG_NO_UI) != 0);
}

IFACEMETHODIMP PasteHandler::GetCommandString(UINT_PTR id, UINT type, UINT*, CHAR* name, UINT size)
{
    return MenuItemString(kItems, id, type, name, size);
}
