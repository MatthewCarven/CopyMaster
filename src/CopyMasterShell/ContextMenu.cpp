#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <strsafe.h>

#include <string.h>

#include "ContextMenu.h"

HRESULT AddMenuItems(HMENU menu, UINT index, UINT firstId, UINT lastId, std::span<const MenuItem> items) noexcept
{
    UINT count = static_cast<UINT>(items.size());
    if (menu == nullptr || count == 0 || lastId < firstId || lastId - firstId < count - 1) return kNoMenuItems;
    UINT added = 0;
    for (UINT i = 0; i < count; ++i)
    {
        MENUITEMINFOW item{};
        item.cbSize = sizeof(item);
        item.fMask = MIIM_ID | MIIM_STRING | MIIM_FTYPE;
        item.fType = MFT_STRING;
        item.wID = firstId + i;
        item.dwTypeData = const_cast<LPWSTR>(items[i].text);
        if (!InsertMenuItemW(menu, index + i, TRUE, &item)) break;
        ++added;
    }
    return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, added);
}

int FindMenuItem(const CMINVOKECOMMANDINFO* info, std::span<const MenuItem> items) noexcept
{
    int count = static_cast<int>(items.size());
    bool unicode = info->cbSize >= sizeof(CMINVOKECOMMANDINFOEX) && (info->fMask & CMIC_MASK_UNICODE);
    if (unicode)
    {
        auto ex = reinterpret_cast<const CMINVOKECOMMANDINFOEX*>(info);
        if (!IS_INTRESOURCE(ex->lpVerbW))
        {
            for (int i = 0; i < count; ++i)
            {
                if (CompareStringOrdinal(ex->lpVerbW, -1, items[i].verb, -1, TRUE) == CSTR_EQUAL) return i;
            }
            return -1;
        }
    }
    if (!IS_INTRESOURCE(info->lpVerb))
    {
        for (int i = 0; i < count; ++i)
        {
            if (_stricmp(info->lpVerb, items[i].verbAnsi) == 0) return i;
        }
        return -1;
    }
    UINT_PTR offset = reinterpret_cast<UINT_PTR>(info->lpVerb);
    return offset < static_cast<UINT_PTR>(count) ? static_cast<int>(offset) : -1;
}

HRESULT MenuItemString(std::span<const MenuItem> items, UINT_PTR id, UINT type, CHAR* name, UINT size) noexcept
{
    if (id >= items.size()) return E_INVALIDARG;
    const MenuItem& item = items[id];
    switch (type)
    {
    case GCS_VALIDATEW:
    case GCS_VALIDATEA:
        return S_OK;
    case GCS_VERBW:
    case GCS_HELPTEXTW:
        if (name == nullptr || size == 0) return E_INVALIDARG;
        return StringCchCopyW(reinterpret_cast<PWSTR>(name), size, type == GCS_VERBW ? item.verb : item.help);
    case GCS_VERBA:
    case GCS_HELPTEXTA:
        if (name == nullptr || size == 0) return E_INVALIDARG;
        return StringCchCopyA(name, size, type == GCS_VERBA ? item.verbAnsi : item.helpAnsi);
    default:
        return E_NOTIMPL;
    }
}
