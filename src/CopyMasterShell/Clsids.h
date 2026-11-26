#pragma once

// The handlers' class IDs. Shared with tools\ShellHost, which loads the DLL without the registry:
//   struct __declspec(uuid(COPYMASTER_DRAGDROP_CLSID)) X;  ...  __uuidof(X)

// The right-drag menu's "CopyMaster Copy here" / "CopyMaster Move here" (phase 3).
#define COPYMASTER_DRAGDROP_CLSID "EA68F6FA-3C02-46D3-917E-FEE4FF327E27"
