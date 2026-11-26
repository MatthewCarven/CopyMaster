// ShellHost: drives CopyMasterShell.dll's handlers the way Explorer does, without Explorer, so the
// DLL can be tested without restarting Explorer. It also stands in for CopyMaster.exe.
//
//   ShellHost menu   --dll <dll> --target <target> [data] <source>...
//       Loads the DLL (no registry), Initialize + QueryContextMenu; prints the items added.
//   ShellHost invoke --dll <dll> --target <target> --verb <verb> [--by-verb|--by-verb-ansi] [--ui] [data] <source>...
//       ...then InvokeCommand on the item with that verb: by its menu offset, as Explorer does for
//       a click, or by the verb itself (Unicode or ANSI). No message boxes unless --ui.
//   ShellHost cocreate <clsid>
//       CoCreateInstance through the registry, to see which registrations COM honours here.
//   ShellHost --job <file>
//       Pretends to be CopyMaster.exe (copy ShellHost.exe as CopyMaster.exe beside the DLL): writes
//       its command line to .\received\<name>.cmdline beside itself, then moves the job file there.
//
// <target>  a folder path; a parsing name such as ::{20D04FE0-3AEA-1069-A2D8-08002B30309D} (This PC);
//           or "desktop", the root of the namespace, as Explorer's desktop window has it.
// [data]    (default) a data object holding CF_HDROP with exactly <source>..., in order
//           --no-hdrop    a data object with no file paths (as a phone, or a zip's contents, give)
//           --shell-data  the shell's own data object for <source>... (what an Explorer drag carries)
//
// Prints, one per line (UTF-8): "target <SFGAO hex> <file-system path or ->", "initialize <hr>",
// "query <hr>", "item <offset> <verb> <text>", "invoke <hr>", "cocreate <hr>", "error <text>".
// Exit code: 0 if everything asked for succeeded, 1 if not, 2 for a bad command line.

#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <wrl/client.h>
#include <wrl/implements.h>

#include <cstdio>
#include <cstring>
#include <memory>
#include <string>
#include <vector>

#include "../../src/CopyMasterShell/Clsids.h"

using Microsoft::WRL::ClassicCom;
using Microsoft::WRL::ComPtr;
using Microsoft::WRL::Make;
using Microsoft::WRL::RuntimeClass;
using Microsoft::WRL::RuntimeClassFlags;

struct __declspec(uuid(COPYMASTER_DRAGDROP_CLSID)) CopyMasterDragDrop;

namespace
{

constexpr UINT kFirstId = 100;

std::string Utf8(const std::wstring& text)
{
    if (text.empty()) return {};
    int size = WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    std::string utf8(static_cast<size_t>(size), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), utf8.data(), size, nullptr, nullptr);
    return utf8;
}

void Print(const std::wstring& line)
{
    std::string text = Utf8(line) + "\n";
    fwrite(text.data(), 1, text.size(), stdout);
    fflush(stdout);
}

std::wstring Hex(unsigned long value)
{
    wchar_t text[16];
    swprintf_s(text, L"0x%08lX", value);
    return text;
}

// PIDLs are __unaligned pointers on x64, so the owner holds that type rather than ITEMIDLIST*.
struct PidlFree
{
    using pointer = PIDLIST_ABSOLUTE;
    void operator()(PIDLIST_ABSOLUTE pidl) const noexcept { CoTaskMemFree(pidl); }
};
using Pidl = std::unique_ptr<ITEMIDLIST, PidlFree>;

// A data object holding CF_HDROP (or nothing), like a drag from Explorer's file view.
class FilesData final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IDataObject>
{
public:
    FilesData(std::vector<std::wstring> paths, bool hdrop) : m_paths(std::move(paths)), m_hdrop(hdrop) {}

