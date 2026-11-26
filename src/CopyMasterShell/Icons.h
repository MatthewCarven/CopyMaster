#pragma once

#include <windows.h>

#include <string>

// The CopyMaster icon in this DLL's resources (IDI_COPYMASTER), for the menu items.

// "<this DLL's path>,-101": an icon location, the form IExplorerCommand::GetIcon answers with.
// Empty if Windows won't say where this DLL is. May throw std::bad_alloc.
std::wstring IconLocation();

// The icon as a classic menu item's bitmap (MENUITEMINFO::hbmpItem): 32 bits a pixel, alpha
// premultiplied, at the small-icon size. Made on first use and kept until the handler goes; a handler
// outlives the menu it filled. Null if it couldn't be made: the items then show without one.
class MenuBitmap
{
public:
    MenuBitmap() = default;
    MenuBitmap(const MenuBitmap&) = delete;
    MenuBitmap& operator=(const MenuBitmap&) = delete;
    ~MenuBitmap();

    HBITMAP Get() noexcept;

private:
    HBITMAP m_bitmap = nullptr;
    bool m_tried = false;
};
