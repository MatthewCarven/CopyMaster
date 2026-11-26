#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <wrl/module.h>

#include <iterator>
#include <new>
#include <optional>
#include <string_view>

#include "ContextMenu.h"
#include "DragDropHandler.h"
#include "Folders.h"
#include "JobLauncher.h"

CoCreatableClass(DragDropHandler);

namespace
{

// Menu offset i is kItems[i], which does kOperations[i].
constexpr MenuItem kItems[] = {
    {L"CopyMaster Copy here", L"CopyMaster.CopyHere", "CopyMaster.CopyHere", L"Copies the items here with CopyMaster.",
     "Copies the items here with CopyMaster."},
    {L"CopyMaster Move here", L"CopyMaster.MoveHere", "CopyMaster.MoveHere", L"Moves the items here with CopyMaster.",
     "Moves the items here with CopyMaster."},
};
constexpr Operation kOperations[] = {Operation::Copy, Operation::Move};
static_assert(std::size(kItems) == std::size(kOperations));

// The dragged items' paths, in order, or none if the drag carries no file paths (CF_HDROP).
std::vector<std::wstring> DroppedFiles(IDataObject* data)
{
    FORMATETC format{CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
    STGMEDIUM medium{};
    if (FAILED(data->GetData(&format, &medium))) return {};
    struct Release
    {
        STGMEDIUM* medium;
        ~Release() { ReleaseStgMedium(medium); }
    } release{&medium};
    if (medium.tymed != TYMED_HGLOBAL || medium.hGlobal == nullptr) return {};
    return DropFiles(static_cast<HDROP>(medium.hGlobal));
}

// A path without its trailing backslashes ("C:\" becomes "C:"), for comparing folders.
std::wstring_view WithoutTrailingSlash(std::wstring_view path)
{
    while (!path.empty() && path.back() == L'\\') path.remove_suffix(1);
    return path;
}

// Whether every source sits directly in `folder` (compared without case, as NTFS names are). Only the
// strings: no disk access. A source with no backslash, or a drive root, never counts.
bool AllIn(const std::wstring& folder, const std::vector<std::wstring>& sources)
{
    std::wstring_view target = WithoutTrailingSlash(folder);
    for (const std::wstring& source : sources)
    {
        std::wstring_view path = WithoutTrailingSlash(source);
        size_t slash = path.rfind(L'\\');
        if (slash == std::wstring_view::npos) return false;
        std::wstring_view parent = WithoutTrailingSlash(path.substr(0, slash));
        if (CompareStringOrdinal(parent.data(), static_cast<int>(parent.size()), target.data(),
                                 static_cast<int>(target.size()), TRUE) != CSTR_EQUAL)
        {
            return false;
        }
    }
    return !sources.empty();
}

} // namespace

IFACEMETHODIMP DragDropHandler::Initialize(PCIDLIST_ABSOLUTE folder, IDataObject* data, HKEY)
{
    m_destination.clear();
    m_sources.clear();
    m_allAlreadyThere = false;
    if (folder == nullptr || data == nullptr) return S_OK; // nothing to offer
    try
    {
        std::wstring destination = FileSystemFolder(folder);
        if (destination.empty()) return S_OK;
        std::vector<std::wstring> sources = DroppedFiles(data);
        if (sources.empty()) return S_OK;
        m_allAlreadyThere = AllIn(destination, sources);
        m_destination = std::move(destination);
        m_sources = std::move(sources);
        return S_OK;
    }
    catch (const std::bad_alloc&)
    {
        m_destination.clear();
        m_sources.clear();
        return E_OUTOFMEMORY;
    }
    catch (...)
    {
        m_destination.clear();
        m_sources.clear();
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP DragDropHandler::QueryContextMenu(HMENU menu, UINT index, UINT firstId, UINT lastId, UINT flags)
{
    if ((flags & CMF_DEFAULTONLY) || m_destination.empty() || m_sources.empty() || m_allAlreadyThere)
    {
        return kNoMenuItems;
    }
    return AddMenuItems(menu, index, firstId, lastId, kItems);
}

IFACEMETHODIMP DragDropHandler::InvokeCommand(CMINVOKECOMMANDINFO* info)
{
    if (info == nullptr || info->cbSize < sizeof(CMINVOKECOMMANDINFO)) return E_INVALIDARG;
    int item = FindMenuItem(info, kItems);
    if (item < 0) return E_FAIL; // not one of ours
    if (m_destination.empty() || m_sources.empty()) return E_UNEXPECTED;
    bool quiet = (info->fMask & CMIC_MASK_FLAG_NO_UI) != 0;
    return StartJob(kOperations[item], m_destination, m_sources, std::nullopt, info->hwnd, quiet);
}

IFACEMETHODIMP DragDropHandler::GetCommandString(UINT_PTR id, UINT type, UINT*, CHAR* name, UINT size)
{
    return MenuItemString(kItems, id, type, name, size);
}
