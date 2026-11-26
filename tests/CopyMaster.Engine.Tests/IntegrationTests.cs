using System.Diagnostics;
using CopyMaster.Engine.Control;
using CopyMaster.Engine.Copying;
using CopyMaster.Engine.Jobs;
using CopyMaster.Engine.Native;
using Xunit.Abstractions;

namespace CopyMaster.Engine.Tests;

// Opt-in tests against real places. Skipped unless these are set:
//   CM_TEST_VOLUME        a folder on another volume       (dev\test-volumes.ps1 makes one)
//   CM_TEST_SMALL_VOLUME  a folder on a small volume to fill (dev\test-volumes.ps1 makes one)
//   CM_TEST_SHARE         a scratch folder on an SMB share
//   CM_TEST_SHARE_UNC     the same folder by its \\server\share name (if CM_TEST_SHARE uses a drive letter)

internal static class Places
{
    public static string? OtherVolume => Environment.GetEnvironmentVariable("CM_TEST_VOLUME");
    public static string? SmallVolume => Environment.GetEnvironmentVariable("CM_TEST_SMALL_VOLUME");
    public static string? Share => Environment.GetEnvironmentVariable("CM_TEST_SHARE");
    public static string? ShareUnc => Environment.GetEnvironmentVariable("CM_TEST_SHARE_UNC");
}

public sealed class OtherVolumeFactAttribute : FactAttribute
{
    public OtherVolumeFactAttribute()
    {
        if (Places.OtherVolume is null) Skip = "CM_TEST_VOLUME isn't set (dev\\test-volumes.ps1).";
    }
}

public sealed class SmallVolumeFactAttribute : FactAttribute
{
    public SmallVolumeFactAttribute()
    {
        if (Places.SmallVolume is null) Skip = "CM_TEST_SMALL_VOLUME isn't set (dev\\test-volumes.ps1).";
    }
}

public sealed class ShareFactAttribute : FactAttribute
{
    public ShareFactAttribute()
    {
        if (Places.Share is null) Skip = "CM_TEST_SHARE isn't set.";
    }
}

public class OtherVolumeTests
{
    [OtherVolumeFact]
    public void Volumes_are_told_apart()
    {
        using var local = new TestFolder();
        using var other = new TestFolder(Places.OtherVolume);
        Assert.False(Volumes.SameVolume(local.Root, other.Root));
        Assert.True(Volumes.SameVolume(local.Root, Path.GetTempPath()));
    }

    [OtherVolumeFact]
    public void A_vhdx_is_a_disk_of_its_own_for_the_scheduler()
    {
        using var local = new TestFolder();
        using var other = new TestFolder(Places.OtherVolume);
        var localDisks = Scheduling.Disks.KeysFor(local.Root);
        var otherDisks = Scheduling.Disks.KeysFor(other.Root);
        Assert.All(localDisks.Concat(otherDisks), key => Assert.StartsWith("disk:", key));
        Assert.Empty(localDisks.Intersect(otherDisks));
    }

    [OtherVolumeFact]
    public void A_move_to_another_volume_copies_then_deletes_and_keeps_everything()
    {
        using var local = new TestFolder();
        using var other = new TestFolder(Places.OtherVolume);
        string a = local.File(@"tree\a.txt", "a");
        File.WriteAllText(a + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var created = new DateTime(2019, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var modified = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetCreationTimeUtc(a, created);
        File.SetLastWriteTimeUtc(a, modified);
        local.File(@"tree\sub\b.txt", "b");
        string big = local.RandomFile(@"tree\big.bin", 5 * Units.MB);
        byte[] bigBytes = File.ReadAllBytes(big);

        var result = Run.Move(other.Root, local.At("tree"));

        Run.AssertSucceeded(result);
        Assert.Equal(3, result.ItemsMoved); // one per file: a rename would have been 1
        string moved = other.At(@"tree\a.txt");
        Assert.Contains("ZoneId=3", File.ReadAllText(moved + ":Zone.Identifier"));
        Assert.Equal(created, File.GetCreationTimeUtc(moved));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(moved));
        Assert.Equal(bigBytes, File.ReadAllBytes(other.At(@"tree\big.bin")));
        Assert.False(Directory.Exists(local.At("tree")));
        Run.AssertNoTempFiles(other.Root);
    }

    [OtherVolumeFact]
    public void And_back_again_verified()
    {
        using var local = new TestFolder();
        using var other = new TestFolder(Places.OtherVolume);
        string big = other.RandomFile(@"tree\big.bin", 3 * Units.MB + 7);
        byte[] bytes = File.ReadAllBytes(big);
        other.File(@"tree\sub\b.txt", "b");

        var result = Run.Job(CopyOperation.Move, local.Root, [other.At("tree")], new JobOptions { Verify = true });

        Run.AssertSucceeded(result);
        Assert.Equal(2, result.ItemsMoved);
        Assert.Equal(bytes, File.ReadAllBytes(local.At(@"tree\big.bin")));
        Assert.False(Directory.Exists(other.At("tree")));
    }
}

public class FullDiskTests
{
    private static long Free(string folder) => Volumes.FreeSpace(folder) ?? throw new InvalidOperationException();

