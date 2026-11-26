// DllRegisterServer / DllUnregisterServer, so `regsvr32` (and later an installer) can use them.
//
// Everything goes in HKLM: it works whether Explorer is elevated or not, and suits a machine-wide
// installer. (On Matthew's laptop UAC is off and tokens aren't split, so COM would honour HKCU too:
// WORKLOG.md, phase 3. With UAC on, an elevated Explorer would ignore HKCU.) Writing HKLM needs an
// elevated caller, which is every process here.

#include <windows.h>
#include <objbase.h>
#include <shlobj.h>

#include <new>
#include <span>
#include <string>

#include "DragDropHandler.h"
#include "Module.h"
#include "PasteHandler.h"
#include "Registration.h"

namespace
{

constexpr wchar_t kApproved[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Approved";

// Right-drag handlers: a folder, a drive, and Folder for the Desktop (and any other shell folder;
// Initialize hides the items unless the target is a real file-system folder). Windows' own zip
// right-drag handlers sit under Folder. Explorer loads one CLSID once, however many keys name it.
constexpr const wchar_t* kDragDropKeys[] = {
    L"Directory\\shellex\\DragDropHandlers\\CopyMaster",
    L"Drive\\shellex\\DragDropHandlers\\CopyMaster",
    L"Folder\\shellex\\DragDropHandlers\\CopyMaster",
};

// Right-click handlers: a folder's background (Directory\Background serves the Desktop's background
// too, as Windows' own "New" and NVIDIA's desktop items show), a folder, and a drive.
constexpr const wchar_t* kPasteKeys[] = {
    L"Directory\\Background\\shellex\\ContextMenuHandlers\\CopyMaster",
    L"Directory\\shellex\\ContextMenuHandlers\\CopyMaster",
    L"Drive\\shellex\\ContextMenuHandlers\\CopyMaster",
};

struct Handler
{
    const CLSID& clsid;
    const wchar_t* name;
    std::span<const wchar_t* const> keys; // under Software\Classes, each naming the CLSID
};

const Handler kHandlers[] = {
    {__uuidof(DragDropHandler), L"CopyMaster drag-and-drop handler", kDragDropKeys},
    {__uuidof(PasteHandler), L"CopyMaster paste handler", kPasteKeys},
};

std::wstring ClsidText(const CLSID& clsid)
{
    wchar_t text[40]{};
    StringFromGUID2(clsid, text, ARRAYSIZE(text));
    return text;
}

std::wstring Classes(const std::wstring& key)
{
    return L"Software\\Classes\\" + key;
}

HRESULT SetString(const std::wstring& key, const wchar_t* name, const std::wstring& value)
{
    LSTATUS status = RegSetKeyValueW(HKEY_LOCAL_MACHINE, key.c_str(), name, REG_SZ, value.c_str(),
                                     static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
    return HRESULT_FROM_WIN32(status);
}

// Gone already counts as done.
HRESULT Removed(LSTATUS status)
{
    return status == ERROR_SUCCESS || status == ERROR_FILE_NOT_FOUND ? S_OK : HRESULT_FROM_WIN32(status);
}

HRESULT Register(const Handler& handler, const std::wstring& dll)
{
    std::wstring clsid = ClsidText(handler.clsid);
    std::wstring classKey = Classes(L"CLSID\\" + clsid);

    HRESULT result = SetString(classKey, nullptr, handler.name);
    if (SUCCEEDED(result)) result = SetString(classKey + L"\\InprocServer32", nullptr, dll);
    if (SUCCEEDED(result)) result = SetString(classKey + L"\\InprocServer32", L"ThreadingModel", L"Apartment");
    for (const wchar_t* key : handler.keys)
    {
        if (SUCCEEDED(result)) result = SetString(Classes(key), nullptr, clsid);
    }
    if (SUCCEEDED(result)) result = SetString(kApproved, clsid.c_str(), handler.name);
    return result;
}

HRESULT Unregister(const Handler& handler)
{
    std::wstring clsid = ClsidText(handler.clsid);
    HRESULT result = S_OK;
    auto note = [&](HRESULT removed) {
        if (SUCCEEDED(result)) result = removed;
    };
    for (const wchar_t* key : handler.keys)
    {
        note(Removed(RegDeleteKeyExW(HKEY_LOCAL_MACHINE, Classes(key).c_str(), KEY_WOW64_64KEY, 0)));
    }
    note(Removed(RegDeleteKeyValueW(HKEY_LOCAL_MACHINE, kApproved, clsid.c_str())));
    note(Removed(RegDeleteTreeW(HKEY_LOCAL_MACHINE, Classes(L"CLSID\\" + clsid).c_str())));
    return result;
}

HRESULT RegisterAll()
{
    std::wstring dll = ModulePath();
    if (dll.empty()) return HRESULT_FROM_WIN32(GetLastError());
    for (const Handler& handler : kHandlers)
    {
        HRESULT result = Register(handler, dll);
        if (FAILED(result)) return result;
    }
    return S_OK;
}

HRESULT UnregisterAll()
{
    HRESULT result = S_OK;
    for (const Handler& handler : kHandlers)
    {
        HRESULT removed = Unregister(handler);
        if (SUCCEEDED(result)) result = removed;
    }
    return result;
}

} // namespace

HRESULT RegisterServer() noexcept
{
    try
    {
        HRESULT result = RegisterAll();
        if (FAILED(result))
        {
            UnregisterAll(); // all or nothing
            return result;
        }
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr); // Explorer drops its cached lookups
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

HRESULT UnregisterServer() noexcept
{
    try
    {
        HRESULT result = UnregisterAll();
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
        return result;
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