    IFACEMETHODIMP GetData(FORMATETC* format, STGMEDIUM* medium) override
    {
        if (format == nullptr || medium == nullptr) return E_INVALIDARG;
        *medium = {};
        HRESULT result = QueryGetData(format);
        if (result != S_OK) return result;

        size_t chars = 1; // the list ends with an empty string
        for (const auto& path : m_paths) chars += path.size() + 1;
        HGLOBAL global = GlobalAlloc(GHND, sizeof(DROPFILES) + chars * sizeof(wchar_t));
        if (global == nullptr) return E_OUTOFMEMORY;
        auto drop = static_cast<DROPFILES*>(GlobalLock(global));
        drop->pFiles = sizeof(DROPFILES);
        drop->fWide = TRUE;
        auto out = reinterpret_cast<wchar_t*>(reinterpret_cast<BYTE*>(drop) + sizeof(DROPFILES));
        for (const auto& path : m_paths)
        {
            memcpy(out, path.c_str(), (path.size() + 1) * sizeof(wchar_t));
            out += path.size() + 1;
        }
        *out = L'\0';
        GlobalUnlock(global);

        medium->tymed = TYMED_HGLOBAL;
        medium->hGlobal = global;
        return S_OK;
    }

    IFACEMETHODIMP GetDataHere(FORMATETC*, STGMEDIUM*) override { return E_NOTIMPL; }

    IFACEMETHODIMP QueryGetData(FORMATETC* format) override
    {
        if (format == nullptr) return E_INVALIDARG;
        if (!m_hdrop || format->cfFormat != CF_HDROP) return DV_E_FORMATETC;
        if (!(format->tymed & TYMED_HGLOBAL)) return DV_E_TYMED;
        if (format->dwAspect != DVASPECT_CONTENT) return DV_E_DVASPECT;
        return S_OK;
    }

    IFACEMETHODIMP GetCanonicalFormatEtc(FORMATETC*, FORMATETC* out) override
    {
        if (out != nullptr) out->ptd = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP SetData(FORMATETC*, STGMEDIUM*, BOOL) override { return E_NOTIMPL; }

    IFACEMETHODIMP EnumFormatEtc(DWORD direction, IEnumFORMATETC** formats) override
    {
        if (formats == nullptr) return E_INVALIDARG;
        *formats = nullptr;
        if (direction != DATADIR_GET) return E_NOTIMPL;
        FORMATETC format{CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
        return SHCreateStdEnumFmtEtc(m_hdrop ? 1 : 0, &format, formats);
    }

    IFACEMETHODIMP DAdvise(FORMATETC*, DWORD, IAdviseSink*, DWORD*) override { return OLE_E_ADVISENOTSUPPORTED; }
    IFACEMETHODIMP DUnadvise(DWORD) override { return OLE_E_ADVISENOTSUPPORTED; }
    IFACEMETHODIMP EnumDAdvise(IEnumSTATDATA**) override { return OLE_E_ADVISENOTSUPPORTED; }

private:
    std::vector<std::wstring> m_paths;
    bool m_hdrop;
};

HRESULT ShellDataObject(const std::vector<std::wstring>& sources, IDataObject** data)
{
    std::vector<Pidl> owned;
    std::vector<PCIDLIST_ABSOLUTE> pidls;
    for (const auto& source : sources)
    {
        PIDLIST_ABSOLUTE pidl = nullptr;
        HRESULT result = SHParseDisplayName(source.c_str(), nullptr, &pidl, 0, nullptr);
        if (FAILED(result)) return result;
        owned.emplace_back(pidl);
        pidls.push_back(pidl);
    }
    ComPtr<IShellItemArray> items;
    HRESULT result = SHCreateShellItemArrayFromIDLists(static_cast<UINT>(pidls.size()), pidls.data(), &items);
    if (FAILED(result)) return result;
    return items->BindToHandler(nullptr, BHID_DataObject, IID_PPV_ARGS(data));
}

HRESULT ParseTarget(const std::wstring& target, Pidl& pidl)
{
    PIDLIST_ABSOLUTE parsed = nullptr;
    HRESULT result = _wcsicmp(target.c_str(), L"desktop") == 0
                         ? SHGetSpecialFolderLocation(nullptr, CSIDL_DESKTOP, &parsed) // the empty, root PIDL
                         : SHParseDisplayName(target.c_str(), nullptr, &parsed, 0, nullptr);
    pidl.reset(parsed);
    return result;
}

void DescribeTarget(PCIDLIST_ABSOLUTE pidl)
{
    ComPtr<IShellItem> item;
    SFGAOF attributes = 0;
    std::wstring path = L"-";
    if (SUCCEEDED(SHCreateItemFromIDList(pidl, IID_PPV_ARGS(&item))))
    {
        item->GetAttributes(SFGAO_FILESYSTEM | SFGAO_FOLDER | SFGAO_STREAM | SFGAO_FILESYSANCESTOR, &attributes);
        PWSTR name = nullptr;
        if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &name)))
        {
            path = name;
            CoTaskMemFree(name);
        }
    }
    Print(L"target " + Hex(attributes) + L" " + path);
}

