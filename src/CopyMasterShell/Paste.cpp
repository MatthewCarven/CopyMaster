#include <windows.h>
#include <shlobj.h>

#include <new>
#include <optional>
#include <string>

#include "Clipboard.h"
#include "JobLauncher.h"
#include "Module.h"
#include "Paste.h"

namespace
{

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

bool CanPaste(const std::wstring& destination) noexcept
{
    return !destination.empty() && ClipboardHasFiles();
}

HRESULT PasteInto(const std::wstring& destination, HWND owner, bool quiet) noexcept
{
    try
    {
        ClipboardFiles files;
        HRESULT result = ReadClipboardFiles(files, kClipboardTimeoutMs);
        if (FAILED(result))
        {
            if (!quiet) MessageBoxW(owner, ClipboardProblem(result).c_str(), L"CopyMaster", MB_OK | MB_ICONERROR);
            return result;
        }
        // Only a cut whose clipboard number was pinned down gets one: the engine never empties the
        // clipboard without it.
        std::optional<DWORD> sequence;
        if (files.cut && files.sequence != 0) sequence = files.sequence;
        return StartJob(files.cut ? Operation::Move : Operation::Copy, destination, files.paths, sequence, owner, quiet);
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
