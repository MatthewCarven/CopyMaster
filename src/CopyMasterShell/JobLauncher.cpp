#include <windows.h>
#include <objbase.h>
#include <shlobj.h>

#include <algorithm>
#include <memory>
#include <new>
#include <optional>
#include <string>
#include <vector>

#include "JobLauncher.h"
#include "Module.h"

namespace
{

using CoTaskString = std::unique_ptr<wchar_t, CoTaskMemDeleter>;
using UniqueHandle = std::unique_ptr<void, HandleCloser>;

constexpr char kHeader[] = "CopyMaster job 1\n"; // JobFile.Header

// Why something failed, for the message box.
struct Failure
{
    HRESULT result = S_OK;
    std::wstring message;
};

// Appends `text` as UTF-8. False if it isn't valid UTF-16 (a lone surrogate, which NTFS allows in a
// name): UTF-8 can't carry that, so the engine would look for a different name.
bool AppendUtf8(std::string& out, const std::wstring& text)
{
    if (text.empty()) return true;
    int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr,
                                   0, nullptr, nullptr);
    if (size <= 0) return false;
    size_t start = out.size();
    out.resize(start + static_cast<size_t>(size));
    return WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()),
                               out.data() + start, size, nullptr, nullptr) == size;
}

bool AppendLine(std::string& out, const char* key, const std::wstring& value)
{
    out += key;
    out += '=';
    if (!AppendUtf8(out, value)) return false;
    out += '\n';
    return true;
}

// The job in JobFile's format: a header, then key=value lines. Names can't hold line breaks, so
// nothing needs escaping, and a value may contain '=' (only the first one splits).
Failure FormatJob(Operation operation, const std::wstring& destination, const std::vector<std::wstring>& sources,
                  std::optional<DWORD> clipboardSequence, std::string& text)
{
    text = kHeader;
    text += operation == Operation::Move ? "op=move\n" : "op=copy\n";
    const std::wstring* bad = nullptr;
    if (!AppendLine(text, "dest", destination)) bad = &destination;
    if (clipboardSequence) text += "clipseq=" + std::to_string(*clipboardSequence) + "\n";
    for (const std::wstring& source : sources)
    {
        if (bad == nullptr && !AppendLine(text, "src", source)) bad = &source;
    }
    if (bad != nullptr)
    {
        return {HRESULT_FROM_WIN32(ERROR_NO_UNICODE_TRANSLATION),
                L"CopyMaster can't take this, because its name isn't valid Unicode:\n" + *bad};
    }
    return {};
}

HRESULT MakeFolder(const std::wstring& folder)
{
    if (CreateDirectoryW(folder.c_str(), nullptr) || GetLastError() == ERROR_ALREADY_EXISTS) return S_OK;
    return HRESULT_FROM_WIN32(GetLastError());
}

// %LOCALAPPDATA%\CopyMaster\jobs, made if need be. The same folder CopyMaster.exe deletes job files
// from (Environment.SpecialFolder.LocalApplicationData asks for the same known folder).
Failure JobsFolder(std::wstring& folder)
{
    PWSTR local = nullptr;
    HRESULT result = SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_DEFAULT, nullptr, &local);
    CoTaskString owned(local);
    if (FAILED(result)) return {result, L"CopyMaster couldn't find your AppData\\Local folder.\n\n" + SystemMessage(result)};

    folder = std::wstring(local) + L"\\CopyMaster";
    result = MakeFolder(folder);
    if (SUCCEEDED(result))
    {
        folder += L"\\jobs";
        result = MakeFolder(folder);
    }
    if (FAILED(result)) return {result, L"CopyMaster couldn't make the folder\n" + folder + L"\n\n" + SystemMessage(result)};
    return {};
}

