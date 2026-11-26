#include <windows.h>
#include <objbase.h>

#include <stdio.h>

#include "Module.h"

extern "C" IMAGE_DOS_HEADER __ImageBase; // this DLL's own HMODULE, from the linker

std::wstring ModulePath()
{
    std::wstring path(MAX_PATH, L'\0');
    for (;;)
    {
        DWORD length = GetModuleFileNameW(reinterpret_cast<HMODULE>(&__ImageBase), path.data(),
                                          static_cast<DWORD>(path.size()));
        if (length == 0) return {};
        if (length < path.size())
        {
            path.resize(length);
            return path;
        }
        if (path.size() >= 32768) return {};
        path.resize(path.size() * 2); // too long for the buffer: it was cut short
    }
}

std::wstring SystemMessage(HRESULT result)
{
    PWSTR text = nullptr;
    DWORD length = FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM |
                                      FORMAT_MESSAGE_IGNORE_INSERTS,
                                  nullptr, static_cast<DWORD>(result), 0, reinterpret_cast<PWSTR>(&text), 0, nullptr);
    if (length == 0 || text == nullptr)
    {
        wchar_t code[32];
        swprintf_s(code, L"Error 0x%08X.", static_cast<unsigned>(result));
        return code;
    }
    std::wstring message(text, length);
    LocalFree(text);
    while (!message.empty() && (message.back() == L'\n' || message.back() == L'\r' || message.back() == L' '))
    {
        message.pop_back();
    }
    return message;
}

std::wstring ModuleFolder()
{
    std::wstring path = ModulePath();
    size_t slash = path.find_last_of(L'\\');
    return slash == std::wstring::npos ? std::wstring{} : path.substr(0, slash);
}

void CoTaskMemDeleter::operator()(void* memory) const noexcept
{
    CoTaskMemFree(memory);
}

void HandleCloser::operator()(void* handle) const noexcept
{
    if (handle != nullptr && handle != INVALID_HANDLE_VALUE) CloseHandle(handle);
}
