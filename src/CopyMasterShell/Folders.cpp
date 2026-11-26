#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <wrl/client.h>

#include <memory>

#include "Folders.h"
#include "Module.h"

using Microsoft::WRL::ComPtr;

std::wstring FileSystemFolder(IShellItem* item)
{
    if (item == nullptr) return {};
    SFGAOF attributes = 0;
    if (FAILED(item->GetAttributes(SFGAO_FOLDER | SFGAO_STREAM, &attributes))) return {};
    if (!(attributes & SFGAO_FOLDER) || (attributes & SFGAO_STREAM)) return {};

    PWSTR path = nullptr;
    if (FAILED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)) || path == nullptr) return {};
    std::unique_ptr<wchar_t, CoTaskMemDeleter> owned(path);
    return path;
}

std::wstring FileSystemFolder(PCIDLIST_ABSOLUTE folder)
{
    ComPtr<IShellItem> item;
    if (folder == nullptr || FAILED(SHCreateItemFromIDList(folder, IID_PPV_ARGS(&item)))) return {};
    return FileSystemFolder(item.Get());
}

std::wstring SelectedFolder(IDataObject* selection)
{
    // The shell's IDList array (CFSTR_SHELLIDLIST), which every Explorer selection carries. Without it
    // there's nothing to go on but paths, and parsing a path can mean waiting on a network.
    ComPtr<IShellItemArray> items;
    if (selection == nullptr || FAILED(SHCreateShellItemArrayFromDataObject(selection, IID_PPV_ARGS(&items)))) return {};
    DWORD count = 0;
    if (FAILED(items->GetCount(&count)) || count != 1) return {}; // Explorer's own Paste wants one folder too
    ComPtr<IShellItem> item;
    if (FAILED(items->GetItemAt(0, &item))) return {};
    return FileSystemFolder(item.Get());
}

std::vector<std::wstring> DropFiles(HDROP drop)
{
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
