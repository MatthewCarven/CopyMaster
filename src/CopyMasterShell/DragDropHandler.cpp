#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <strsafe.h>
#include <wrl/client.h>
#include <wrl/module.h>

#include <iterator>
#include <memory>
#include <new>
#include <string_view>
#include <string.h>

#include "DragDropHandler.h"
#include "JobLauncher.h"
#include "Module.h"

using Microsoft::WRL::ComPtr;

CoCreatableClass(DragDropHandler);

namespace
{

struct Command
{
    Operation operation;
    const wchar_t* text;
    const wchar_t* verb;
    const char* verbAnsi;
    const wchar_t* help;
    const char* helpAnsi;
};

// Menu offset i is kCommands[i].
constexpr Command kCommands[] = {
    {Operation::Copy, L"CopyMaster Copy here", L"CopyMaster.CopyHere", "CopyMaster.CopyHere",
     L"Copies the items here with CopyMaster.", "Copies the items here with CopyMaster."},
    {Operation::Move, L"CopyMaster Move here", L"CopyMaster.MoveHere", "CopyMaster.MoveHere",
     L"Moves the items here with CopyMaster.", "Moves the items here with CopyMaster."},
};
constexpr UINT kCommandCount = static_cast<UINT>(std::size(kCommands));

// The target's file-system path, or empty if it isn't a folder on disk: This PC, Control Panel, the
// Recycle Bin, a library, a phone, or a .zip (which shows as a folder but is a file, SFGAO_STREAM).
// Reads only what the shell already knows about the item; no disk access, so nothing to hang on.
std::wstring FileSystemFolder(PCIDLIST_ABSOLUTE folder)
{
    ComPtr<IShellItem> item;
    if (FAILED(SHCreateItemFromIDList(folder, IID_PPV_ARGS(&item)))) return {};
    SFGAOF attributes = 0;
    if (FAILED(item->GetAttributes(SFGAO_FOLDER | SFGAO_STREAM, &attributes))) return {};
    if (!(attributes & SFGAO_FOLDER) || (attributes & SFGAO_STREAM)) return {};

    PWSTR path = nullptr;
    if (FAILED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)) || path == nullptr) return {};
    std::unique_ptr<wchar_t, CoTaskMemDeleter> owned(path);
    return path;
}

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

    auto drop = static_cast<HDROP>(medium.hGlobal);
    UINT count = DragQueryFileW(drop, 0xFFFFFFFF, nullptr, 0);
    std::vector<std::wstring> paths;
    paths.reserve(count);
    for (UINT i = 0; i < count; ++i)
    {
        UINT length = DragQueryFileW(drop, i, nullptr, 0);
        if (length == 0) continue;
        std::wstring path(length, L'\0');
        if (DragQueryFileW(drop, i, path.data(), length + 1) != length) continue;
        paths.push_back(std::move(path));
    }
    return paths;
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

// Which of ours `info` asks for, or -1: a menu offset (a click), or a verb (someone else's code).
int CommandIndex(const CMINVOKECOMMANDINFO* info)
{
    bool unicode = info->cbSize >= sizeof(CMINVOKECOMMANDINFOEX) && (info->fMask & CMIC_MASK_UNICODE);
    if (unicode)
    {
        auto ex = reinterpret_cast<const CMINVOKECOMMANDINFOEX*>(info);
        if (!IS_INTRESOURCE(ex->lpVerbW))
        {
            for (UINT i = 0; i < kCommandCount; ++i)
            {
                if (CompareStringOrdinal(ex->lpVerbW, -1, kCommands[i].verb, -1, TRUE) == CSTR_EQUAL)
                {
                    return static_cast<int>(i);
                }
            }
            return -1;
        }
    }
    if (!IS_INTRESOURCE(info->lpVerb))
    {
        for (UINT i = 0; i < kCommandCount; ++i)
        {
            if (_stricmp(info->lpVerb, kCommands[i].verbAnsi) == 0) return static_cast<int>(i);
        }
        return -1;
    }
    UINT_PTR offset = reinterpret_cast<UINT_PTR>(info->lpVerb);
    return offset < kCommandCount ? static_cast<int>(offset) : -1;
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
    UINT added = 0;
    if (menu == nullptr || (flags & CMF_DEFAULTONLY) || m_destination.empty() || m_sources.empty() ||
        m_allAlreadyThere || lastId < firstId || lastId - firstId < kCommandCount - 1)
    {
        return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 0);
    }
    for (UINT i = 0; i < kCommandCount; ++i)
    {
        MENUITEMINFOW item{};
        item.cbSize = sizeof(item);
        item.fMask = MIIM_ID | MIIM_STRING | MIIM_FTYPE;
        item.fType = MFT_STRING;
        item.wID = firstId + i;
        item.dwTypeData = const_cast<LPWSTR>(kCommands[i].text);
        if (!InsertMenuItemW(menu, index + i, TRUE, &item)) break;
        ++added;
    }
    return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, added); // highest offset used + 1
}

IFACEMETHODIMP DragDropHandler::InvokeCommand(CMINVOKECOMMANDINFO* info)
{
    if (info == nullptr || info->cbSize < sizeof(CMINVOKECOMMANDINFO)) return E_INVALIDARG;
    int command = CommandIndex(info);
    if (command < 0) return E_FAIL; // not one of ours
    if (m_destination.empty() || m_sources.empty()) return E_UNEXPECTED;
    bool quiet = (info->fMask & CMIC_MASK_FLAG_NO_UI) != 0;
    return StartJob(kCommands[command].operation, m_destination, m_sources, info->hwnd, quiet);
}

IFACEMETHODIMP DragDropHandler::GetCommandString(UINT_PTR id, UINT type, UINT*, CHAR* name, UINT size)
{
    if (id >= kCommandCount) return E_INVALIDARG;
    const Command& command = kCommands[id];
    switch (type)
    {
    case GCS_VALIDATEW:
    case GCS_VALIDATEA:
        return S_OK;
    case GCS_VERBW:
    case GCS_HELPTEXTW:
        if (name == nullptr || size == 0) return E_INVALIDARG;
        return StringCchCopyW(reinterpret_cast<PWSTR>(name), size, type == GCS_VERBW ? command.verb : command.help);
    case GCS_VERBA:
    case GCS_HELPTEXTA:
        if (name == nullptr || size == 0) return E_INVALIDARG;
        return StringCchCopyA(name, size, type == GCS_VERBA ? command.verbAnsi : command.helpAnsi);
    default:
        return E_NOTIMPL;
    }
}
