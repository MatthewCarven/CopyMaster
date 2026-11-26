// DllRegisterServer / DllUnregisterServer, so `regsvr32` (and later an installer) can use them.
//
// Everything goes in HKLM. UAC is off on Matthew's laptop, so Explorer runs elevated, and COM
// ignores per-user (HKCU) class registrations in an elevated process: confirmed in phase 3 with
// tools\ShellHost (WORKLOG.md). Writing HKLM needs an elevated caller, which is every process here.

#include <windows.h>
#include <objbase.h>
#include <shlobj.h>

#include <new>
#include <string>

#include "DragDropHandler.h"
#include "Module.h"
#include "Registration.h"

namespace
{

constexpr wchar_t kName[] = L"CopyMaster drag-and-drop handler";
constexpr wchar_t kHandlerName[] = L"CopyMaster";
constexpr wchar_t kApproved[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Approved";

// Where right-drag handlers are looked up: a folder, a drive, and Folder for the Desktop (and any
// other shell folder; Initialize hides the items unless the target is a real file-system folder).
// Windows' own zip right-drag handlers sit under Folder. Explorer loads one CLSID once, however
// many of these keys name it.
constexpr const wchar_t* kTargets[] = {L"Directory", L"Drive", L"Folder"};

std::wstring ClsidText()
{
    wchar_t text[40]{};
    StringFromGUID2(__uuidof(DragDropHandler), text, ARRAYSIZE(text));
    return text;
}

std::wstring HandlerKey(const wchar_t* target)
{
    return std::wstring(L"Software\\Classes\\") + target + L"\\shellex\\DragDropHandlers\\" + kHandlerName;
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

HRESULT Register()
{
    std::wstring dll = ModulePath();
    if (dll.empty()) return HRESULT_FROM_WIN32(GetLastError());
    std::wstring clsid = ClsidText();
    std::wstring classKey = L"Software\\Classes\\CLSID\\" + clsid;

    HRESULT result = SetString(classKey, nullptr, kName);
    if (SUCCEEDED(result)) result = SetString(classKey + L"\\InprocServer32", nullptr, dll);
    if (SUCCEEDED(result)) result = SetString(classKey + L"\\InprocServer32", L"ThreadingModel", L"Apartment");
    for (const wchar_t* target : kTargets)
    {
        if (SUCCEEDED(result)) result = SetString(HandlerKey(target), nullptr, clsid);
    }
    if (SUCCEEDED(result)) result = SetString(kApproved, clsid.c_str(), kName);
    return result;
}

HRESULT Unregister()
{
    std::wstring clsid = ClsidText();
    HRESULT result = S_OK;
    for (const wchar_t* target : kTargets)
    {
        HRESULT removed = Removed(RegDeleteKeyExW(HKEY_LOCAL_MACHINE, HandlerKey(target).c_str(), KEY_WOW64_64KEY, 0));
        if (SUCCEEDED(result)) result = removed;
    }
    HRESULT removed = Removed(RegDeleteKeyValueW(HKEY_LOCAL_MACHINE, kApproved, clsid.c_str()));
    if (SUCCEEDED(result)) result = removed;
    removed = Removed(RegDeleteTreeW(HKEY_LOCAL_MACHINE, (L"Software\\Classes\\CLSID\\" + clsid).c_str()));
    if (SUCCEEDED(result)) result = removed;
    return result;
}

} // namespace

HRESULT RegisterServer() noexcept
{
    try
    {
        HRESULT result = Register();
        if (FAILED(result))
        {
            Unregister(); // all or nothing
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
        HRESULT result = Unregister();
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
