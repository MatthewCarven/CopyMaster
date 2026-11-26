using CopyMaster.Engine.Jobs;

namespace CopyMaster.Engine.Tests;

/// <summary>
/// A scratch folder (under %TEMP%, or under <c>parent</c>), deleted afterwards, read-only
/// files included.
/// </summary>
internal sealed class TestFolder : IDisposable
{
    public TestFolder(string? parent = null)
    {
        Root = Path.Combine(parent ?? Path.GetTempPath(), "CopyMasterTests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string At(params string[] parts) => Path.Combine([Root, .. parts]);

    public string Folder(params string[] parts)
    {
        string path = At(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    public string File(string relative, string content)
    {
        string path = At(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public string RandomFile(string relative, long size)
    {
        string path = At(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var buffer = new byte[Math.Min(size, 1 << 20)];
        Random.Shared.NextBytes(buffer);
        using var stream = new FileStream(path, FileMode.CreateNew);
        for (long left = size; left > 0; left -= buffer.Length)
        {
            stream.Write(buffer, 0, (int)Math.Min(left, buffer.Length));
        }
        return path;
    }

    public void Dispose()
    {
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(Root, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true,
                     }))
            {
                System.IO.File.SetAttributes(entry, FileAttributes.Normal);
            }
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A test that failed may still hold a file open; %TEMP% will do.
        }
    }
}

internal static class Run
{
    public static JobResult Copy(string destination, params string[] sources) =>
        Job(CopyOperation.Copy, destination, sources);

    public static JobResult Move(string destination, params string[] sources) =>
        Job(CopyOperation.Move, destination, sources);

    public static JobResult Job(CopyOperation operation, string destination, IEnumerable<string> sources,
        JobOptions? options = null, IJobPrompts? prompts = null, bool forceCrossVolume = false)
    {
        using var job = new CopyJob(new JobSpec(operation, destination, sources.ToList()), options, prompts)
        {
            ForceCrossVolume = forceCrossVolume,
        };
        return job.Run();
    }

    public static void AssertNoTempFiles(string folder) =>
        Assert.Empty(Directory.EnumerateFiles(folder, ".~cm*.tmp", new EnumerationOptions
        {
            RecurseSubdirectories = true, AttributesToSkip = 0,
        }));

    public static void AssertSucceeded(JobResult result) =>
        Assert.True(result.Succeeded,
            "Failures: " + string.Join("; ", result.Failures.Select(f => $"{f.Source}: {f.Message}")) +
            $" Stopped: {result.StoppedReason} Cancelled: {result.Cancelled}");
}

internal sealed class FakePrompts : IJobPrompts
{
    public Func<ConflictInfo, ConflictAnswer> OnConflict { get; init; } = _ => new(ConflictResolution.Skip);
    public Func<string, DiskFullAnswer> OnDiskFull { get; init; } = _ => DiskFullAnswer.StopJob;
    public Func<long, long, bool> OnNoRoom { get; init; } = (_, _) => true; // (needed, free) → start anyway?
    public int ConflictCalls { get; private set; }
    public int DiskFullCalls { get; private set; }
    public int NoRoomCalls { get; private set; }

    public bool StartWithoutRoom(string destination, long bytesNeeded, long bytesFree)
    {
        NoRoomCalls++;
        return OnNoRoom(bytesNeeded, bytesFree);
    }

    public ConflictAnswer ResolveConflict(ConflictInfo conflict)
    {
        ConflictCalls++;
        return OnConflict(conflict);
    }

    public DiskFullAnswer DiskFull(string destination, string message)
    {
        DiskFullCalls++;
        return OnDiskFull(destination);
    }
}

[CollectionDefinition("Timing", DisableParallelization = true)]
public sealed class TimingCollection;
