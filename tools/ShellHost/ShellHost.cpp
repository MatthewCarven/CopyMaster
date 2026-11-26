// ShellHost: drives CopyMasterShell.dll's handlers the way Explorer does, without Explorer, so the
// DLL can be tested without restarting Explorer. It also stands in for CopyMaster.exe.
//
//   ShellHost menu   --dll <dll> --target <target> [data] <source>...
//       The right-drag handler: loads the DLL (no registry), Initialize + QueryContextMenu; prints the
//       items added.
//   ShellHost invoke --dll <dll> --target <target> --verb <verb> [--by-verb|--by-verb-ansi] [--ui] [data] <source>...
//       ...then InvokeCommand on the item with that verb: by its menu offset, as Explorer does for
//       a click, or by the verb itself (Unicode or ANSI). No message boxes unless --ui.
//   ShellHost menu|invoke --paste --dll <dll> --target <target>... [--background] --clip <what> [paste options] <file>...
//       The right-click Paste handler, with <file>... on the clipboard. Without --background, as a
//       right-click on <target> (a folder or drive; give --target twice for two selected); with it, as
//       a right-click on <target>'s background. It changes the clipboard, so it runs only on a private
//       window station: through `ShellHost isolated`.
//         --clip copy|cut     the files, as Explorer's Copy or Cut leaves them ("Preferred DropEffect")
//         --clip text|empty   no files: some text, or nothing at all
//         --clip hung         files promised by a window that never answers (delayed rendering)
//         --shell-data        the clipboard holds the shell's own data object for the files
//         --clip-before-invoke text   the clipboard changes between the menu and the click
//         --clip-after-invoke text    ...or after the click, before the engine has finished
//         --wait-engine       waits for the job to run (see --job) and prints its exit code
//   ShellHost menu|invoke --command --dll <dll> --target <target>... [--background [--empty-items]]
//                         [--site <target>] [--site-shellfolder] --clip <what> [paste options] <file>...
//       Windows 11's compact-menu Paste (an IExplorerCommand), as Explorer drives it: SetSite, GetState,
//       GetTitle, GetIcon, ...; then Invoke. Without --background the selection is <target>...; with it
//       there is none (null, or with --empty-items an empty array), as for a right-click on a background,
//       and the site's folder view shows <target>. --site gives the site a folder of its own;
//       --site-shellfolder makes its view hand out only the folder's IShellFolder, not an IShellItem.
//       Like --paste, only on a private window station.
//   ShellHost isolated <command>...
//       Runs `ShellHost <command>...` on a window station and desktop of its own, so whatever it does
//       to the clipboard (the window station's) stays there, and waits for it.
//   ShellHost cocreate <clsid> [inproc|local]
//       CoCreateInstance through the registry (in-process by default; local: out of process, as a
//       packaged surrogate class), to see which registrations COM honours here. For an IExplorerCommand
//       (out of the way: no selection, no site) it prints its title and state too.
//   ShellHost --job <file>
//       Pretends to be CopyMaster.exe (copy ShellHost.exe as CopyMaster.exe beside the DLL): writes
//       its command line to .\received\<name>.cmdline beside itself, then moves the job file there.
//       If CM_SHELLHOST_ENGINE names a real engine (cmcopy.exe), it then waits for <name>.go (from
//       --wait-engine), runs `<engine> --job <name> --quiet` with its output in <name>.log, and writes
//       its exit code to <name>.engine.
//
// <target>  a folder path; a parsing name such as ::{20D04FE0-3AEA-1069-A2D8-08002B30309D} (This PC);
//           or "desktop", the root of the namespace, as Explorer's desktop window has it.
// [data]    (right-drag) the data object Initialize gets:
//           (default) one holding CF_HDROP with exactly <source>..., in order
//           --no-hdrop    one with no file paths (as a phone, or a zip's contents, give)
//           --shell-data  the shell's own data object for <source>... (what an Explorer drag carries)
//
// Prints, one per line (UTF-8): "target <SFGAO hex> <file-system path or ->", "initialize <hr>",
// "query <hr>", "item <offset> <verb> <text>", "bitmap <offset> <width>x<height> <bits a pixel>" (the
// picture beside that item), "state <hr> hidden|enabled|other", "title <text>",
// "icon <location>", "icons <count in that file>", "canonical <guid>", "flags <hex>", "invoke <hr>",
// "took <ms>" (the invoke), "dialog owned|unowned <text, lines joined by ' | '>" (a message box, read
// and answered OK), "clipboard <sequence number> files|text|empty|other", "engine <exit code>",
// "cocreate <hr>", "error <text>". Exit code: 0 if everything asked for succeeded, 1 if not, 2 for a bad
// command line.

#include <windows.h>
#include <oleidl.h>
#include <shellapi.h>
#include <shlobj.h>
#include <wrl/client.h>
#include <wrl/implements.h>

#include <cstdio>
#include <cstring>
#include <functional>
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
struct __declspec(uuid(COPYMASTER_PASTE_CLSID)) CopyMasterPaste;
struct __declspec(uuid(COPYMASTER_PASTE_COMMAND_CLSID)) CopyMasterPasteCommand;

