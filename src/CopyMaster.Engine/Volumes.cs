using CopyMaster.Engine.Native;
using static CopyMaster.Engine.Native.NativeMethods;

namespace CopyMaster.Engine;

/// <summary>Which volume a path is on, and whether two paths are the same file.</summary>
internal static unsafe class Volumes
{
    private const int BufferLength = 1024;

    /// <summary>
    /// A key that's equal for two paths on the same volume: the volume's GUID path where
    /// there is one, else the volume root (a share's <c>\\server\share\</c>, upper-cased).
    /// A mapped drive letter counts as the share it points at, so Z:\ and \\server\share\
    /// match. A guess at planning time only: a move tries a rename first and falls back to
    /// copying if Windows says ERROR_NOT_SAME_DEVICE.
    /// </summary>
    public static string Key(string fullPath)
    {
        char* root = stackalloc char[BufferLength];
        if (!GetVolumePathName(fullPath, root, BufferLength))
        {
            return (Path.GetPathRoot(fullPath) ?? fullPath).ToUpperInvariant();
        }

        string rootPath = new(root);
        char* name = stackalloc char[BufferLength];
        if (GetVolumeNameForVolumeMountPoint(rootPath, name, BufferLength)) return new string(name).ToUpperInvariant();

        if (rootPath.Length == 3 && rootPath[1] == ':' && GetDriveType(rootPath) == DRIVE_REMOTE &&
            ShareOf(rootPath[..2]) is string share)
        {
            rootPath = share;
        }
        return (Path.EndsInDirectorySeparator(rootPath) ? rootPath : rootPath + '\\').ToUpperInvariant();
    }

    private static string? ShareOf(string drive)
    {
        uint length = BufferLength;
        char* remote = stackalloc char[BufferLength];
        return WNetGetConnection(drive, remote, ref length) == 0 ? new string(remote) : null;
    }

    public static bool SameVolume(string a, string b) =>
        string.Equals(Key(a), Key(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True if both paths name the same file or folder: the same path, or (where both
    /// exist) the same volume serial and file index, which catches hard links and
    /// different spellings of one path.
    /// </summary>
    public static bool SameFile(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        return FileId(a) is { } idA && FileId(b) is { } idB && idA == idB;
    }

    /// <summary>
    /// True only if both can be identified and are different files: the guard before
    /// deleting a source because its destination is identical. If either can't be told
    /// apart, they might be one file, and deleting the "source" would delete the only copy.
    /// </summary>
    public static bool DifferentFiles(string a, string b) =>
        !string.Equals(a, b, StringComparison.OrdinalIgnoreCase) &&
        FileId(a) is { } idA && FileId(b) is { } idB && idA != idB;

    private static (uint Volume, ulong Index)? FileId(string fullPath)
    {
        using var handle = CreateFile(LongPath.Extended(fullPath), FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, 0,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return null;
        return (info.dwVolumeSerialNumber, ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow);
    }

    /// <summary>
    /// Bytes free for this user on the volume holding <paramref name="folder"/>, or null if
    /// Windows won't say. Works for folder mount points and shares, unlike DriveInfo.
    /// </summary>
    public static long? FreeSpace(string folder) =>
        GetDiskFreeSpaceEx(folder, out ulong available, out _, out _) ? (long)available : null;

    /// <summary>Tells Explorer a folder's contents changed, so open windows refresh.</summary>
    public static void NotifyExplorer(string folder)
    {
        fixed (char* path = folder)
        {
            SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, path, 0);
        }
    }
}