HRESULT LoadHandler(const std::wstring& dll, IShellExtInit** handler)
{
    HMODULE module = LoadLibraryExW(dll.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (module == nullptr) return HRESULT_FROM_WIN32(GetLastError());
    auto getClassObject = reinterpret_cast<LPFNGETCLASSOBJECT>(GetProcAddress(module, "DllGetClassObject"));
    if (getClassObject == nullptr) return HRESULT_FROM_WIN32(GetLastError());
    ComPtr<IClassFactory> factory;
    HRESULT result = getClassObject(__uuidof(CopyMasterDragDrop), IID_PPV_ARGS(&factory));
    if (FAILED(result)) return result;
    return factory->CreateInstance(nullptr, IID_PPV_ARGS(handler));
}

enum class InvokeBy
{
    Offset,
    Verb,
    VerbAnsi,
};

struct Options
{
    std::wstring dll, target, verb;
    bool invoke = false, hdrop = true, shellData = false, ui = false;
    InvokeBy by = InvokeBy::Offset;
    std::vector<std::wstring> sources;
};

int RunHandler(const Options& options)
{
    ComPtr<IShellExtInit> handler;
    HRESULT result = LoadHandler(options.dll, &handler);
    if (FAILED(result))
    {
        Print(L"error loading the handler " + Hex(static_cast<unsigned long>(result)));
        return 1;
    }

    Pidl target;
    result = ParseTarget(options.target, target);
    if (FAILED(result))
    {
        Print(L"error parsing the target " + Hex(static_cast<unsigned long>(result)));
        return 1;
    }
    DescribeTarget(target.get());

    ComPtr<IDataObject> data;
    result = options.shellData ? ShellDataObject(options.sources, &data)
                               : Make<FilesData>(options.sources, options.hdrop).CopyTo(data.GetAddressOf());
    if (FAILED(result))
    {
        Print(L"error making the data object " + Hex(static_cast<unsigned long>(result)));
        return 1;
    }

    result = handler->Initialize(target.get(), data.Get(), nullptr);
    Print(L"initialize " + Hex(static_cast<unsigned long>(result)));
    if (FAILED(result)) return 1;

    ComPtr<IContextMenu> menu;
    result = handler.As(&menu);
    if (FAILED(result)) return 1;
    HMENU popup = CreatePopupMenu();
    result = menu->QueryContextMenu(popup, 0, kFirstId, 0x7FFF, CMF_NORMAL);
    Print(L"query " + Hex(static_cast<unsigned long>(result)));
    if (FAILED(result)) return 1;

    int found = -1;
    int count = GetMenuItemCount(popup);
    for (int i = 0; i < count; ++i)
    {
        UINT offset = GetMenuItemID(popup, i) - kFirstId;
        wchar_t text[256]{};
        GetMenuStringW(popup, static_cast<UINT>(i), text, ARRAYSIZE(text), MF_BYPOSITION);
        wchar_t verb[256]{};
        menu->GetCommandString(offset, GCS_VERBW, nullptr, reinterpret_cast<LPSTR>(verb), ARRAYSIZE(verb));
        Print(L"item " + std::to_wstring(offset) + L" " + verb + L" " + text);
        if (options.invoke && options.verb == verb) found = static_cast<int>(offset);
    }
    DestroyMenu(popup);
    if (!options.invoke) return 0;
    if (found < 0)
    {
        Print(L"error no item has the verb " + options.verb);
        return 1;
    }

    std::string verbAnsi = Utf8(options.verb);
    CMINVOKECOMMANDINFOEX info{};
    info.cbSize = sizeof(info);
    info.fMask = options.ui ? 0 : CMIC_MASK_FLAG_NO_UI;
    info.nShow = SW_SHOWNORMAL;
    switch (options.by)
    {
    case InvokeBy::Offset:
        info.fMask |= CMIC_MASK_UNICODE;
        info.lpVerb = MAKEINTRESOURCEA(found);
        info.lpVerbW = MAKEINTRESOURCEW(found);
        break;
    case InvokeBy::Verb:
        info.fMask |= CMIC_MASK_UNICODE;
        info.lpVerb = verbAnsi.c_str();
        info.lpVerbW = options.verb.c_str();
        break;
    case InvokeBy::VerbAnsi:
        info.cbSize = sizeof(CMINVOKECOMMANDINFO); // the old structure: ANSI only
        info.lpVerb = verbAnsi.c_str();
        break;
    }
    result = menu->InvokeCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&info));
    Print(L"invoke " + Hex(static_cast<unsigned long>(result)));
    return result == S_OK ? 0 : 1;
}

