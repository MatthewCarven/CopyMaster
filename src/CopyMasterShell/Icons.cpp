#include <windows.h>

#include <memory>
#include <new>
#include <string>
#include <type_traits>
#include <vector>

#include "Icons.h"
#include "Module.h"
#include "resource.h"

extern "C" IMAGE_DOS_HEADER __ImageBase; // this DLL's own HMODULE, from the linker

namespace
{

struct BitmapDeleter
{
    void operator()(HBITMAP bitmap) const noexcept
    {
        if (bitmap != nullptr) DeleteObject(bitmap);
    }
};

// The icon at `size` pixels as a top-down 32-bit DIB with premultiplied alpha, as menus draw
// hbmpItem (AlphaBlend with AC_SRC_ALPHA). LoadImage picks the icon's nearest size and scales it.
HBITMAP MakeMenuBitmap(int size)
{
    HICON icon = static_cast<HICON>(LoadImageW(reinterpret_cast<HINSTANCE>(&__ImageBase), MAKEINTRESOURCEW(IDI_COPYMASTER),
                                               IMAGE_ICON, size, size, LR_DEFAULTCOLOR));
    if (icon == nullptr) return nullptr;
    ICONINFO info{};
    BOOL got = GetIconInfo(icon, &info);
    DestroyIcon(icon);
    if (!got) return nullptr;
    std::unique_ptr<std::remove_pointer_t<HBITMAP>, BitmapDeleter> color(info.hbmColor), mask(info.hbmMask);
    if (!color) return nullptr; // a black-and-white icon: ours never is

    BITMAPINFO format{};
    format.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    format.bmiHeader.biWidth = size;
    format.bmiHeader.biHeight = -size; // top-down
    format.bmiHeader.biPlanes = 1;
    format.bmiHeader.biBitCount = 32;
    format.bmiHeader.biCompression = BI_RGB;
    std::vector<DWORD> pixels(static_cast<size_t>(size) * static_cast<size_t>(size));
    HDC screen = GetDC(nullptr);
    int lines = screen != nullptr ? GetDIBits(screen, color.get(), 0, size, pixels.data(), &format, DIB_RGB_COLORS) : 0;
    if (screen != nullptr) ReleaseDC(nullptr, screen);
    if (lines != size) return nullptr;

    bool alpha = false;
    for (DWORD pixel : pixels) alpha = alpha || (pixel >> 24) != 0;
    if (!alpha) return nullptr; // an icon with no alpha channel would need its mask: ours always has one

    void* bits = nullptr;
    HBITMAP bitmap = CreateDIBSection(nullptr, &format, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (bitmap == nullptr || bits == nullptr) return bitmap;
    auto out = static_cast<DWORD*>(bits);
    for (size_t i = 0; i < pixels.size(); ++i)
    {
        DWORD pixel = pixels[i];
        DWORD a = pixel >> 24;
        auto premultiply = [a](DWORD channel) { return (channel * a + 127) / 255; };
        out[i] = (a << 24) | (premultiply((pixel >> 16) & 0xFF) << 16) | (premultiply((pixel >> 8) & 0xFF) << 8) |
                 premultiply(pixel & 0xFF);
    }
    return bitmap;
}

} // namespace

std::wstring IconLocation()
{
    std::wstring path = ModulePath();
    if (path.empty()) return {};
    return path + L",-" + std::to_wstring(IDI_COPYMASTER);
}

MenuBitmap::~MenuBitmap()
{
    if (m_bitmap != nullptr) DeleteObject(m_bitmap);
}

HBITMAP MenuBitmap::Get() noexcept
{
    if (!m_tried)
    {
        m_tried = true;
        try
        {
            int size = GetSystemMetrics(SM_CXSMICON);
            m_bitmap = MakeMenuBitmap(size > 0 ? size : 16);
        }
        catch (const std::bad_alloc&)
        {
            m_bitmap = nullptr;
        }
    }
    return m_bitmap;
}
