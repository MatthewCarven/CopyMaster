#include <windows.h>
#include <objbase.h>

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
