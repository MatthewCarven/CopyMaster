using System.Runtime.InteropServices;

namespace CopyMaster.Engine.Native;

/// <summary>Win32 error codes and the HRESULTs that carry them.</summary>
internal static class Win32
{
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_NOT_SAME_DEVICE = 17;
    public const int ERROR_SHARING_VIOLATION = 32;
    public const int ERROR_HANDLE_DISK_FULL = 39;
    public const int ERROR_FILE_EXISTS = 80;
    public const int ERROR_DISK_FULL = 112;
    public const int ERROR_ALREADY_EXISTS = 183;
    public const int ERROR_MORE_DATA = 234;
    public const int ERROR_REQUEST_ABORTED = 1235;

    public static int HResultFromWin32(int code) => code <= 0 ? code : unchecked((int)0x80070000 | (code & 0xFFFF));

    /// <summary>The Win32 code inside an HRESULT, or null if it doesn't carry one.</summary>
    // Both masks cast to int: a bare 0xFFFF0000 is a uint, which would widen the test to long.
    public static int? CodeFromHResult(int hr) =>
        (hr & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? hr & 0xFFFF : null;

    public static bool IsDiskFull(int hr) => CodeFromHResult(hr) is ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL;

    public static string Message(int hr) =>
        CodeFromHResult(hr) is int code ? Marshal.GetPInvokeErrorMessage(code) : $"Error 0x{hr:X8}";

    /// <summary>An IOException whose HResult is the failure, so callers can test it.</summary>
    public static IOException Error(int hr) => new(Message(hr), hr);

    public static IOException LastError() => Error(HResultFromWin32(Marshal.GetLastPInvokeError()));
}
