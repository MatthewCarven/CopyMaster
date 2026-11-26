using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CopyMaster.Engine.Native;

internal static unsafe partial class NativeMethods
{
    // ---- CopyFile2 -------------------------------------------------------------------

    internal const uint COPY_FILE_FAIL_IF_EXISTS = 0x00000001;

    internal const int COPYFILE2_CALLBACK_CHUNK_STARTED = 1;
    internal const int COPYFILE2_CALLBACK_CHUNK_FINISHED = 2;
    internal const int COPYFILE2_CALLBACK_STREAM_STARTED = 3;
    internal const int COPYFILE2_CALLBACK_STREAM_FINISHED = 4;
    internal const int COPYFILE2_CALLBACK_POLL_CONTINUE = 5;
    internal const int COPYFILE2_CALLBACK_ERROR = 6;

    internal const int COPYFILE2_PROGRESS_CONTINUE = 0;
    internal const int COPYFILE2_PROGRESS_CANCEL = 1; // stops and deletes the destination

    [StructLayout(LayoutKind.Sequential)]
    internal struct COPYFILE2_EXTENDED_PARAMETERS
    {
        public uint dwSize;
        public uint dwCopyFlags;
        public int* pfCancel;
        public delegate* unmanaged<COPYFILE2_MESSAGE*, void*, int> pProgressRoutine;
        public void* pvCallbackContext;
    }

    // COPYFILE2_MESSAGE: Type, dwPadding, then a union. Only the members we read are declared.
    [StructLayout(LayoutKind.Explicit)]
    internal struct COPYFILE2_MESSAGE
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(8)] public COPYFILE2_STREAM_STARTED StreamStarted;
        [FieldOffset(8)] public COPYFILE2_CHUNK_FINISHED ChunkFinished;
        [FieldOffset(8)] public COPYFILE2_ERROR Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct COPYFILE2_STREAM_STARTED
    {
        public uint dwStreamNumber;
        public uint dwReserved;
        public nint hSourceFile;
        public nint hDestinationFile;
        public ulong uliStreamSize;
        public ulong uliTotalFileSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct COPYFILE2_CHUNK_FINISHED
    {
        public uint dwStreamNumber;
        public uint dwFlags;
        public nint hSourceFile;
        public nint hDestinationFile;
        public ulong uliChunkNumber;
        public ulong uliChunkSize;
        public ulong uliStreamSize;
        public ulong uliStreamBytesTransferred;
        public ulong uliTotalFileSize;
        public ulong uliTotalBytesTransferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct COPYFILE2_ERROR
    {
        public int CopyPhase;
        public uint dwStreamNumber;
        public int hrFailure;
        public uint dwReserved;
        public ulong uliChunkNumber;
        public ulong uliStreamSize;
        public ulong uliStreamBytesTransferred;
        public ulong uliTotalFileSize;
        public ulong uliTotalBytesTransferred;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CopyFile2(string existingFileName, string newFileName,
        COPYFILE2_EXTENDED_PARAMETERS* extendedParameters);

    // ---- Moving, volumes, file identity -----------------------------------------------

    internal const uint MOVEFILE_REPLACE_EXISTING = 0x1;

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveFileEx(string existingFileName, string newFileName, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumePathName(string fileName, char* volumePathName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumeNameForVolumeMountPoint(string mountPoint, char* volumeName,
        uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);

    internal const uint DRIVE_REMOTE = 4;

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetDriveType(string rootPathName);

    /// <summary>The \\server\share a mapped drive letter ("Z:") points at. Returns a Win32 error code.</summary>
    [LibraryImport("mpr.dll", EntryPoint = "WNetGetConnectionW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int WNetGetConnection(string localName, char* remoteName, ref uint length);

    internal const uint FILE_READ_ATTRIBUTES = 0x80;
    internal const uint FILE_SHARE_ALL = 0x7; // read | write | delete
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000; // needed to open directories

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    // FILETIMEs are pairs of DWORDs (4-byte aligned), so they're declared as uint pairs.
    [StructLayout(LayoutKind.Sequential)]
    internal struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public uint ftCreationTimeLow, ftCreationTimeHigh;
        public uint ftLastAccessTimeLow, ftLastAccessTimeHigh;
        public uint ftLastWriteTimeLow, ftLastWriteTimeHigh;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandle(SafeFileHandle file,
        out BY_HANDLE_FILE_INFORMATION information);

    // ---- Which physical disks a volume is on --------------------------------------------

    internal const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000; // FILE_ANY_ACCESS: no rights needed
    internal const uint FILE_SHARE_READ_WRITE = 0x3;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, void* inBuffer,
        uint inBufferSize, void* outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);

    // ---- Handing a job to the running instance --------------------------------------------

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    /// <summary>Lets that process bring its window to the front (we're the foreground process now).</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(uint processId);

    // ---- Telling Explorer ---------------------------------------------------------------

    internal const int SHCNE_UPDATEDIR = 0x00001000;
    internal const uint SHCNF_PATHW = 0x0005;

    [LibraryImport("shell32.dll")]
    internal static partial void SHChangeNotify(int eventId, uint flags, char* item1, nint item2);
}
