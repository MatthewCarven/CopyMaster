using System.Runtime.InteropServices;
using CopyMaster.Engine.Jobs;
using CopyMaster.Engine.Native;
using static CopyMaster.Engine.Native.NativeMethods;

namespace CopyMaster.Engine.Scheduling;

/// <summary>
/// What a job keeps busy, for the scheduler: one key per physical disk its sources and
/// destination are on (two partitions of one disk share a key), and one per server for
/// network paths (a mapped drive counts as its share's server).
/// </summary>
public static unsafe class Disks
{
    private const int BufferLength = 1024;

    /// <summary>
    /// The keys a job needs. A move within one volume is only renames, which take no disk
    /// time, so those sources add nothing; a job made only of them needs no keys at all.
    /// </summary>
    public static IReadOnlySet<string> ResourcesFor(JobSpec spec)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string destination;
        try
        {
            destination = Path.GetFullPath(spec.Destination);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return keys; // the job will fail at once; nothing to wait for
        }

        bool needsDestination = false;
        foreach (string raw in spec.Sources)
        {
            string source;
            try
            {
                source = Path.GetFullPath(raw);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (spec.Operation == CopyOperation.Move && Volumes.SameVolume(source, destination)) continue;
            keys.UnionWith(KeysFor(source));
            needsDestination = true;
        }
        if (needsDestination) keys.UnionWith(KeysFor(destination));
        return keys;
    }

    /// <summary>Whether a job's keys include a network share (for <see cref="AppSettings.NetworkSpeedLimit"/>).</summary>
    public static bool UsesNetwork(IEnumerable<string> resources) =>
        resources.Any(key => key.StartsWith(ServerPrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>server:NAME</c> for a network path, <c>disk:N</c> for each physical disk under a
    /// local volume, or <c>volume:...</c> if Windows won't say which disks those are.
    /// </summary>
    public static IReadOnlyList<string> KeysFor(string fullPath)
    {
        if (ServerOf(fullPath) is string server) return [ServerKey(server)];

        char* root = stackalloc char[BufferLength];
        if (!GetVolumePathName(fullPath, root, BufferLength)) return [VolumeKey(fullPath)];
        string rootPath = new(root);

        if (rootPath.Length == 3 && rootPath[1] == ':' && GetDriveType(rootPath) == DRIVE_REMOTE)
        {
            uint length = BufferLength;
            char* remote = stackalloc char[BufferLength];
            if (WNetGetConnection(rootPath[..2], remote, ref length) == 0 && ServerOf(new string(remote)) is string mapped)
            {
                return [ServerKey(mapped)];
            }
            return [ServerKey(rootPath[..2])]; // a network drive Windows won't name: still the network
        }

        char* name = stackalloc char[BufferLength];
        if (!GetVolumeNameForVolumeMountPoint(rootPath, name, BufferLength)) return [VolumeKey(rootPath)];
        string volume = new(name); // \\?\Volume{guid}\

        var disks = DiskNumbers(volume.TrimEnd('\\'));
        return disks.Count > 0 ? disks.Select(n => $"disk:{n}").ToList() : [VolumeKey(volume)];
    }

    /// <summary>The server in <c>\\server\share\...</c> or <c>\\?\UNC\server\share\...</c>, or null.</summary>
    internal static string? ServerOf(string path)
    {
        string rest;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) rest = path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return null;
        else if (path.StartsWith(@"\\", StringComparison.Ordinal)) rest = path[2..];
        else return null;

        int end = rest.IndexOf('\\');
        string server = end < 0 ? rest : rest[..end];
        return server.Length > 0 ? server : null;
    }

    private const string ServerPrefix = "server:";

    private static string ServerKey(string server) => ServerPrefix + server.ToUpperInvariant();

    private static string VolumeKey(string root) => "volume:" + root.ToUpperInvariant();

    /// <summary>The physical disk numbers under a volume (several for a spanned or striped one).</summary>
    private static List<uint> DiskNumbers(string volumeDevice)
    {
        var numbers = new List<uint>();
        using var handle = CreateFile(volumeDevice, 0, FILE_SHARE_READ_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (handle.IsInvalid) return numbers;

        // VOLUME_DISK_EXTENTS: a DWORD count, then 8-aligned DISK_EXTENTs of
        // { DWORD DiskNumber; LARGE_INTEGER StartingOffset; LARGE_INTEGER ExtentLength; } (24 bytes).
        const int Header = 8, Extent = 24;
        for (int capacity = 8; capacity <= 1024; capacity *= 8)
        {
            var bytes = new byte[Header + capacity * Extent];
            fixed (byte* buffer = bytes)
            {
                if (DeviceIoControl(handle, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0, buffer, (uint)bytes.Length,
                        out _, 0))
                {
                    uint count = *(uint*)buffer;
                    for (int i = 0; i < count && i < capacity; i++)
                    {
                        uint disk = *(uint*)(buffer + Header + i * Extent);
                        if (!numbers.Contains(disk)) numbers.Add(disk);
                    }
                    return numbers;
                }
            }
            if (Marshal.GetLastPInvokeError() != Win32.ERROR_MORE_DATA) return numbers;
        }
        return numbers;
    }
}