    [SmallVolumeFact]
    public void A_file_too_big_for_the_disk_fails_alone_and_gives_the_space_back()
    {
        using var local = new TestFolder();
        using var small = new TestFolder(Places.SmallVolume);
        long free = Free(small.Root);
        string big = local.RandomFile("big.bin", free + 4 * Units.MB);
        string tiny = local.File("tiny.txt", "tiny");
        var prompts = new FakePrompts { OnDiskFull = _ => DiskFullAnswer.SkipFile };

        var result = Run.Job(CopyOperation.Copy, small.Root, [big, tiny], prompts: prompts);

        var failure = Assert.Single(result.Failures);
        Assert.Equal(big, failure.Source);
        Assert.True(Win32.IsDiskFull(failure.HResult), $"HResult 0x{failure.HResult:X8}: {failure.Message}");
        Assert.Equal(1, prompts.DiskFullCalls);
        Assert.Equal("tiny", File.ReadAllText(small.At("tiny.txt")));
        Run.AssertNoTempFiles(small.Root);
        Assert.True(Free(small.Root) > free - Units.MB, "the partial copy's space came back");
    }

    [SmallVolumeFact]
    public void Filling_up_with_no_one_to_ask_stops_and_keeps_what_was_copied()
    {
        using var local = new TestFolder();
        using var small = new TestFolder(Places.SmallVolume);
        int count = (int)(Free(small.Root) / (3 * Units.MB)) + 3;
        string[] files = Enumerable.Range(0, count)
            .Select(i => local.RandomFile($"part{i:00}.bin", 3 * Units.MB)).ToArray();

        var result = Run.Job(CopyOperation.Copy, small.Root, files);

        Assert.Equal("The destination disk is full.", result.StoppedReason);
        Assert.False(result.Cancelled);
        Assert.InRange(result.FilesCopied, 1, count - 1);
        foreach (string copy in Directory.GetFiles(small.Root))
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(local.Root, Path.GetFileName(copy))), File.ReadAllBytes(copy));
        }
        Run.AssertNoTempFiles(small.Root);
    }

    [SmallVolumeFact]
    public void Making_room_then_retrying_finishes()
    {
        using var local = new TestFolder();
        using var small = new TestFolder(Places.SmallVolume);
        string destination = small.Folder("dst");
        string filler = small.RandomFile("filler.bin", Free(small.Root) - 2 * Units.MB);
        string source = local.RandomFile("wanted.bin", 6 * Units.MB);
        var prompts = new FakePrompts
        {
            OnDiskFull = _ =>
            {
                File.Delete(filler);
                return DiskFullAnswer.Retry;
            },
        };

        var result = Run.Job(CopyOperation.Copy, destination, [source], new JobOptions { Verify = true }, prompts);

        Run.AssertSucceeded(result);
        Assert.Equal(1, prompts.DiskFullCalls);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(destination, "wanted.bin")));
    }
}

