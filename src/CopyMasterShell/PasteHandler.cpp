#include <windows.h>
#include <shlobj.h>
#include <wrl/module.h>

#include <new>
#include <optional>
#include <string>

#include "Clipboard.h"
#include "ContextMenu.h"
#include "Folders.h"
#include "JobLauncher.h"
#include "Module.h"
#include "PasteHandler.h"

CoCreatableClass(PasteHandler);

namespace
{

constexpr MenuItem kItems[] = {
    {L"CopyMaster Paste", L"CopyMaster.Paste", "CopyMaster.Paste",
     L"Pastes the files on the clipboard here with CopyMaster.", "Pastes the files on the clipboard here with CopyMaster."},
};

// How long a click waits for the clipboard's owner to produce the files: under the five seconds after
// which Windows calls a window "Not responding". A healthy owner takes milliseconds.
constexpr DWORD kClipboardTimeoutMs = 3000;

std::wstring ClipboardProblem(HRESULT result)
{
    if (result == DV_E_FORMATETC)
    {
        return L"There are no files on the clipboard to paste.\n\nCopy or cut some in Explorer first.";
    }
    if (result == HRESULT_FROM_WIN32(ERROR_TIMEOUT))
    {
        return L"The clipboard didn't answer: the program that put something there isn't responding.\n\n"
               L"Try again in a moment.";
    }
    return L"CopyMaster couldn't read the clipboard.\n\n" + SystemMessage(result);
}

} // namespace

IFACEMETHODIMP PasteHandler::Initialize(PCIDLIST_ABSOLUTE folder, IDataObject* selection, HKEY)
{
    m_destination.clear();
    try
    {
        // A right-click on a folder or drive hands over the selection: paste into that. On a folder's
        // background (or the Desktop's) there's no selection, just the folder: paste here.
        if (selection != nullptr) m_destination = SelectedFolder(selection);
        else if (folder != nullptr) m_destination = FileSystemFolder(folder);
        return S_OK;
    }
    catch (const std::bad_alloc&)
    {
        m_destination.clear();
        return E_OUTOFMEMORY;
    }
    catch (...)
    {
        m_destination.clear();
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP PasteHandler::QueryContextMenu(HMENU menu, UINT index, UINT firstId, UINT lastId, UINT flags)
{
    if ((flags & CMF_DEFAULTONLY) || m_destination.empty() || !ClipboardHasFiles()) return kNoMenuItems;
    return AddMenuItems(menu, index, firstId, lastId, kItems);
}

IFACEMETHODIMP PasteHandler::InvokeCommand(CMINVOKECOMMANDINFO* info)
{
    if (info == nullptr || info->cbSize < sizeof(CMINVOKECOMMANDINFO)) return E_INVALIDARG;
    if (FindMenuItem(info, kItems) < 0) return E_FAIL; // not ours
    if (m_destination.empty()) return E_UNEXPECTED;
    bool quiet = (info->fMask & CMIC_MASK_FLAG_NO_UI) != 0;
    try
    {
        ClipboardFiles files;
        HRESULT result = ReadClipboardFiles(files, kClipboardTimeoutMs);
        if (FAILED(result))
        {
            if (!quiet) MessageBoxW(info->hwnd, ClipboardProblem(result).c_str(), L"CopyMaster", MB_OK | MB_ICONERROR);
            return result;
        }
        // Only a cut whose clipboard number was pinned down gets one: the engine never empties the
        // clipboard without it.
        std::optional<DWORD> sequence;
        if (files.cut && files.sequence != 0) sequence = files.sequence;
        return StartJob(files.cut ? Operation::Move : Operation::Copy, m_destination, files.paths, sequence,
                        info->hwnd, quiet);
    }
    catch (const std::bad_alloc&)
    {
        return E_OUTOFMEMORY;
    }
    catch (...)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP PasteHandler::GetCommandString(UINT_PTR id, UINT type, UINT*, CHAR* name, UINT size)
{
    return MenuItemString(kItems, id, type, name, size);
}
