using CopyMaster.Engine.Jobs;
using CopyMaster.Engine.Native;

namespace CopyMaster.Engine.Tests;

public class VerifyTests
{
    private static void Corrupt(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        int first = stream.ReadByte();
        stream.Position = 0;
        stream.WriteByte((byte)(first ^ 0xFF));
    }

    [Fact]
    public void A_verified_copy_counts_both_stages()
    {
        using var f = new TestFolder();
        string source = f.RandomFile(@"src\a.bin", 3 * Units.MB + 123); // not a whole number of sectors
        using var job = new CopyJob(new JobSpec(CopyOperation.Copy, f.Folder("dst"), [source]),
            new JobOptions { Verify = true });
        JobProgress? last = null;
        job.ProgressChanged += p => last = p;

        Run.AssertSucceeded(job.Run());

        Assert.Equal(2 * (3 * Units.MB + 123), last!.BytesTotal);
        Assert.Equal(last.BytesTotal, last.BytesDone);
    }

    [Fact]
    public void A_mismatch_is_copied_again()
    {
        using var f = new TestFolder();
        string source = f.RandomFile(@"src\a.bin", Units.MB);
        int verifies = 0;
        using var job = new CopyJob(new JobSpec(CopyOperation.Copy, f.Folder("dst"), [source]),
            new JobOptions { Verify = true })
        {
            BeforeVerify = temp =>
            {
                if (++verifies == 1) Corrupt(temp);
            },
        };

        Run.AssertSucceeded(job.Run());

        Assert.Equal(2, verifies);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(f.At(@"dst\a.bin")));
    }

    [Fact]
    public void Two_mismatches_fail_the_file_and_leave_nothing()
    {
        using var f = new TestFolder();
        string source = f.RandomFile(@"src\a.bin", Units.MB);
        string destination = f.Folder("dst");
        using var job = new CopyJob(new JobSpec(CopyOperation.Copy, destination, [source]),
            new JobOptions { Verify = true }) { BeforeVerify = Corrupt };

        var result = job.Run();

        Assert.Contains("didn't match", Assert.Single(result.Failures).Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }
}

public class ErrorTests
{
    private static readonly int DiskFull = Win32.HResultFromWin32(Win32.ERROR_DISK_FULL);

    [Fact]
    public void A_locked_file_fails_the_rest_copy_and_a_retry_finishes()
    {
        using var f = new TestFolder();
        string a = f.File(@"src\a.txt", "a");
        string b = f.File(@"src\b.txt", "b");
        string destination = f.Folder("dst");
        using var job = new CopyJob(new JobSpec(CopyOperation.Copy, destination, [a, b]));

        JobResult result;
        using (new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = job.Run();
            var failure = Assert.Single(result.Failures);
            Assert.Equal(b, failure.Source);
            Assert.Equal(Win32.HResultFromWin32(Win32.ERROR_SHARING_VIOLATION), failure.HResult);
            Assert.Equal(1, result.FilesCopied);
        }

        result = job.RetryFailed();

        Run.AssertSucceeded(result);
        Assert.Equal("b", File.ReadAllText(Path.Combine(destination, "b.txt")));
        Run.AssertNoTempFiles(destination);
    }

    [Fact]
    public void Disk_full_then_retry_copies_the_file()
    {
        using var f = new TestFolder();
        string source = f.File(@"src\a.txt", "a");
        int copies = 0;
        var prompts = new FakePrompts { OnDiskFull = _ => DiskFullAnswer.Retry };
        using var job = new CopyJob(new JobSpec(CopyOperation.Copy, f.Folder("dst"), [source]), prompts: prompts)
        {
            BeforeCopy = (_, _) =>
            {
                if (++copies == 1) throw new IOException("There is not enough space on the disk.", DiskFull);
            },
        };

        Run.AssertSucceeded(job.Run());

        Assert.Equal(1, prompts.DiskFullCalls);
        Assert.True(File.Exists(f.At(@"dst\a.txt")));
    }

    [Fact]
    public void Disk_full_then_skip_fails_that_file_only()
    {
        using var f = new TestFolder();
        string a = f.File(@"src\a.txt", "a");
        string b = f.File(@"src\b.txt", "b");
        var prompts = new FakePrompts { OnDiskFull = _ => DiskFullAnswer.SkipFile };
        using var job = new CopyJob(new JobSpec(CopyOperation.Copy, f.Folder("dst"), [a, b]), prompts: prompts)
        {
            BeforeCopy = (source, _) =>
            {
                if (source == a) throw new IOException("There is not enough space on the disk.", DiskFull);
            },
        };

        var result = job.Run();

        Assert.Equal(a, Assert.Single(result.Failures).Source);
        Assert.Equal(1, result.FilesCopied);
        Assert.Null(result.StoppedReason);
    }

    [Fact]
    public void Disk_full_with_no_one_to_ask_stops_the_job()
    {
        using var f = new TestFolder();
        string a = f.File(@"src\a.txt", "a");
        string b = f.File(@"src\b.txt", "b");
        using var job = new CopyJob(new JobSpec(CopyOperation.Copy, f.Folder("dst"), [a, b]))
        {
            BeforeCopy = (_, _) => throw new IOException("There is not enough space on the disk.", DiskFull),
        };

        var result = job.Run();

        Assert.NotNull(result.StoppedReason);
        Assert.False(result.Cancelled);
        Assert.Equal(0, result.FilesCopied);
    }

    [Fact]
    public void A_folder_that_cannot_be_made_holds_its_contents_for_the_retry()
    {
        using var f = new TestFolder();
        f.File(@"src\tree\a.txt", "a");
        f.File(@"src\tree\sub\b.txt", "b");
        string blocker = f.File(@"dst\tree", "a file where the folder should go");
        using var job = new CopyJob(new JobSpec(CopyOperation.Copy, f.At("dst"), [f.At(@"src\tree")]));

        var result = job.Run();
        Assert.Single(result.Failures);
        Assert.Equal(0, result.FilesCopied);

        File.Delete(blocker);
        result = job.RetryFailed();

        Run.AssertSucceeded(result);
        Assert.Equal("b", File.ReadAllText(f.At(@"dst\tree\sub\b.txt")));
    }

    [Fact]
    public void A_source_that_cannot_be_deleted_is_retried_as_a_delete_only()
    {
        using var f = new TestFolder();
        string source = f.File(@"src\a.txt", "a");
        FileStream? holder = null;
        using var job = new CopyJob(new JobSpec(CopyOperation.Move, f.Folder("dst"), [source]))
        {
            ForceCrossVolume = true,
            // Readable (so the copy works) but not deletable while held.
            BeforeCopy = (s, _) => holder = new FileStream(s, FileMode.Open, FileAccess.Read, FileShare.Read),
        };

        var result = job.Run();
        var failure = Assert.Single(result.Failures);
        Assert.Equal(FailureStage.DeleteSource, failure.Stage);
        Assert.True(File.Exists(f.At(@"dst\a.txt")));
        Assert.Equal(1, result.FilesCopied);

        holder!.Dispose();
        result = job.RetryFailed();

        Run.AssertSucceeded(result);
        Assert.False(File.Exists(source));
        Assert.Equal(1, result.ItemsMoved);
        Assert.Equal(0, result.FilesCopied);
    }
}