int CoCreate(const std::wstring& clsidText)
{
    CLSID clsid{};
    HRESULT result = CLSIDFromString(clsidText.c_str(), &clsid);
    if (FAILED(result))
    {
        Print(L"error not a CLSID: " + clsidText);
        return 2;
    }
    ComPtr<IUnknown> object;
    result = CoCreateInstance(clsid, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&object));
    Print(L"cocreate " + Hex(static_cast<unsigned long>(result)));
    return SUCCEEDED(result) ? 0 : 1;
}

bool WriteUtf8File(const std::wstring& path, const std::wstring& text)
{
    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    std::string utf8 = Utf8(text);
    DWORD written = 0;
    BOOL ok = WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
    CloseHandle(file);
    return ok && written == utf8.size();
}

// As CopyMaster.exe: keep what we were given, for the test to read, out of the real jobs folder.
int FakeEngine(const std::wstring& jobPath)
{
    std::wstring self(32768, L'\0');
    self.resize(GetModuleFileNameW(nullptr, self.data(), static_cast<DWORD>(self.size())));
    std::wstring folder = self.substr(0, self.find_last_of(L'\\')) + L"\\received";
    CreateDirectoryW(folder.c_str(), nullptr);
    std::wstring target = folder + L"\\" + jobPath.substr(jobPath.find_last_of(L'\\') + 1);
    // The command line first: once the job file shows up, the test reads both.
    if (!WriteUtf8File(target + L".cmdline", GetCommandLineW())) return 1;
    return MoveFileExW(jobPath.c_str(), target.c_str(), MOVEFILE_COPY_ALLOWED | MOVEFILE_REPLACE_EXISTING) ? 0 : 1;
}

int Usage()
{
    Print(L"error usage: ShellHost menu|invoke --dll <dll> --target <target> [--verb <verb>] [--by-verb|--by-verb-ansi] "
          L"[--ui] [--no-hdrop|--shell-data] <source>... | cocreate <clsid> | --job <file>");
    return 2;
}

} // namespace

int wmain(int argc, wchar_t** argv)
{
    if (argc == 3 && wcscmp(argv[1], L"--job") == 0) return FakeEngine(argv[2]);
    if (argc < 2) return Usage();

    if (FAILED(OleInitialize(nullptr))) return 1; // an STA with OLE, like Explorer's UI thread
    int exitCode = 2;
    std::wstring command = argv[1];
    if (command == L"cocreate" && argc == 3)
    {
        exitCode = CoCreate(argv[2]);
    }
    else if (command == L"menu" || command == L"invoke")
    {
        Options options;
        options.invoke = command == L"invoke";
        bool ok = true;
        for (int i = 2; i < argc && ok; ++i)
        {
            std::wstring arg = argv[i];
            auto value = [&](std::wstring& out) {
                ok = ++i < argc;
                if (ok) out = argv[i];
            };
            if (arg == L"--dll") value(options.dll);
            else if (arg == L"--target") value(options.target);
            else if (arg == L"--verb") value(options.verb);
            else if (arg == L"--by-verb") options.by = InvokeBy::Verb;
            else if (arg == L"--by-verb-ansi") options.by = InvokeBy::VerbAnsi;
            else if (arg == L"--ui") options.ui = true;
            else if (arg == L"--no-hdrop") options.hdrop = false;
            else if (arg == L"--shell-data") options.shellData = true;
            else if (arg.rfind(L"--", 0) == 0) ok = false;
            else options.sources.push_back(arg);
        }
        ok = ok && !options.dll.empty() && !options.target.empty() && (!options.invoke || !options.verb.empty());
        exitCode = ok ? RunHandler(options) : Usage();
    }
    else
    {
        exitCode = Usage();
    }
    OleUninitialize();
    return exitCode;
}
