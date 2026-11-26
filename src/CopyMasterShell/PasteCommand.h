#pragma once

#include <windows.h>
#include <shobjidl.h>
#include <wrl/client.h>
#include <wrl/implements.h>

#include <string>

#include "Clsids.h"

// "CopyMaster Paste" in Windows 11's compact right-click menu (the classic menu's is PasteHandler; both
// share Paste.h).
//
// Windows 11 puts only packaged apps' commands in its compact menu, so this class isn't registered by
// DllRegisterServer: CopyMaster's sparse package (package\AppxManifest.xml) names it for Directory (a
// folder), Directory\Background (a folder's background, and the Desktop's) and Drive, and gives its
// "external location", the install folder, where this DLL and CopyMaster.exe are. Explorer may run it in
// its own process or in a COM surrogate (dllhost.exe, in the package's context): nothing here depends on
// which. Explorer calls SetSite (its folder view), then GetState, GetTitle and GetIcon to build the menu,
// and Invoke if Paste is chosen.
//
// Where to paste: the one folder or drive in the selection Explorer hands over (a right-click on it), or,
// with no selection (a right-click on a background), the folder the site's view shows. GetState shows
// the command only when that's a folder on disk and the clipboard offers files, reading nothing but the
// clipboard's list of formats. Invoke reads the files (never hanging on the clipboard's owner) and hands
// CopyMaster.exe the job, as the classic menu's Paste does.
class __declspec(uuid(COPYMASTER_PASTE_COMMAND_CLSID)) PasteCommand final
    : public Microsoft::WRL::RuntimeClass<Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>, IExplorerCommand,
                                          IObjectWithSite>
{
public:
    // IExplorerCommand
    IFACEMETHODIMP GetTitle(IShellItemArray* items, LPWSTR* title) override;
    IFACEMETHODIMP GetIcon(IShellItemArray* items, LPWSTR* icon) override;
    IFACEMETHODIMP GetToolTip(IShellItemArray* items, LPWSTR* tip) override;
    IFACEMETHODIMP GetCanonicalName(GUID* name) override;
    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL okToBeSlow, EXPCMDSTATE* state) override;
    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx* bind) override;
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override;
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override;

    // IObjectWithSite
    IFACEMETHODIMP SetSite(IUnknown* site) override;
    IFACEMETHODIMP GetSite(REFIID iid, void** site) override;

private:
    // Where a paste goes, or empty for nowhere. May throw std::bad_alloc.
    std::wstring Destination(IShellItemArray* items) const;
    std::wstring SiteFolder() const;
    HWND OwnerWindow() const noexcept;

    Microsoft::WRL::ComPtr<IUnknown> m_site;
};