namespace
{

constexpr UINT kFirstId = 100;
constexpr wchar_t kStationPrefix[] = L"CopyMasterTest.";

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

std::wstring Hex(HRESULT value)
{
    return Hex(static_cast<unsigned long>(value));
}

std::wstring ExePath()
{
    std::wstring self(32768, L'\0');
    self.resize(GetModuleFileNameW(nullptr, self.data(), static_cast<DWORD>(self.size())));
    return self;
}

std::wstring FolderOf(const std::wstring& path)
{
    size_t slash = path.find_last_of(L'\\');
    return slash == std::wstring::npos ? std::wstring(L".") : path.substr(0, slash);
}

bool Exists(const std::wstring& path)
{
    return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES;
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

// Written whole or not at all, for a reader that polls for it.
bool PublishUtf8File(const std::wstring& path, const std::wstring& text)
{
    std::wstring temporary = path + L".tmp";
    return WriteUtf8File(temporary, text) && MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING);
}

bool ReadSmallFile(const std::wstring& path, std::wstring& text)
{
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    char buffer[256]{};
    DWORD read = 0;
    BOOL ok = ReadFile(file, buffer, sizeof(buffer) - 1, &read, nullptr);
    CloseHandle(file);
    if (!ok) return false;
    text.assign(buffer, buffer + read); // ASCII: an exit code
    return true;
}

// One argument for a command line, quoted so CommandLineToArgvW (and the CRT) gives it back unchanged.
std::wstring QuoteArgument(const std::wstring& argument)
{
    if (!argument.empty() && argument.find_first_of(L" \t\n\v\"") == std::wstring::npos) return argument;
    std::wstring quoted = L"\"";
    for (size_t i = 0;; ++i)
    {
        size_t backslashes = 0;
        while (i < argument.size() && argument[i] == L'\\')
        {
            ++i;
            ++backslashes;
        }
        if (i == argument.size())
        {
            quoted.append(backslashes * 2, L'\\'); // before the closing quote
            break;
        }
        if (argument[i] == L'"') quoted.append(backslashes * 2 + 1, L'\\');
        else quoted.append(backslashes, L'\\');
        quoted += argument[i];
    }
    return quoted + L"\"";
}

// Runs the message loop until `done` says so or `timeoutMs` passes. The clipboard's owner (OLE's
// window, when ShellHost put files there) has to answer while ShellHost waits.
bool PumpUntil(const std::function<bool()>& done, DWORD timeoutMs)
{
    ULONGLONG deadline = GetTickCount64() + timeoutMs;
    while (!done())
    {
        if (GetTickCount64() >= deadline) return false;
        MsgWaitForMultipleObjects(0, nullptr, FALSE, 20, QS_ALLINPUT);
        MSG message;
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
        {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }
    return true;
}

// PIDLs are __unaligned pointers on x64, so the owner holds that type rather than ITEMIDLIST*.
struct PidlFree
{
    using pointer = PIDLIST_ABSOLUTE;
    void operator()(PIDLIST_ABSOLUTE pidl) const noexcept { CoTaskMemFree(pidl); }
};
using Pidl = std::unique_ptr<ITEMIDLIST, PidlFree>;

CLIPFORMAT PreferredDropEffectFormat()
{
    return static_cast<CLIPFORMAT>(RegisterClipboardFormatW(CFSTR_PREFERREDDROPEFFECT));
}

HGLOBAL DwordGlobal(DWORD value)
{
    HGLOBAL global = GlobalAlloc(GHND, sizeof(DWORD));
    if (global == nullptr) return nullptr;
    *static_cast<DWORD*>(GlobalLock(global)) = value;
    GlobalUnlock(global);
    return global;
}

// A data object holding CF_HDROP (or nothing), like a drag from Explorer's file view; and, with an
// effect, "Preferred DropEffect", like Explorer's Copy or Cut.
class FilesData final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IDataObject>
{
public:
    FilesData(std::vector<std::wstring> paths, bool hdrop, DWORD effect = 0)
        : m_paths(std::move(paths)), m_hdrop(hdrop), m_effect(effect)
    {
    }

    IFACEMETHODIMP GetData(FORMATETC* format, STGMEDIUM* medium) override
    {
        if (format == nullptr || medium == nullptr) return E_INVALIDARG;
        *medium = {};
        HRESULT result = QueryGetData(format);
        if (result != S_OK) return result;
        HGLOBAL global = format->cfFormat == CF_HDROP ? DropGlobal() : DwordGlobal(m_effect);
        if (global == nullptr) return E_OUTOFMEMORY;
        medium->tymed = TYMED_HGLOBAL;
        medium->hGlobal = global;
        return S_OK;
    }

    IFACEMETHODIMP GetDataHere(FORMATETC*, STGMEDIUM*) override { return E_NOTIMPL; }

    IFACEMETHODIMP QueryGetData(FORMATETC* format) override
    {
        if (format == nullptr) return E_INVALIDARG;
        bool offered = (m_hdrop && format->cfFormat == CF_HDROP) ||
                       (m_effect != 0 && format->cfFormat == PreferredDropEffectFormat());
        if (!offered) return DV_E_FORMATETC;
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
        FORMATETC offered[2]{};
        UINT count = 0;
        if (m_hdrop) offered[count++] = {CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
        if (m_effect != 0) offered[count++] = {PreferredDropEffectFormat(), nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
        return SHCreateStdEnumFmtEtc(count, offered, formats);
    }

    IFACEMETHODIMP DAdvise(FORMATETC*, DWORD, IAdviseSink*, DWORD*) override { return OLE_E_ADVISENOTSUPPORTED; }
    IFACEMETHODIMP DUnadvise(DWORD) override { return OLE_E_ADVISENOTSUPPORTED; }
    IFACEMETHODIMP EnumDAdvise(IEnumSTATDATA**) override { return OLE_E_ADVISENOTSUPPORTED; }

private:
    HGLOBAL DropGlobal() const
    {
        size_t chars = 1; // the list ends with an empty string
        for (const auto& path : m_paths) chars += path.size() + 1;
        HGLOBAL global = GlobalAlloc(GHND, sizeof(DROPFILES) + chars * sizeof(wchar_t));
        if (global == nullptr) return nullptr;
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
        return global;
    }

    std::vector<std::wstring> m_paths;
    bool m_hdrop;
    DWORD m_effect;
};

HRESULT ShellDataObject(const std::vector<std::wstring>& paths, IDataObject** data)
{
    std::vector<Pidl> owned;
    std::vector<PCIDLIST_ABSOLUTE> pidls;
    for (const auto& path : paths)
    {
        PIDLIST_ABSOLUTE pidl = nullptr;
        HRESULT result = SHParseDisplayName(path.c_str(), nullptr, &pidl, 0, nullptr);
        if (FAILED(result)) return result;
        owned.emplace_back(pidl);
        pidls.push_back(pidl);
    }
    ComPtr<IShellItemArray> items;
    HRESULT result = SHCreateShellItemArrayFromIDLists(static_cast<UINT>(pidls.size()), pidls.data(), &items);
    if (FAILED(result)) return result;
    return items->BindToHandler(nullptr, BHID_DataObject, IID_PPV_ARGS(data));
}

// The folder view Explorer's site hands an IExplorerCommand (QueryService(SID_SFolderView)): only
// GetFolder does anything. Explorer's own view gives the folder as an IShellItem; with `shellFolderOnly`,
// only as the folder's IShellFolder (and what that implements, such as IPersistFolder2).
class FakeFolderView final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IFolderView>
{
public:
    FakeFolderView(PCIDLIST_ABSOLUTE folder, bool shellFolderOnly) : m_folder(ILCloneFull(folder)), m_shellFolderOnly(shellFolderOnly)
    {
    }

    IFACEMETHODIMP GetFolder(REFIID iid, void** out) override
    {
        if (out == nullptr) return E_POINTER;
        *out = nullptr;
        if (!m_folder) return E_FAIL;
        if (iid == __uuidof(IShellItem)) return m_shellFolderOnly ? E_NOINTERFACE : SHCreateItemFromIDList(m_folder.get(), iid, out);
        ComPtr<IShellFolder> folder;
        HRESULT result = ILIsEmpty(m_folder.get()) ? SHGetDesktopFolder(&folder)
                                                   : SHBindToObject(nullptr, m_folder.get(), nullptr, IID_PPV_ARGS(&folder));
        if (FAILED(result)) return result;
        return folder.CopyTo(iid, out);
    }

    IFACEMETHODIMP GetCurrentViewMode(UINT*) override { return E_NOTIMPL; }
    IFACEMETHODIMP SetCurrentViewMode(UINT) override { return E_NOTIMPL; }
    IFACEMETHODIMP Item(int, PITEMID_CHILD*) override { return E_NOTIMPL; }
    IFACEMETHODIMP ItemCount(UINT, int*) override { return E_NOTIMPL; }
    IFACEMETHODIMP Items(UINT, REFIID, void**) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetSelectionMarkedItem(int*) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetFocusedItem(int*) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetItemPosition(PCUITEMID_CHILD, POINT*) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetSpacing(POINT*) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetDefaultSpacing(POINT*) override { return E_NOTIMPL; }
    IFACEMETHODIMP GetAutoArrange() override { return E_NOTIMPL; }
    IFACEMETHODIMP SelectItem(int, DWORD) override { return E_NOTIMPL; }
    IFACEMETHODIMP SelectAndPositionItems(UINT, PCUITEMID_CHILD_ARRAY, POINT*, DWORD) override { return E_NOTIMPL; }

private:
    Pidl m_folder;
    bool m_shellFolderOnly;
};

// An empty selection, which Explorer sometimes hands a command on a background instead of none.
class EmptyItems final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IShellItemArray>
{
public:
    IFACEMETHODIMP BindToHandler(IBindCtx*, REFGUID, REFIID, void** out) override
    {
        if (out != nullptr) *out = nullptr;
        return E_NOTIMPL;
    }
    IFACEMETHODIMP GetPropertyStore(GETPROPERTYSTOREFLAGS, REFIID, void** out) override
    {
        if (out != nullptr) *out = nullptr;
        return E_NOTIMPL;
    }
    IFACEMETHODIMP GetPropertyDescriptionList(REFPROPERTYKEY, REFIID, void** out) override
    {
        if (out != nullptr) *out = nullptr;
        return E_NOTIMPL;
    }
    IFACEMETHODIMP GetAttributes(SIATTRIBFLAGS, SFGAOF, SFGAOF* attributes) override
    {
        if (attributes != nullptr) *attributes = 0;
        return E_NOTIMPL;
    }
    IFACEMETHODIMP GetCount(DWORD* count) override
    {
        if (count == nullptr) return E_POINTER;
        *count = 0;
        return S_OK;
    }
    IFACEMETHODIMP GetItemAt(DWORD, IShellItem** item) override
    {
        if (item != nullptr) *item = nullptr;
        return E_INVALIDARG;
    }
    IFACEMETHODIMP EnumItems(IEnumShellItems** items) override
    {
        if (items != nullptr) *items = nullptr;
        return E_NOTIMPL;
    }
};

// Explorer's site for a menu command: a service provider with the folder view, and the window
// (IOleWindow) that would own a message box. The window is a hidden one of ShellHost's own.
class FakeSite final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IServiceProvider, IOleWindow>
{
public:
    FakeSite(PCIDLIST_ABSOLUTE folder, bool shellFolderOnly, HWND window)
        : m_view(Make<FakeFolderView>(folder, shellFolderOnly)), m_window(window)
    {
    }

    IFACEMETHODIMP QueryService(REFGUID service, REFIID iid, void** out) override
    {
        if (out == nullptr) return E_POINTER;
        *out = nullptr;
        if (service != SID_SFolderView || !m_view) return E_NOINTERFACE;
        return m_view.CopyTo(iid, out);
    }

    IFACEMETHODIMP GetWindow(HWND* window) override
    {
        if (window == nullptr) return E_POINTER;
        *window = m_window;
        return m_window != nullptr ? S_OK : E_FAIL;
    }

    IFACEMETHODIMP ContextSensitiveHelp(BOOL) override { return E_NOTIMPL; }

private:
    ComPtr<FakeFolderView> m_view;
    HWND m_window;
};

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

// Loads the DLL straight from its file (no registry) and makes one of its classes.
HRESULT LoadClass(const std::wstring& dll, REFCLSID clsid, REFIID iid, void** object)
{
    HMODULE module = LoadLibraryExW(dll.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (module == nullptr) return HRESULT_FROM_WIN32(GetLastError());
    auto getClassObject = reinterpret_cast<LPFNGETCLASSOBJECT>(GetProcAddress(module, "DllGetClassObject"));
    if (getClassObject == nullptr) return HRESULT_FROM_WIN32(GetLastError());
    ComPtr<IClassFactory> factory;
    HRESULT result = getClassObject(clsid, IID_PPV_ARGS(&factory));
    if (FAILED(result)) return result;
    return factory->CreateInstance(nullptr, iid, object);
}

HRESULT LoadHandler(const std::wstring& dll, REFCLSID clsid, IShellExtInit** handler)
{
    return LoadClass(dll, clsid, IID_PPV_ARGS(handler));
}

// ---- Message boxes --------------------------------------------------------------------------------

// Watches the thread that's invoking a command for message boxes, as a person would: reads each one's
// text, notes whether it has an owner, and presses OK. A message box on a private window station has
// nobody to answer it, and the compact menu's Paste has no "no UI" flag to keep it quiet.
class DialogWatcher
{
public:
    explicit DialogWatcher(HWND expectedOwner) : m_thread(GetCurrentThreadId()), m_owner(expectedOwner)
    {
        m_stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        m_watching = CreateThread(nullptr, 0, Watch, this, 0, nullptr);
    }
    DialogWatcher(const DialogWatcher&) = delete;
    DialogWatcher& operator=(const DialogWatcher&) = delete;
    ~DialogWatcher() { Stop(); }

    // Stops watching, then prints what it saw.
    void Stop()
    {
        if (m_watching == nullptr) return;
        SetEvent(m_stop);
        WaitForSingleObject(m_watching, INFINITE);
        CloseHandle(m_watching);
        CloseHandle(m_stop);
        m_watching = nullptr;
        for (const auto& line : m_seen) Print(line);
    }

private:
    static DWORD WINAPI Watch(void* parameter)
    {
        auto self = static_cast<DialogWatcher*>(parameter);
        while (WaitForSingleObject(self->m_stop, 25) == WAIT_TIMEOUT)
        {
            EnumThreadWindows(self->m_thread, Found, reinterpret_cast<LPARAM>(self));
        }
        return 0;
    }

    static BOOL CALLBACK Found(HWND window, LPARAM parameter)
    {
        auto self = reinterpret_cast<DialogWatcher*>(parameter);
        // A dialog (#32770) with a message box's text control. Not necessarily visible: a dialog shows
        // itself once its thread's queue first runs dry, which on a private desktop may take a while.
        wchar_t type[32]{};
        GetClassNameW(window, type, ARRAYSIZE(type));
        if (wcscmp(type, L"#32770") != 0 || GetDlgItem(window, 0xFFFF) == nullptr) return TRUE;
        for (HWND answered : self->m_answered)
        {
            if (answered == window) return TRUE;
        }
        self->m_answered.push_back(window);
        wchar_t text[2048]{};
        DWORD_PTR copied = 0;
        SendMessageTimeoutW(GetDlgItem(window, 0xFFFF), WM_GETTEXT, ARRAYSIZE(text), reinterpret_cast<LPARAM>(text),
                            SMTO_ABORTIFHUNG, 2000, &copied);
        std::wstring flat;
        for (const wchar_t* c = text; *c != L'\0'; ++c)
        {
            if (*c == L'\r') continue;
            if (*c == L'\n') flat += L" | ";
            else flat += *c;
        }
        HWND owner = GetWindow(window, GW_OWNER);
        const wchar_t* whose = owner == nullptr ? L"unowned" : owner == self->m_owner ? L"owned" : L"owned-elsewhere";
        self->m_seen.push_back(std::wstring(L"dialog ") + whose + L" " + flat);
        // Closed, as Esc would: an OK-only box then returns IDOK. (A WM_COMMAND for IDOK, posted or sent,
        // left it open here, on a desktop where it had never been shown.)
        PostMessageW(window, WM_CLOSE, 0, 0);
        return TRUE;
    }

    DWORD m_thread;
    HWND m_owner;
    HANDLE m_stop = nullptr;
    HANDLE m_watching = nullptr;
    std::vector<HWND> m_answered;
    std::vector<std::wstring> m_seen;
};

// ---- The clipboard (only ever on a private window station) ----------------------------------------

bool OnPrivateStation()
{
    wchar_t name[256]{};
    DWORD needed = 0;
    if (!GetUserObjectInformationW(GetProcessWindowStation(), UOI_NAME, name, sizeof(name) - sizeof(wchar_t), &needed))
    {
        return false;
    }
    return wcsncmp(name, kStationPrefix, wcslen(kStationPrefix)) == 0;
}

HWND MakeMessageWindow()
{
    return CreateWindowExW(0, L"STATIC", L"ShellHost", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, nullptr, nullptr);
}

// Owns what ShellHost puts on the clipboard itself (text, or nothing).
HWND ClipboardWindow()
{
    static HWND window = MakeMessageWindow();
    return window;
}

bool OpenClipboardPatiently(HWND owner)
{
    for (int i = 0; i < 50; ++i)
    {
        if (OpenClipboard(owner)) return true;
        Sleep(10);
    }
    return false;
}

HRESULT PutText(const std::wstring& text)
{
    if (!OpenClipboardPatiently(ClipboardWindow())) return HRESULT_FROM_WIN32(GetLastError());
    EmptyClipboard();
    size_t bytes = (text.size() + 1) * sizeof(wchar_t);
    HGLOBAL global = GlobalAlloc(GMEM_MOVEABLE, bytes);
    bool ok = global != nullptr;
    if (ok)
    {
        memcpy(GlobalLock(global), text.c_str(), bytes);
        GlobalUnlock(global);
        ok = SetClipboardData(CF_UNICODETEXT, global) != nullptr;
        if (!ok) GlobalFree(global);
    }
    CloseClipboard();
    return ok ? S_OK : E_FAIL;
}

HRESULT PutNothing()
{
    if (!OpenClipboardPatiently(ClipboardWindow())) return HRESULT_FROM_WIN32(GetLastError());
    BOOL ok = EmptyClipboard();
    CloseClipboard();
    return ok ? S_OK : HRESULT_FROM_WIN32(GetLastError());
}

// Copy or Cut: OLE puts a promise of each format on the clipboard and produces it when asked
// (WM_RENDERFORMAT to its window on this thread), as Explorer's Copy and Cut do.
HRESULT PutFiles(const std::vector<std::wstring>& paths, DWORD effect, bool shellData)
{
    ComPtr<IDataObject> data;
    if (shellData)
    {
        HRESULT result = ShellDataObject(paths, &data);
        if (FAILED(result)) return result;
        FORMATETC format{PreferredDropEffectFormat(), nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
        STGMEDIUM medium{};
        medium.tymed = TYMED_HGLOBAL;
        medium.hGlobal = DwordGlobal(effect);
        result = data->SetData(&format, &medium, TRUE); // as Explorer's Cut marks its data object
        if (FAILED(result))
        {
            ReleaseStgMedium(&medium);
            return result;
        }
    }
    else
    {
        HRESULT result = Make<FilesData>(paths, true, effect).CopyTo(data.GetAddressOf());
        if (FAILED(result)) return result;
    }
    return OleSetClipboard(data.Get());
}

DWORD WINAPI HungOwner(void* parameter)
{
    HWND window = MakeMessageWindow();
    if (window != nullptr && OpenClipboardPatiently(window))
    {
        EmptyClipboard();
        SetClipboardData(CF_HDROP, nullptr); // promised, never produced
        CloseClipboard();
    }
    SetEvent(static_cast<HANDLE>(parameter));
    Sleep(INFINITE); // never reads its messages, so WM_RENDERFORMAT never comes back
    return 0;
}

// A window on another thread owns the clipboard and has promised files, but never answers.
HRESULT PutHung()
{
    HANDLE ready = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (ready == nullptr) return HRESULT_FROM_WIN32(GetLastError());
    HANDLE thread = CreateThread(nullptr, 0, HungOwner, ready, 0, nullptr);
    if (thread == nullptr) return HRESULT_FROM_WIN32(GetLastError());
    CloseHandle(thread);
    bool set = WaitForSingleObject(ready, 10000) == WAIT_OBJECT_0; // the event stays: the thread has it
    return set && IsClipboardFormatAvailable(CF_HDROP) ? S_OK : E_FAIL;
}

void PrintClipboard()
{
    const wchar_t* state = IsClipboardFormatAvailable(CF_HDROP)          ? L"files"
                           : IsClipboardFormatAvailable(CF_UNICODETEXT) ? L"text"
                           : CountClipboardFormats() == 0               ? L"empty"
                                                                        : L"other";
    Print(L"clipboard " + std::to_wstring(GetClipboardSequenceNumber()) + L" " + state);
}

enum class Clip
{
    None,
    Copy,
    Cut,
    Text,
    Empty,
    Hung,
};

HRESULT PutOnClipboard(Clip clip, const std::vector<std::wstring>& files, bool shellData)
{
    switch (clip)
    {
    case Clip::Copy: return PutFiles(files, DROPEFFECT_COPY | DROPEFFECT_LINK, shellData);
    case Clip::Cut: return PutFiles(files, DROPEFFECT_MOVE, shellData);
    case Clip::Text: return PutText(L"Some text, not files.");
    case Clip::Empty: return PutNothing();
    case Clip::Hung: return PutHung();
    default: return S_OK;
    }
}

bool ParseClip(const std::wstring& text, Clip& clip)
{
    if (text == L"copy") clip = Clip::Copy;
    else if (text == L"cut") clip = Clip::Cut;
    else if (text == L"text") clip = Clip::Text;
    else if (text == L"empty") clip = Clip::Empty;
    else if (text == L"hung") clip = Clip::Hung;
    else return false;
    return true;
}

// The job the fake engine received in <the DLL's folder>\received: lets it run (<job>.go), then waits
// for it to finish (<job>.engine), answering the clipboard meanwhile.
int WaitForEngine(const std::wstring& dll)
{
    std::wstring received = FolderOf(dll) + L"\\received";
    std::wstring job;
    auto found = [&] {
        WIN32_FIND_DATAW data;
        HANDLE search = FindFirstFileW((received + L"\\*.cmjob").c_str(), &data);
        if (search == INVALID_HANDLE_VALUE) return false;
        job = received + L"\\" + data.cFileName;
        FindClose(search);
        return true;
    };
    if (!PumpUntil(found, 10000))
    {
        Print(L"error the engine never got a job");
        return 1;
    }
    if (!WriteUtf8File(job + L".go", L"go"))
    {
        Print(L"error couldn't let the engine go");
        return 1;
    }
    std::wstring exitCode;
    if (!PumpUntil([&] { return ReadSmallFile(job + L".engine", exitCode); }, 30000))
    {
        Print(L"error the engine didn't finish in 30 seconds");
        return 1;
    }
    Print(L"engine " + exitCode);
    return 0;
}

// ---- Running a handler ----------------------------------------------------------------------------

enum class InvokeBy
{
    Offset,
    Verb,
    VerbAnsi,
};

struct Options
{
    std::wstring dll, verb;
    std::vector<std::wstring> targets, sources;
    bool invoke = false, hdrop = true, shellData = false, ui = false;
    InvokeBy by = InvokeBy::Offset;
    // Paste
    bool paste = false, background = false, waitEngine = false;
    Clip clip = Clip::None, clipBeforeInvoke = Clip::None, clipAfterInvoke = Clip::None;
    // The compact menu's Paste (IExplorerCommand)
    bool command = false, emptyItems = false, siteShellFolder = false;
    std::wstring site;
};

// Initialize, as Explorer calls it: the right-drag handler gets the target folder and the dragged
// items; the paste handler gets the selection (a right-click on items) or just the folder (a right-click
// on its background).
HRESULT InitializeHandler(const Options& options, IShellExtInit* handler)
{
    std::vector<Pidl> targets;
    for (const auto& target : options.targets)
    {
        Pidl pidl;
        HRESULT result = ParseTarget(target, pidl);
        if (FAILED(result))
        {
            Print(L"error parsing the target " + Hex(result));
            return result;
        }
        DescribeTarget(pidl.get());
        targets.push_back(std::move(pidl));
    }

    ComPtr<IDataObject> data;
    HRESULT result = S_OK;
    PCIDLIST_ABSOLUTE folder = nullptr;
    if (!options.paste)
    {
        folder = targets[0].get();
        result = options.shellData ? ShellDataObject(options.sources, &data)
                                   : Make<FilesData>(options.sources, options.hdrop).CopyTo(data.GetAddressOf());
    }
    else if (options.background)
    {
        folder = targets[0].get();
    }
    else
    {
        result = ShellDataObject(options.targets, &data);
    }
    if (FAILED(result))
    {
        Print(L"error making the data object " + Hex(result));
        return result;
    }

    result = handler->Initialize(folder, data.Get(), nullptr);
    Print(L"initialize " + Hex(result));
    return result;
}

int AfterInvoke(const Options& options, HRESULT result);

int RunHandler(const Options& options)
{
    ComPtr<IShellExtInit> handler;
    HRESULT result = LoadHandler(options.dll, options.paste ? __uuidof(CopyMasterPaste) : __uuidof(CopyMasterDragDrop),
                                 &handler);
    if (FAILED(result))
    {
        Print(L"error loading the handler " + Hex(result));
        return 1;
    }

    if (options.paste)
    {
        HRESULT put = PutOnClipboard(options.clip, options.sources, options.shellData);
        if (FAILED(put))
        {
            Print(L"error putting that on the clipboard " + Hex(put));
            return 1;
        }
        PrintClipboard();
    }
    if (FAILED(InitializeHandler(options, handler.Get()))) return 1;

    ComPtr<IContextMenu> menu;
    result = handler.As(&menu);
    if (FAILED(result)) return 1;
    HMENU popup = CreatePopupMenu();
    result = menu->QueryContextMenu(popup, 0, kFirstId, 0x7FFF, CMF_NORMAL);
    Print(L"query " + Hex(result));
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
        // The picture beside it: a real bitmap, not one of the HBMMENU_ stock ones (small numbers).
        MENUITEMINFOW picture{};
        picture.cbSize = sizeof(picture);
        picture.fMask = MIIM_BITMAP;
        BITMAP bitmap{};
        if (GetMenuItemInfoW(popup, static_cast<UINT>(i), TRUE, &picture) &&
            reinterpret_cast<UINT_PTR>(picture.hbmpItem) > 16 && GetObjectW(picture.hbmpItem, sizeof(bitmap), &bitmap) != 0)
        {
            Print(L"bitmap " + std::to_wstring(offset) + L" " + std::to_wstring(bitmap.bmWidth) + L"x" +
                  std::to_wstring(bitmap.bmHeight) + L" " + std::to_wstring(bitmap.bmBitsPixel));
        }
    }
    DestroyMenu(popup);
    if (!options.invoke) return 0;
    if (found < 0)
    {
        Print(L"error no item has the verb " + options.verb);
        return 1;
    }

    if (options.clipBeforeInvoke != Clip::None)
    {
        PutOnClipboard(options.clipBeforeInvoke, options.sources, false);
        PrintClipboard();
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
    ULONGLONG started = GetTickCount64();
    ULONGLONG took = 0;
    {
        DialogWatcher dialogs(nullptr); // only --ui lets any show
        result = menu->InvokeCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&info));
        took = GetTickCount64() - started;
        Print(L"invoke " + Hex(result));
        Print(L"took " + std::to_wstring(took));
    }
    return AfterInvoke(options, result);
}

// What every paste run does once the click has happened: shows the clipboard, changes it if asked, and
// waits for the engine if asked.
int AfterInvoke(const Options& options, HRESULT result)
{
    if (options.paste) PrintClipboard();
    if (options.clipAfterInvoke != Clip::None)
    {
        PutOnClipboard(options.clipAfterInvoke, options.sources, false);
        PrintClipboard();
    }
    if (options.waitEngine)
    {
        if (WaitForEngine(options.dll) != 0) return 1;
        PrintClipboard();
    }
    return result == S_OK ? 0 : 1;
}

const wchar_t* StateName(EXPCMDSTATE state)
{
    if (state & ECS_HIDDEN) return L"hidden";
    if (state == ECS_ENABLED) return L"enabled";
    return L"other";
}

std::wstring TakeString(LPWSTR text)
{
    std::wstring copy = text != nullptr ? text : L"";
    CoTaskMemFree(text);
    return copy;
}

// How many icons the file in an icon location ("<file>,-<id>") holds.
UINT IconsIn(const std::wstring& location)
{
    size_t comma = location.find_last_of(L',');
    std::wstring file = comma == std::wstring::npos ? location : location.substr(0, comma);
    return ExtractIconExW(file.c_str(), -1, nullptr, nullptr, 0);
}

// The compact menu's Paste, as Explorer drives an IExplorerCommand.
int RunExplorerCommand(const Options& options)
{
    ComPtr<IExplorerCommand> command;
    HRESULT result = LoadClass(options.dll, __uuidof(CopyMasterPasteCommand), IID_PPV_ARGS(&command));
    if (FAILED(result))
    {
        Print(L"error loading the command " + Hex(result));
        return 1;
    }
    result = PutOnClipboard(options.clip, options.sources, options.shellData);
    if (FAILED(result))
    {
        Print(L"error putting that on the clipboard " + Hex(result));
        return 1;
    }
    PrintClipboard();

    std::vector<Pidl> targets;
    for (const auto& target : options.targets)
    {
        Pidl pidl;
        result = ParseTarget(target, pidl);
        if (FAILED(result))
        {
            Print(L"error parsing the target " + Hex(result));
            return 1;
        }
        DescribeTarget(pidl.get());
        targets.push_back(std::move(pidl));
    }

    // The selection: the targets, or for a background none at all (or an empty one).
    ComPtr<IShellItemArray> items;
    if (options.background && options.emptyItems)
    {
        items = Make<EmptyItems>();
    }
    else if (!options.background)
    {
        std::vector<PCIDLIST_ABSOLUTE> pidls;
        for (const auto& pidl : targets) pidls.push_back(pidl.get());
        result = SHCreateShellItemArrayFromIDLists(static_cast<UINT>(pidls.size()), pidls.data(), &items);
        if (FAILED(result))
        {
            Print(L"error making the selection " + Hex(result));
            return 1;
        }
    }

    // The site: Explorer's folder view, showing the --site folder, or for a background the target.
    HWND window = CreateWindowExW(0, L"STATIC", L"ShellHost site", WS_OVERLAPPED, 0, 0, 100, 100, nullptr, nullptr, nullptr, nullptr);
    Pidl siteFolder;
    if (!options.site.empty())
    {
        result = ParseTarget(options.site, siteFolder);
        if (FAILED(result))
        {
            Print(L"error parsing the site " + Hex(result));
            return 1;
        }
    }
    PCIDLIST_ABSOLUTE siteShows = siteFolder ? siteFolder.get() : options.background ? targets[0].get() : nullptr;
    ComPtr<IObjectWithSite> withSite;
    if (siteShows != nullptr && SUCCEEDED(command.As(&withSite)))
    {
        ComPtr<IServiceProvider> site = Make<FakeSite>(siteShows, options.siteShellFolder, window);
        withSite->SetSite(site.Get());
    }

    EXPCMDSTATE state = ECS_HIDDEN;
    result = command->GetState(items.Get(), FALSE, &state);
    Print(L"state " + Hex(result) + L" " + StateName(state));
    LPWSTR text = nullptr;
    HRESULT got = command->GetTitle(items.Get(), &text);
    if (SUCCEEDED(got)) Print(L"title " + TakeString(text));
    got = command->GetIcon(items.Get(), &text);
    if (SUCCEEDED(got))
    {
        std::wstring icon = TakeString(text);
        Print(L"icon " + icon);
        Print(L"icons " + std::to_wstring(IconsIn(icon)));
    }
    GUID canonical{};
    if (SUCCEEDED(command->GetCanonicalName(&canonical)))
    {
        wchar_t guid[40]{};
        StringFromGUID2(canonical, guid, ARRAYSIZE(guid));
        Print(std::wstring(L"canonical ") + guid);
    }
    EXPCMDFLAGS flags = 0;
    if (SUCCEEDED(command->GetFlags(&flags))) Print(L"flags " + Hex(static_cast<unsigned long>(flags)));
    if (FAILED(result)) return 1;
    if (!options.invoke) return 0;

    if (options.clipBeforeInvoke != Clip::None)
    {
        PutOnClipboard(options.clipBeforeInvoke, options.sources, false);
        PrintClipboard();
    }
    ComPtr<IBindCtx> bind;
    CreateBindCtx(0, &bind);
    {
        DialogWatcher dialogs(window);
        ULONGLONG started = GetTickCount64();
        result = command->Invoke(items.Get(), bind.Get());
        ULONGLONG took = GetTickCount64() - started;
        Print(L"invoke " + Hex(result));
        Print(L"took " + std::to_wstring(took));
    }
    if (withSite) withSite->SetSite(nullptr);
    return AfterInvoke(options, result);
}

int CoCreate(const std::wstring& clsidText, const std::wstring& where)
{
    CLSID clsid{};
    HRESULT result = CLSIDFromString(clsidText.c_str(), &clsid);
    if (FAILED(result) || (where != L"inproc" && where != L"local"))
    {
        Print(L"error not a CLSID and inproc|local: " + clsidText + L" " + where);
        return 2;
    }
    ComPtr<IUnknown> object;
    result = CoCreateInstance(clsid, nullptr, where == L"local" ? CLSCTX_LOCAL_SERVER : CLSCTX_INPROC_SERVER,
                              IID_PPV_ARGS(&object));
    Print(L"cocreate " + Hex(result));
    ComPtr<IExplorerCommand> command;
    if (SUCCEEDED(result) && SUCCEEDED(object.As(&command)))
    {
        // Nothing selected and no site: it should hide itself, and nothing reads the clipboard's data.
        LPWSTR title = nullptr;
        if (SUCCEEDED(command->GetTitle(nullptr, &title))) Print(L"title " + TakeString(title));
        EXPCMDSTATE state = ECS_HIDDEN;
        HRESULT got = command->GetState(nullptr, FALSE, &state);
        Print(L"state " + Hex(got) + L" " + StateName(state));
    }
    return SUCCEEDED(result) ? 0 : 1;
}

// ---- Standing in for CopyMaster.exe ---------------------------------------------------------------

std::wstring Environment(const wchar_t* name)
{
    wchar_t value[32768]{};
    DWORD length = GetEnvironmentVariableW(name, value, ARRAYSIZE(value));
    return length > 0 && length < ARRAYSIZE(value) ? std::wstring(value, length) : std::wstring();
}

// Runs the real engine on the job, once the ShellHost that clicked Paste says so.
int RunRealEngine(const std::wstring& engine, const std::wstring& job)
{
    for (int i = 0; i < 1500 && !Exists(job + L".go"); ++i) Sleep(20);

    SECURITY_ATTRIBUTES inheritable{sizeof(inheritable), nullptr, TRUE};
    HANDLE log = CreateFileW((job + L".log").c_str(), GENERIC_WRITE, FILE_SHARE_READ, &inheritable, CREATE_ALWAYS,
                             FILE_ATTRIBUTE_NORMAL, nullptr);
    if (log == INVALID_HANDLE_VALUE) return 1;
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    startup.hStdOutput = log;
    startup.hStdError = log;
    std::wstring commandLine = QuoteArgument(engine) + L" --job " + QuoteArgument(job) + L" --quiet";
    PROCESS_INFORMATION process{};
    DWORD exitCode = 9999;
    if (CreateProcessW(engine.c_str(), commandLine.data(), nullptr, nullptr, TRUE, 0, nullptr, nullptr, &startup, &process))
    {
        if (WaitForSingleObject(process.hProcess, 25000) == WAIT_OBJECT_0) GetExitCodeProcess(process.hProcess, &exitCode);
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
    }
    CloseHandle(log);
    return PublishUtf8File(job + L".engine", std::to_wstring(exitCode)) ? 0 : 1;
}

// As CopyMaster.exe: keep what we were given, for the test to read, out of the real jobs folder.
int FakeEngine(const std::wstring& jobPath)
{
    std::wstring folder = FolderOf(ExePath()) + L"\\received";
    CreateDirectoryW(folder.c_str(), nullptr);
    std::wstring target = folder + L"\\" + jobPath.substr(jobPath.find_last_of(L'\\') + 1);
    // The command line first: once the job file shows up, the test reads both.
    if (!WriteUtf8File(target + L".cmdline", GetCommandLineW())) return 1;
    if (!MoveFileExW(jobPath.c_str(), target.c_str(), MOVEFILE_COPY_ALLOWED | MOVEFILE_REPLACE_EXISTING)) return 1;
    std::wstring engine = Environment(L"CM_SHELLHOST_ENGINE");
    return engine.empty() ? 0 : RunRealEngine(engine, target);
}

// ---- A window station of its own -----------------------------------------------------------------

// Runs `ShellHost <arguments>` on a new window station and desktop. The clipboard belongs to the window
// station, so the child's clipboard is nobody else's, and goes away with it.
int Isolated(int argc, wchar_t** argv)
{
    std::wstring name = kStationPrefix + std::to_wstring(GetCurrentProcessId());
    HWINSTA station = CreateWindowStationW(name.c_str(), 0, WINSTA_ALL_ACCESS, nullptr);
    if (station == nullptr)
    {
        Print(L"error CreateWindowStation " + Hex(HRESULT_FROM_WIN32(GetLastError())));
        return 1;
    }
    // A desktop is made on the calling process's window station: switch over to make it.
    HWINSTA original = GetProcessWindowStation();
    HDESK desktop = nullptr;
    if (SetProcessWindowStation(station))
    {
        desktop = CreateDesktopW(L"Default", nullptr, nullptr, 0, GENERIC_ALL, nullptr);
        DWORD error = GetLastError();
        SetProcessWindowStation(original);
        SetLastError(error);
    }
    if (desktop == nullptr)
    {
        Print(L"error CreateDesktop " + Hex(HRESULT_FROM_WIN32(GetLastError())));
        CloseWindowStation(station);
        return 1;
    }

    std::wstring self = ExePath();
    std::wstring commandLine = QuoteArgument(self);
    for (int i = 2; i < argc; ++i) commandLine += L" " + QuoteArgument(argv[i]);
    std::wstring desktopName = name + L"\\Default";

    HANDLE standard[] = {GetStdHandle(STD_INPUT_HANDLE), GetStdHandle(STD_OUTPUT_HANDLE), GetStdHandle(STD_ERROR_HANDLE)};
    for (HANDLE handle : standard)
    {
        if (handle != nullptr && handle != INVALID_HANDLE_VALUE) SetHandleInformation(handle, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT);
    }
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    startup.lpDesktop = desktopName.data();
    startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = standard[0];
    startup.hStdOutput = standard[1];
    startup.hStdError = standard[2];
    // Everything started in there joins a job, and the window station stays until all of it has ended:
    // the DLL starts "CopyMaster.exe" without waiting, and it needs the desktop too. Closing the job (if
    // this process is killed) ends them all.
    HANDLE job = CreateJobObjectW(nullptr, nullptr);
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (job != nullptr) SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof(limits));

    PROCESS_INFORMATION process{};
    DWORD exitCode = 1;
    if (job != nullptr && CreateProcessW(self.c_str(), commandLine.data(), nullptr, nullptr, TRUE, CREATE_SUSPENDED,
                                         nullptr, nullptr, &startup, &process))
    {
        if (!AssignProcessToJobObject(job, process.hProcess)) Print(L"error AssignProcessToJobObject");
        ResumeThread(process.hThread);
        WaitForSingleObject(process.hProcess, INFINITE);
        GetExitCodeProcess(process.hProcess, &exitCode);
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        // Then whatever it started, for up to three minutes.
        for (int i = 0; i < 9000; ++i)
        {
            JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting{};
            if (!QueryInformationJobObject(job, JobObjectBasicAccountingInformation, &accounting, sizeof(accounting),
                                           nullptr) ||
                accounting.ActiveProcesses == 0)
            {
                break;
            }
            Sleep(20);
        }
    }
    else
    {
        Print(L"error CreateProcess " + Hex(HRESULT_FROM_WIN32(GetLastError())));
    }
    if (job != nullptr) CloseHandle(job);
    CloseDesktop(desktop);
    CloseWindowStation(station);
    return static_cast<int>(exitCode);
}

int Usage()
{
    Print(L"error usage: ShellHost menu|invoke --dll <dll> --target <target> [--verb <verb>] [--by-verb|--by-verb-ansi] "
          L"[--ui] [--no-hdrop|--shell-data] [--paste|--command [--background [--empty-items]] [--site <target>] "
          L"[--site-shellfolder] --clip copy|cut|text|empty|hung [--clip-before-invoke text] [--clip-after-invoke text] "
          L"[--wait-engine]] <source>... | isolated <command>... | cocreate <clsid> [inproc|local] | --job <file>");
    return 2;
}

int RunCommand(int argc, wchar_t** argv)
{
    std::wstring command = argv[1];
    if (command == L"cocreate" && (argc == 3 || argc == 4)) return CoCreate(argv[2], argc == 4 ? argv[3] : L"inproc");
    if (command != L"menu" && command != L"invoke") return Usage();

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
        auto clip = [&](Clip& out) {
            std::wstring text;
            value(text);
            ok = ok && ParseClip(text, out);
        };
        std::wstring target;
        if (arg == L"--dll") value(options.dll);
        else if (arg == L"--target")
        {
            value(target);
            options.targets.push_back(target);
        }
        else if (arg == L"--verb") value(options.verb);
        else if (arg == L"--by-verb") options.by = InvokeBy::Verb;
        else if (arg == L"--by-verb-ansi") options.by = InvokeBy::VerbAnsi;
        else if (arg == L"--ui") options.ui = true;
        else if (arg == L"--no-hdrop") options.hdrop = false;
        else if (arg == L"--shell-data") options.shellData = true;
        else if (arg == L"--paste") options.paste = true;
        else if (arg == L"--background") options.background = true;
        else if (arg == L"--clip") clip(options.clip);
        else if (arg == L"--clip-before-invoke") clip(options.clipBeforeInvoke);
        else if (arg == L"--clip-after-invoke") clip(options.clipAfterInvoke);
        else if (arg == L"--wait-engine") options.waitEngine = true;
        else if (arg == L"--command") options.command = options.paste = true;
        else if (arg == L"--empty-items") options.emptyItems = true;
        else if (arg == L"--site") value(options.site);
        else if (arg == L"--site-shellfolder") options.siteShellFolder = true;
        else if (arg.rfind(L"--", 0) == 0) ok = false;
        else options.sources.push_back(arg);
    }
    // A command needs no verb: it is the item.
    ok = ok && !options.dll.empty() && !options.targets.empty() &&
         (!options.invoke || options.command || !options.verb.empty());
    // One target, except for a right-click on several selected items.
    ok = ok && (options.targets.size() == 1 || (options.paste && !options.background));
    ok = ok && (options.paste ? options.clip != Clip::None : options.clip == Clip::None);
    ok = ok && (options.command || (!options.emptyItems && options.site.empty() && !options.siteShellFolder));
    if (!ok) return Usage();
    if (options.paste && !OnPrivateStation())
    {
        Print(L"error --paste changes the clipboard, so it runs only on a private window station: ShellHost isolated ...");
        return 2;
    }
    return options.command ? RunExplorerCommand(options) : RunHandler(options);
}

} // namespace

int wmain(int argc, wchar_t** argv)
{
    if (argc == 3 && wcscmp(argv[1], L"--job") == 0) return FakeEngine(argv[2]);
    if (argc >= 3 && wcscmp(argv[1], L"isolated") == 0) return Isolated(argc, argv);
    if (argc < 2) return Usage();

    if (FAILED(OleInitialize(nullptr))) return 1; // an STA with OLE, like Explorer's UI thread
    int exitCode = RunCommand(argc, argv);
    // Files ShellHost put on the clipboard stay its own (OLE's) until it exits: nobody else needs them.
    OleUninitialize();
    return exitCode;
}