Failure WriteJobFile(const std::wstring& folder, const std::string& text, std::wstring& path)
{
    GUID id{};
    HRESULT result = CoCreateGuid(&id);
    if (FAILED(result)) return {result, L"CopyMaster couldn't name its job file.\n\n" + SystemMessage(result)};
    wchar_t name[40]{};
    StringFromGUID2(id, name, ARRAYSIZE(name)); // {xxxxxxxx-...}
    path = folder + L"\\" + std::wstring(name + 1, 36) + L".cmjob";

    UniqueHandle file(CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (file.get() == INVALID_HANDLE_VALUE)
    {
        result = HRESULT_FROM_WIN32(GetLastError());
        return {result, L"CopyMaster couldn't write its job file\n" + path + L"\n\n" + SystemMessage(result)};
    }

    size_t done = 0;
    while (done < text.size())
    {
        DWORD chunk = static_cast<DWORD>(std::min(text.size() - done, static_cast<size_t>(1) << 30));
        DWORD written = 0;
        if (!WriteFile(file.get(), text.data() + done, chunk, &written, nullptr) || written == 0)
        {
            result = HRESULT_FROM_WIN32(GetLastError());
            file.reset();
            DeleteFileW(path.c_str());
            return {result, L"CopyMaster couldn't write its job file\n" + path + L"\n\n" + SystemMessage(result)};
        }
        done += written;
    }
    return {};
}

// The CopyMaster.exe beside this DLL (the deployed layout keeps them in one folder).
Failure FindEngine(std::wstring& folder, std::wstring& exe)
{
    folder = ModuleFolder();
    exe = folder + L"\\CopyMaster.exe";
    DWORD attributes = GetFileAttributesW(exe.c_str());
    if (folder.empty() || attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY))
    {
        return {HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND),
                L"CopyMaster.exe isn't where it should be:\n" + exe +
                    L"\n\nCopyMaster's Explorer extension starts the CopyMaster.exe in its own folder."};
    }
    return {};
}

Failure StartEngine(const std::wstring& folder, const std::wstring& exe, const std::wstring& jobPath)
{
    // Paths can't contain '"', so quoting each is enough. The working folder is CopyMaster's own,
    // so it doesn't hold on to whatever folder Explorer has as current.
    std::wstring commandLine = L"\"" + exe + L"\" --job \"" + jobPath + L"\"";
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(exe.c_str(), commandLine.data(), nullptr, nullptr, FALSE, 0, nullptr, folder.c_str(),
                        &startup, &process))
    {
        HRESULT result = HRESULT_FROM_WIN32(GetLastError());
        return {result, L"CopyMaster.exe didn't start:\n" + exe + L"\n\n" + SystemMessage(result)};
    }
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return {};
}

Failure Start(Operation operation, const std::wstring& destination, const std::vector<std::wstring>& sources,
              std::optional<DWORD> clipboardSequence)
{
    std::string text;
    Failure failure = FormatJob(operation, destination, sources, clipboardSequence, text);
    if (FAILED(failure.result)) return failure;

    // Find the exe before writing anything, so a broken install leaves no job file.
    std::wstring engineFolder, exe;
    failure = FindEngine(engineFolder, exe);
    if (FAILED(failure.result)) return failure;

    std::wstring jobsFolder;
    failure = JobsFolder(jobsFolder);
    if (FAILED(failure.result)) return failure;

    std::wstring path;
    failure = WriteJobFile(jobsFolder, text, path);
    if (FAILED(failure.result)) return failure;

    failure = StartEngine(engineFolder, exe, path);
    if (FAILED(failure.result)) DeleteFileW(path.c_str());
    return failure;
}

} // namespace

HRESULT StartJob(Operation operation, const std::wstring& destination, const std::vector<std::wstring>& sources,
                 std::optional<DWORD> clipboardSequence, HWND owner, bool quiet) noexcept
{
    try
    {
        Failure failure = Start(operation, destination, sources, clipboardSequence);
        if (FAILED(failure.result) && !quiet)
        {
            MessageBoxW(owner, failure.message.c_str(), L"CopyMaster", MB_OK | MB_ICONERROR);
        }
        return failure.result;
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
