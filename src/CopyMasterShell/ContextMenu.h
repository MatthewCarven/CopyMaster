#pragma once

#include <windows.h>
#include <shlobj.h>

#include <span>

// What the handlers share for their IContextMenu: a table of items, and the three calls on it.

// One item a handler adds: what the menu shows, the verb code can invoke it by (Unicode and ANSI),
// and the help text.
struct MenuItem
{
    const wchar_t* text;
    const wchar_t* verb;
    const char* verbAnsi;
    const wchar_t* help;
    const char* helpAnsi;
};

// QueryContextMenu's answer when a handler adds nothing.
constexpr HRESULT kNoMenuItems = MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 0);

// Inserts `items` at `index` with IDs from `firstId` (menu offset i is items[i]), if they fit below
// `lastId`. Returns QueryContextMenu's result: the highest offset used, plus one.
HRESULT AddMenuItems(HMENU menu, UINT index, UINT firstId, UINT lastId, std::span<const MenuItem> items) noexcept;

// Which of `items` InvokeCommand asks for, or -1: by menu offset (a click), or by verb (someone else's
// code), Unicode or ANSI, ignoring case.
int FindMenuItem(const CMINVOKECOMMANDINFO* info, std::span<const MenuItem> items) noexcept;

// GetCommandString over `items`: the verb or help text, in Unicode or ANSI.
HRESULT MenuItemString(std::span<const MenuItem> items, UINT_PTR id, UINT type, CHAR* name, UINT size) noexcept;
