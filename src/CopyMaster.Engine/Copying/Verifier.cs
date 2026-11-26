using System.Runtime.InteropServices;
using CopyMaster.Engine.Control;
using Microsoft.Win32.SafeHandles;

namespace CopyMaster.Engine.Copying;

/// <summary>
/// Compares a copy with its source, byte for byte. The copy is read unbuffered
/// (FILE_FLAG_NO_BUFFERING), so it comes back from the disk and not from the file cache:
/// a cached read would pass a copy that the disk stored wrongly. Opening a file unbuffered
/// makes Windows flush its cached writes first, so the data really has gone to the disk.
/// </summary>
internal static unsafe class Verifier
{
    // A multiple of every sector size, as unbuffered reads need.
    private const int ChunkSize = 1 << 20;
    private const int Alignment = 4096;
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    /// <returns>True if both files hold the same bytes.</returns>
    /// <param name="onBytes">Called with each chunk's size as it's compared.</param>
    public static bool SameContents(string source, string copy, JobControl control, Action<long> onBytes)
    {
        using var src = File.OpenHandle(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        using var dst = File.OpenHandle(copy, FileMode.Open, FileAccess.Read,
            FileShare.Read, NoBuffering | FileOptions.SequentialScan);

        long length = RandomAccess.GetLength(src);
        if (RandomAccess.GetLength(dst) != length) return false;

        byte* a = (byte*)NativeMemory.AlignedAlloc(ChunkSize, Alignment);
        byte* b = (byte*)NativeMemory.AlignedAlloc(ChunkSize, Alignment);
        try
        {
            var bufferA = new Span<byte>(a, ChunkSize);
            var bufferB = new Span<byte>(b, ChunkSize);
            for (long offset = 0; offset < length;)
            {
                control.Checkpoint();
                int want = (int)Math.Min(ChunkSize, length - offset);

                if (ReadFully(src, bufferA[..want], offset) != want) return false;

                // Unbuffered: ask for a whole number of sectors; at the end of the file the
                // read simply returns fewer bytes.
                int aligned = (want + Alignment - 1) / Alignment * Alignment;
                if (RandomAccess.Read(dst, bufferB[..aligned], offset) < want) return false;

                if (!bufferA[..want].SequenceEqual(bufferB[..want])) return false;

                offset += want;
                onBytes(want);
                control.AfterChunk(want);
            }
            return true;
        }
        finally
        {
            NativeMemory.AlignedFree(a);
            NativeMemory.AlignedFree(b);
        }
    }

    private static int ReadFully(SafeFileHandle file, Span<byte> buffer, long offset)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = RandomAccess.Read(file, buffer[total..], offset + total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
