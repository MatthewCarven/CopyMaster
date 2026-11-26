#pragma once

#include <windows.h>

#include <string>

// This DLL's full path, and the folder it's in (no trailing backslash). Empty if Windows won't say.
std::wstring ModulePath();
std::wstring ModuleFolder();

// Windows' own words for an error ("Access is denied."), or "Error 0x...." if it has none.
std::wstring SystemMessage(HRESULT result);

// Small owners for what Windows hands out, so an early return or a bad_alloc can't leak it.
struct CoTaskMemDeleter
{
    void operator()(void* memory) const noexcept;
};

struct HandleCloser
{
    void operator()(void* handle) const noexcept;
};
