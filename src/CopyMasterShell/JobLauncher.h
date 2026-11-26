#pragma once

#include <windows.h>

#include <string>
#include <vector>

enum class Operation
{
    Copy,
    Move,
};

// Hands a job to CopyMaster.exe: writes it to %LOCALAPPDATA%\CopyMaster\jobs\<guid>.cmjob in
// JobFile's format (src/CopyMaster.Engine/Jobs/JobFile.cs), then starts the CopyMaster.exe in this
// DLL's own folder with --job <file>, and doesn't wait for it. CopyMaster.exe deletes the file once
// the job is queued. Nothing here reads or writes the files being copied.
//
// On failure: no job file is left behind, and unless `quiet`, a message box owned by `owner` says
// what went wrong.
HRESULT StartJob(Operation operation, const std::wstring& destination, const std::vector<std::wstring>& sources,
                 HWND owner, bool quiet) noexcept;
