#include <windows.h>
#include <oleidl.h>
#include <shellapi.h>
#include <shlobj.h>

#include <memory>
#include <new>
#include <utility>

#include "Clipboard.h"
#include "Folders.h"

namespace
{

// Another program may have the clipboard open for a moment (opening it is exclusive): try for 200 ms.
constexpr int kOpenTries = 10;
constexpr DWORD kOpenPauseMs = 20;
// Reads until the sequence number holds still across one (see Read).
constexpr int kReadTries = 3;

// What the reading thread and the thread waiting for it share. Whichever lets go last frees it, so a
// wait that gives up leaves the reader something to write to.
struct Reading
{
    HANDLE done = nullptr; // set once the reader has finished
    HRESULT result = E_PENDING;
    ClipboardFiles files;

    Reading() = default;
    Reading(const Reading&) = delete;
    Reading& operator=(const Reading&) = delete;
    ~Reading()
    {
        if (done != nullptr) CloseHandle(done);
    }
};

struct ThreadStart
{
    std::shared_ptr<Reading> reading;
    HMODULE module = nullptr; // this DLL, held until the thread ends
};

class OpenClipboardScope
{
public:
    OpenClipboardScope()
    {
        for (int i = 0; i < kOpenTries && !m_open; ++i)
        {
            if (i > 0) Sleep(kOpenPauseMs);
            m_open = OpenClipboard(nullptr) != FALSE;
            if (!m_open) m_error = GetLastError();
        }
    }
    OpenClipboardScope(const OpenClipboardScope&) = delete;
    OpenClipboardScope& operator=(const OpenClipboardScope&) = delete;
    ~OpenClipboardScope()
    {
        if (m_open) CloseClipboard();
    }

    bool Open() const { return m_open; }
    HRESULT Error() const { return HRESULT_FROM_WIN32(m_error != 0 ? m_error : ERROR_ACCESS_DENIED); }

private:
    bool m_open = false;
    DWORD m_error = 0;
};

// Whether a "Preferred DropEffect" means Cut. Explorer's Cut asks for DROPEFFECT_MOVE; its Copy for
// COPY | LINK. Only a plain move counts, so a program that offers both never has its files moved.
bool MeansCut(DWORD effect)
{
    return (effect & DROPEFFECT_MOVE) != 0 && (effect & DROPEFFECT_COPY) == 0;
}

// The files on the open clipboard. Asking for a format may make its owner produce it.
HRESULT ReadOpenClipboard(ClipboardFiles& files)
{
    files.paths.clear();
    files.cut = false;
    HANDLE drop = GetClipboardData(CF_HDROP);
    if (drop == nullptr) return DV_E_FORMATETC;
    files.paths = DropFiles(static_cast<HDROP>(drop));
    if (files.paths.empty()) return DV_E_FORMATETC;

    UINT format = RegisterClipboardFormatW(CFSTR_PREFERREDDROPEFFECT);
    HANDLE effect = format != 0 ? GetClipboardData(format) : nullptr;
    if (effect != nullptr && GlobalSize(effect) >= sizeof(DWORD))
    {
        if (auto value = static_cast<const DWORD*>(GlobalLock(effect)))
        {
            files.cut = MeansCut(*value);
            GlobalUnlock(effect);
        }
    }
    return S_OK;
}

// Reads the files and the sequence number that names them. The number is taken before opening and
// after closing, and kept only if it held still: then nothing changed in between, so it names exactly
// what was read. (The engine empties the clipboard only if the number is still this one.) Producing a
// delayed format can itself move the number on, so a second read, of data now there, settles it.
HRESULT Read(ClipboardFiles& files)
{
    for (int attempt = 0; attempt < kReadTries; ++attempt)
    {
        DWORD before = 0;
        HRESULT result = E_FAIL;
        {
            OpenClipboardScope clipboard;
            if (!clipboard.Open()) return clipboard.Error();
            before = GetClipboardSequenceNumber();
            result = ReadOpenClipboard(files);
        }
        if (FAILED(result)) return result;
        if (GetClipboardSequenceNumber() == before)
        {
            files.sequence = before; // 0 if this window station won't say
            return S_OK;
        }
    }
    files.sequence = 0; // never held still: a cut still moves, but the clipboard won't be emptied
    return S_OK;
}

void ReadInto(Reading& reading) noexcept
{
    try
    {
        reading.result = Read(reading.files);
    }
    catch (const std::bad_alloc&)
    {
        reading.result = E_OUTOFMEMORY;
    }
    catch (...)
    {
        reading.result = E_UNEXPECTED;
    }
}

DWORD WINAPI ReadThread(void* parameter)
{
    HMODULE module = nullptr;
    {
        std::unique_ptr<ThreadStart> start(static_cast<ThreadStart*>(parameter));
        module = start->module;
        ReadInto(*start->reading);
        SetEvent(start->reading->done);
    } // lets go of the shared state while this DLL is surely still loaded
    FreeLibraryAndExitThread(module, 0);
}

// Waits for `event`, answering only messages other threads send to this one (such as the clipboard's
// WM_RENDERFORMAT): no input, no posted messages, so nothing else in Explorer runs meanwhile.
DWORD WaitAnsweringSentMessages(HANDLE event, DWORD timeoutMs)
{
    ULONGLONG deadline = GetTickCount64() + timeoutMs;
    for (;;)
    {
        ULONGLONG now = GetTickCount64();
        DWORD left = now >= deadline ? 0 : static_cast<DWORD>(deadline - now);
        DWORD waited = MsgWaitForMultipleObjectsEx(1, &event, left, QS_SENDMESSAGE, 0);
        if (waited == WAIT_OBJECT_0 + 1)
        {
            MSG message;
            PeekMessageW(&message, nullptr, 0, 0, PM_NOREMOVE | PM_QS_SENDMESSAGE); // runs the sent ones
            continue;
        }
        return waited; // WAIT_OBJECT_0, WAIT_TIMEOUT or WAIT_FAILED
    }
}

} // namespace

bool ClipboardHasFiles() noexcept
{
    return IsClipboardFormatAvailable(CF_HDROP) != FALSE;
}

HRESULT ReadClipboardFiles(ClipboardFiles& files, DWORD timeoutMs) noexcept
{
    try
    {
        auto reading = std::make_shared<Reading>();
        reading->done = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (reading->done == nullptr) return HRESULT_FROM_WIN32(GetLastError());

        auto start = std::make_unique<ThreadStart>();
        start->reading = reading;
        // Keeps this DLL loaded while the thread runs, even after Explorer lets go of the handler.
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(&ReadThread),
                                &start->module))
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }
        HANDLE thread = CreateThread(nullptr, 0, ReadThread, start.get(), 0, nullptr);
        if (thread == nullptr)
        {
            HRESULT result = HRESULT_FROM_WIN32(GetLastError());
            FreeLibrary(start->module);
            return result;
        }
        start.release(); // the thread owns it now
        CloseHandle(thread);

        DWORD waited = WaitAnsweringSentMessages(reading->done, timeoutMs);
        if (waited == WAIT_TIMEOUT) return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
        if (waited != WAIT_OBJECT_0) return HRESULT_FROM_WIN32(GetLastError());
        if (SUCCEEDED(reading->result)) files = std::move(reading->files);
        return reading->result;
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
