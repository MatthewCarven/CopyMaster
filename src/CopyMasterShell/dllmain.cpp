// CopyMasterShell: the Explorer side of CopyMaster.
//
// This DLL runs inside explorer.exe, so a crash or hang here takes Explorer down.
// It only collects {operation, destination, sources}, writes a job file and starts
// CopyMaster.exe; it never touches file contents (PLAN.md, "Why two parts").
//
//   DragDropHandler.*   the right-drag menu (phase 3)
//   PasteHandler.*      the right-click menu's Paste (phase 4); Clipboard.* reads the clipboard
//   ContextMenu.*, Folders.*  what the two share: menu items, where to copy to
//   JobLauncher.*       writes the job file and starts CopyMaster.exe beside this DLL
//   Registration.*      HKLM registration for regsvr32
//
// No DllMain: the CRT's default is enough, and with the static CRT this DLL must
// not call DisableThreadLibraryCalls.

#include <windows.h>
#include <wrl/module.h>

#include "Registration.h"

using Microsoft::WRL::InProc;
using Microsoft::WRL::Module;

STDAPI DllGetClassObject(_In_ REFCLSID clsid, _In_ REFIID iid, _Outptr_ LPVOID* object)
{
    return Module<InProc>::GetModule().GetClassObject(clsid, iid, object);
}

STDAPI DllCanUnloadNow()
{
    return Module<InProc>::GetModule().GetObjectCount() == 0 ? S_OK : S_FALSE;
}

STDAPI DllRegisterServer()
{
    return RegisterServer();
}

STDAPI DllUnregisterServer()
{
    return UnregisterServer();
}
