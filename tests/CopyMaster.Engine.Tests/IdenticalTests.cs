using System.Runtime.InteropServices;
using CopyMaster.Engine.Jobs;

namespace CopyMaster.Engine.Tests;

/// <summary>A file the destination already has, byte for byte: nothing to copy, nothing to ask.</summary>
public class IdenticalTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr security);

    private static readonly DateTime Old = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static FakePrompts NeverAsked() => new() { OnConflict = _ => new(ConflictResolution.CancelJob) };

    [Fact]
    public void An_identical_file_is_not_copied_and_nothing_is_asked()
    {
        using var f = new TestFolder();
        string source = f.File(@"src\a.txt", "same bytes");
        string existing = f.File(@"dst\a.txt", "same bytes");
        File.SetLastWriteTimeUtc(existing, Old);
        var prompts = NeverAsked();
        using var job = new CopyJob(new JobSpec(CopyOperation.Copy, f.At("dst"), [source]), prompts: prompts);
        JobProgress? last = null;
        job.ProgressChanged += p => last = p;

        var result = job.Run();

        Run.AssertSucceeded(result);
        Assert.Equal(1, result.Identical);
        Assert.Equal(0, result.FilesCopied);
        Assert.Equal(0, prompts.ConflictCalls);
        Assert.Equal(Old, File.GetLastWriteTimeUtc(existing)); // not touched
        Assert.Equal(last!.BytesTotal, last.BytesDone);
    }

    [Fact]
    public void Same_size_but_different_bytes_is_still_a_conflict()
    {
        using var f = new TestFolder();
        string source = f.File(@"src\a.txt", "abc");
        f.File(@"dst\a.txt", "abd");
        var prompts = new FakePrompts();

        var result = Run.Job(CopyOperation.Copy, f.At("dst"), [source], prompts: prompts);

        Assert.Equal(1, prompts.ConflictCalls);
        Assert.Equal(0, result.Identical);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void An_unreadable_destination_counts_as_different()
    {
        using var f = new TestFolder();
        string source = f.File(@"src\a.txt", "same");
        string existing = f.File(@"dst\a.txt", "same");
        var prompts = new FakePrompts();

        JobResult result;
        using (new FileStream(existing, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = Run.Job(CopyOperation.Copy, f.At("dst"), [source], prompts: prompts);
        }

        Assert.Equal(1, prompts.ConflictCalls);
        Assert.Equal(0, result.Identical);
    }

    [Fact]
    public void Keep_both_makes_no_duplicate_of_an_identical_file()
    {
        using var f = new TestFolder();
        string source = f.File(@"src\a.txt", "same");
        f.File(@"dst\a.txt", "same");

        var result = Run.Job(CopyOperation.Copy, f.At("dst"), [source],
            new JobOptions { Conflicts = ConflictPolicy.Rename });

        Assert.Equal(1, result.Identical);
        Assert.False(File.Exists(f.At(@"dst\a (2).txt")));
    }

    [Fact]
    public void An_identical_tree_copies_nothing()
    {
        using var f = new TestFolder();
        foreach (string root in new[] { "src", "dst" })
        {
            f.File($@"{root}\tree\a.txt", "a");
            f.File($@"{root}\tree\sub\b.txt", "bb");
            f.File($@"{root}\tree\sub\empty.txt", "");
        }

        var result = Run.Copy(f.At("dst"), f.At(@"src\tree"));

        Run.AssertSucceeded(result);
        Assert.Equal(3, result.Identical);
        Assert.Equal(0, result.FilesCopied);
    }

    [Fact]
    public void A_move_across_volumes_just_drops_an_identical_source()
    {
        using var f = new TestFolder();
        f.File(@"src\tree\a.txt", "same");
        f.File(@"src\tree\b.txt", "new");
        f.File(@"dst\tree\a.txt", "same");
        var prompts = NeverAsked();

        var result = Run.Job(CopyOperation.Move, f.At("dst"), [f.At(@"src\tree")], prompts: prompts,
            forceCrossVolume: true);

        Run.AssertSucceeded(result);
        Assert.Equal(0, prompts.ConflictCalls);
        Assert.Equal(1, result.Identical);
        Assert.Equal(1, result.ItemsMoved);
        Assert.Equal(0, result.FilesCopied);
        Assert.Equal("new", File.ReadAllText(f.At(@"dst\tree\b.txt")));
        Assert.False(Directory.Exists(f.At(@"src\tree")), "every file accounted for, so the folder goes");
    }

    [Fact]
    public void A_move_within_a_volume_just_drops_an_identical_source()
    {
        using var f = new TestFolder();
        string source = f.File(@"src\a.txt", "same");
        f.File(@"dst\a.txt", "same");
        var prompts = NeverAsked();

        var result = Run.Job(CopyOperation.Move, f.At("dst"), [source], prompts: prompts);

        Run.AssertSucceeded(result);
        Assert.Equal(0, prompts.ConflictCalls);
        Assert.Equal(1, result.Identical);
        Assert.Equal(0, result.ItemsMoved);
        Assert.False(File.Exists(source));
        Assert.Equal("same", File.ReadAllText(f.At(@"dst\a.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_hard_link_is_never_deleted_as_its_own_copy(bool crossVolume)
    {
        using var f = new TestFolder();
        string source = f.File(@"src\a.txt", "the only copy");
        string link = f.At(@"dst\a.txt");
        Directory.CreateDirectory(f.At("dst"));
        Assert.True(CreateHardLink(link, source, IntPtr.Zero), $"CreateHardLink: {Marshal.GetLastPInvokeError()}");

        var result = Run.Job(CopyOperation.Move, f.At("dst"), [source], forceCrossVolume: crossVolume);

        Assert.True(File.Exists(source));
        Assert.Equal("the only copy", File.ReadAllText(link));
        Assert.Equal(0, result.Identical);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void An_identical_source_that_cannot_be_deleted_retries_as_a_delete_only()
    {
        using var f = new TestFolder();
        string source = f.File(@"src\a.txt", "same");
        f.File(@"dst\a.txt", "same");
        using var job = new CopyJob(new JobSpec(CopyOperation.Move, f.At("dst"), [source])) { ForceCrossVolume = true };

        JobResult result;
        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read)) // readable, not deletable
        {
            result = job.Run();
        }
        var failure = Assert.Single(result.Failures);
        Assert.Equal(FailureStage.DeleteSource, failure.Stage);
        Assert.Contains("already at the destination", failure.Message);

        result = job.RetryFailed();

        Run.AssertSucceeded(result);
        Assert.False(File.Exists(source));
        Assert.Equal(1, result.Identical);
        Assert.Equal(0, result.FilesCopied);
        Assert.Equal(0, result.ItemsMoved);
    }
}
