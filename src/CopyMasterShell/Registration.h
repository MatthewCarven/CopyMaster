#pragma once

#include <windows.h>

// HKLM registration of the handlers, their shellex keys and Shell Extensions\Approved.
HRESULT RegisterServer() noexcept;
HRESULT UnregisterServer() noexcept;
