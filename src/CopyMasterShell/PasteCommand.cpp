#include <windows.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <wrl/module.h>

#include <string.h>
#include <wchar.h>

#include <new>
#include <string>

#include "Folders.h"
#include "Icons.h"
#include "Paste.h"
#include "PasteCommand.h"

using Microsoft::WRL::ComPtr;

CoCreatableClass(PasteCommand);

namespace
{

// A copy of `text` the caller frees with CoTaskMemFree (what SHStrDupW does, without SHLWAPI.dll).
HRESULT CoTaskString(const wchar_t* text, LPWSTR* out)
{
    size_t bytes = (wcslen(text) + 1) * sizeof(wchar_t);
    *out = static_cast<LPWSTR>(CoTaskMemAlloc(bytes));
    if (*out == nullptr) return E_OUTOFMEMORY;
    memcpy(*out, text, bytes);
    return S_OK;
}

} // namespace

std::wstring PasteCommand::Destination(IShellItemArray* items) const
{
    DWORD count = 0;
    if (items != nullptr && FAILED(items->GetCount(&count))) return {};
    if (count > 1) return {}; // Explorer's own Paste wants one folder too
    if (count == 1)
    {
        ComPtr<IShellItem> item;
        if (FAILED(items->GetItemAt(0, &item))) return {};
        return FileSystemFolder(item.Get());
    }
    // No selection (or, as Explorer sometimes gives, an empty one): a right-click on a background.
    return SiteFolder();
}

// The folder Explorer's view shows: its site's IFolderView. As a shell item if the view gives one, else
// as the folder's IDList.
std::wstring PasteCommand::SiteFolder() const
{
    ComPtr<IServiceProvider> services;
    ComPtr<IFolderView> view;
    if (!m_site || FAILED(m_site.As(&services)) ||
        FAILED(services->QueryService(SID_SFolderView, __uuidof(IFolderView), reinterpret_cast<void**>(view.GetAddressOf()))))
    {
        return {};
    }
    ComPtr<IShellItem> item;
    if (SUCCEEDED(view->GetFolder(IID_PPV_ARGS(&item)))) return FileSystemFolder(item.Get());

    ComPtr<IPersistFolder2> folder;
    PIDLIST_ABSOLUTE pidl = nullptr;
    if (FAILED(view->GetFolder(IID_PPV_ARGS(&folder))) || FAILED(folder->GetCurFolder(&pidl))) return {};
    // Freed by hand: a unique_ptr of an __unaligned PIDL type raises C4090.
    std::wstring path;
    try
    {
        path = FileSystemFolder(pidl);
    }
    catch (...)
    {
        CoTaskMemFree(pidl);
        throw;
    }
    CoTaskMemFree(pidl);
    return path;
}

// Explorer's window, to own a message box, if the site says.
HWND PasteCommand::OwnerWindow() const noexcept
{
    ComPtr<IOleWindow> window;
    HWND owner = nullptr;
    if (m_site && SUCCEEDED(m_site.As(&window)) && SUCCEEDED(window->GetWindow(&owner))) return owner;
    return nullptr;
}

IFACEMETHODIMP PasteCommand::GetTitle(IShellItemArray*, LPWSTR* title)
{
    if (title == nullptr) return E_POINTER;
    return CoTaskString(kPasteTitle, title);
}

IFACEMETHODIMP PasteCommand::GetIcon(IShellItemArray*, LPWSTR* icon)
{
    if (icon == nullptr) return E_POINTER;
    *icon = nullptr;
    try
    {
        std::wstring location = IconLocation();
        return location.empty() ? E_FAIL : CoTaskString(location.c_str(), icon);
    }
    catch (const std::bad_alloc&)
    {
        return E_OUTOFMEMORY;
    }
}

IFACEMETHODIMP PasteCommand::GetToolTip(IShellItemArray*, LPWSTR* tip)
{
    if (tip == nullptr) return E_POINTER;
    *tip = nullptr;
    return E_NOTIMPL; // the compact menu shows none
}

IFACEMETHODIMP PasteCommand::GetCanonicalName(GUID* name)
{
    if (name == nullptr) return E_POINTER;
    *name = __uuidof(PasteCommand);
    return S_OK;
}

IFACEMETHODIMP PasteCommand::GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state)
{
    // Everything here is quick (the selection's attributes are in its IDLists; the clipboard's list of
    // formats is always there), so there's never a need for E_PENDING and a second, slow call.
    if (state == nullptr) return E_POINTER;
    *state = ECS_HIDDEN;
    try
    {
        if (CanPaste(Destination(items))) *state = ECS_ENABLED;
        return S_OK;
    }
    catch (const std::bad_alloc&)
    {
        return E_OUTOFMEMORY;
    }
    catch (...)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP PasteCommand::Invoke(IShellItemArray* items, IBindCtx*)
{
    try
    {
        std::wstring destination = Destination(items);
        if (destination.empty()) return E_UNEXPECTED; // GetState hid it: someone else's code asked
        // There's no "no UI" flag here as there is for the classic menu, so failures always say why.
        return PasteInto(destination, OwnerWindow(), false);
    }
    catch (const std::bad_alloc&)
    {
        return E_OUTOFMEMORY;
    }
    catch (...)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP PasteCommand::GetFlags(EXPCMDFLAGS* flags)
{
    if (flags == nullptr) return E_POINTER;
    *flags = ECF_DEFAULT;
    return S_OK;
}

IFACEMETHODIMP PasteCommand::EnumSubCommands(IEnumExplorerCommand** commands)
{
    if (commands == nullptr) return E_POINTER;
    *commands = nullptr;
    return E_NOTIMPL;
}

IFACEMETHODIMP PasteCommand::SetSite(IUnknown* site)
{
    m_site = site;
    return S_OK;
}

IFACEMETHODIMP PasteCommand::GetSite(REFIID iid, void** site)
{
    if (site == nullptr) return E_POINTER;
    *site = nullptr;
    return m_site ? m_site.CopyTo(iid, site) : E_FAIL;
}