public class ShareTests(ITestOutputHelper output)
{
    [ShareFact]
    public void A_verified_copy_to_the_share_matches()
    {
        using var local = new TestFolder();
        using var share = new TestFolder(Places.Share);
        string big = local.RandomFile(@"tree\big.bin", 20 * Units.MB + 5);
        local.File(@"tree\sub\b.txt", "b");

        var clock = Stopwatch.StartNew();
        var result = Run.Job(CopyOperation.Copy, share.Root, [local.At("tree")], new JobOptions { Verify = true });
        output.WriteLine($"20 MB copied and verified to the share in {clock.Elapsed.TotalSeconds:0.0} s");

        Run.AssertSucceeded(result);
        Assert.Equal(File.ReadAllBytes(big), File.ReadAllBytes(share.At(@"tree\big.bin")));
        Run.AssertNoTempFiles(share.Root);
    }

    [ShareFact]
    public void Moves_to_and_from_the_share()
    {
        using var local = new TestFolder();
        using var share = new TestFolder(Places.Share);
        local.File(@"tree\a.txt", "a");
        local.File(@"tree\sub\b.txt", "b");

        var there = Run.Move(share.Root, local.At("tree"));
        Run.AssertSucceeded(there);
        Assert.Equal(2, there.ItemsMoved);
        Assert.False(Directory.Exists(local.At("tree")));

        var back = Run.Move(local.Root, share.At("tree"));
        Run.AssertSucceeded(back);
        Assert.Equal("b", File.ReadAllText(local.At(@"tree\sub\b.txt")));
        Assert.False(Directory.Exists(share.At("tree")));
    }

    [ShareFact]
    public void Chunk_sizes_and_the_speed_limit_over_smb()
    {
        using var local = new TestFolder();
        using var share = new TestFolder(Places.Share);
        string source = local.RandomFile("probe.bin", 64 * Units.MB);

        foreach (long? limit in new long?[] { null, 2 * Units.MB })
        {
            using var control = new JobControl { SpeedLimit = limit };
            var chunks = new List<(double Time, long Size)>();
            var clock = Stopwatch.StartNew();
            long size = limit is null ? 64 * Units.MB : 12 * Units.MB;
            string from = limit is null ? source : local.RandomFile("probe12.bin", size);
            FileCopier.Copy(from, share.At($"probe-{limit ?? 0}.bin"), control,
                n => chunks.Add((clock.Elapsed.TotalSeconds, n)));
            double seconds = clock.Elapsed.TotalSeconds;

            output.WriteLine($"limit {(limit is null ? "none" : Units.Speed(limit.Value))}: {Units.Bytes(size)} " +
                             $"in {seconds:0.00} s ({Units.Speed(size / seconds)}), {chunks.Count} chunks: " +
                             string.Join(", ", chunks.GroupBy(c => c.Size).Select(g => $"{Units.Bytes(g.Key)} x{g.Count()}")));
            if (limit is not null)
            {
                output.WriteLine("chunk times: " + string.Join(" ", chunks.Select(c => c.Time.ToString("0.00"))));
                Assert.InRange(seconds, size / (double)limit.Value - 1.5, size / (double)limit.Value + 8);
            }
        }
    }

    [ShareFact]
    public void One_share_by_two_names()
    {
        if (Places.ShareUnc is null) return;
        using var share = new TestFolder(Places.Share);
        string relative = Path.GetRelativePath(Places.Share!, share.Root);
        string uncRoot = Path.Combine(Places.ShareUnc, relative);
        string file = share.RandomFile("a.bin", 8 * Units.MB);
        string target = Path.Combine(uncRoot, "moved");
        Directory.CreateDirectory(target);

        // A mapped drive counts as its share, so this is a rename: nothing copied.
        Assert.True(Volumes.SameVolume(share.Root, target));
        var clock = Stopwatch.StartNew();
        var result = Run.Move(target, file);
        output.WriteLine($"moved in {clock.Elapsed.TotalSeconds:0.00} s, bytes copied: {result.BytesCopied}");

        Run.AssertSucceeded(result);
        Assert.Equal(0, result.BytesCopied);
        Assert.True(File.Exists(Path.Combine(target, "a.bin")));
        Assert.False(File.Exists(file));
    }
}
