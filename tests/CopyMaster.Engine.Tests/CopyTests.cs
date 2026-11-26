using CopyMaster.Engine.Jobs;

namespace CopyMaster.Engine.Tests;

public class CopyTests
{
    [Fact]
    public void Keeps_dates_attributes_and_the_downloaded_mark()
    {
        using var f = new TestFolder();
        string source = f.File(@"src\a.txt", "hello");
        File.WriteAllText(source + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var created = new DateTime(2019, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var modified = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetCreationTimeUtc(source, created);
        File.SetLastWriteTimeUtc(source, modified);
        File.SetAttributes(source, FileAttributes.ReadOnly | FileAttributes.Hidden);
        string destination = f.Folder("dst");

        var result = Run.Copy(destination, source);

        Run.AssertSucceeded(result);
        Assert.Equal(1, result.FilesCopied);
        string copy = Path.Combine(destination, "a.txt");
        Assert.Equal("hello", File.ReadAllText(copy));
        Assert.Contains("ZoneId=3", File.ReadAllText(copy + ":Zone.Identifier"));
        Assert.Equal(created, File.GetCreationTimeUtc(copy));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(copy));
        Assert.True(File.GetAttributes(copy).HasFlag(FileAttributes.ReadOnly | FileAttributes.Hidden));
        Run.AssertNoTempFiles(destination);
    }

    [Fact]
    public void Copies_a_whole_tree_with_empty_and_hidden_parts_and_folder_dates()
    {
        using var f = new TestFolder();
        f.File(@"src\tree\a.txt", "a");
        f.File(@"src\tree\sub\b.txt", "b");
        f.Folder(@"src\tree\sub\empty");
        string hidden = f.File(@"src\tree\.secret", "s");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);
        var folderDate = new DateTime(2018, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(f.At(@"src\tree\sub"), folderDate);
        string destination = f.Folder("dst");

        var result = Run.Copy(destination, f.At(@"src\tree"));

        Run.AssertSucceeded(result);
        Assert.Equal(3, result.FilesCopied);
        Assert.Equal("a", File.ReadAllText(f.At(@"dst\tree\a.txt")));
        Assert.Equal("b", File.ReadAllText(f.At(@"dst\tree\sub\b.txt")));
        Assert.Equal("s", File.ReadAllText(f.At(@"dst\tree\.secret")));
        Assert.True(Directory.Exists(f.At(@"dst\tree\sub\empty")));
        Assert.Equal(folderDate, Directory.GetLastWriteTimeUtc(f.At(@"dst\tree\sub")));
        Assert.True(File.Exists(f.At(@"src\tree\a.txt")), "a copy leaves the source alone");
    }

    [Fact]
    public void A_copy_into_its_own_folder_does_nothing()
    {
        using var f = new TestFolder();
        string file = f.File(@"src\a.txt", "a");
        f.File(@"src\tree\b.txt", "b");

        var result = Run.Copy(f.At("src"), file, f.At(@"src\tree") + @"\"); // a trailing \ too

        Run.AssertSucceeded(result);
        Assert.Equal(0, result.FilesCopied);
        Assert.Equal(new[] { "a.txt", "tree" }, Directory.GetFileSystemEntries(f.At("src")).Select(p => Path.GetFileName(p)).Order());
        Assert.Equal(new[] { "b.txt" }, Directory.GetFileSystemEntries(f.At(@"src\tree")).Select(p => Path.GetFileName(p)).Order());
        Assert.Equal(2, result.Notes.Count(n => n.StartsWith("Already in that folder: ", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_copy_from_two_folders_skips_only_what_is_already_there()
    {
        using var f = new TestFolder();
        string here = f.File(@"dst\a.txt", "a");
        string there = f.File(@"src\b.txt", "b");

        var result = Run.Copy(f.At("dst"), here, there);

        Run.AssertSucceeded(result);
        Assert.Equal(1, result.FilesCopied);
        Assert.Equal("b", File.ReadAllText(f.At(@"dst\b.txt")));
        Assert.Equal(new[] { "a.txt", "b.txt" }, Directory.GetFileSystemEntries(f.At("dst")).Select(p => Path.GetFileName(p)).Order());
        Assert.Single(result.Notes);
    }

    [Fact]
    public void Refuses_to_copy_a_folder_into_itself()
    {
        using var f = new TestFolder();
        f.File(@"tree\a.txt", "a");
        string inside = f.Folder(@"tree\sub");

        var result = Run.Copy(inside, f.At("tree"));

        var failure = Assert.Single(result.Failures);
        Assert.Equal(FailureStage.Plan, failure.Stage);
        Assert.Empty(Directory.EnumerateFileSystemEntries(inside));
    }

    [Fact]
    public void Reports_a_missing_source_and_copies_the_rest()
    {
        using var f = new TestFolder();
        string real = f.File(@"src\a.txt", "a");
        string destination = f.Folder("dst");

        var result = Run.Copy(destination, f.At(@"src\gone.txt"), real);

        Assert.Single(result.Failures);
        Assert.Equal(1, result.FilesCopied);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Stops_when_the_destination_folder_is_missing()
    {
        using var f = new TestFolder();
        var result = Run.Copy(f.At("nowhere"), f.File("a.txt", "a"));
        Assert.NotNull(result.StoppedReason);
        Assert.False(result.Cancelled);
    }

    [Theory]
    [InlineData(ConflictPolicy.Skip, "old", null)]
    [InlineData(ConflictPolicy.Overwrite, "new", null)]
    [InlineData(ConflictPolicy.Rename, "old", "new")]
    [InlineData(ConflictPolicy.KeepNewer, "new", null)]
    public void Conflict_policies(ConflictPolicy policy, string expected, string? expectedRenamed)
    {
        using var f = new TestFolder();
        string source = f.File(@"src\a.txt", "new");
        File.SetLastWriteTimeUtc(source, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        string old = f.File(@"dst\a.txt", "old");
        File.SetLastWriteTimeUtc(old, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = Run.Job(CopyOperation.Copy, f.At("dst"), [source], new JobOptions { Conflicts = policy });

        Run.AssertSucceeded(result);
        Assert.Equal(expected, File.ReadAllText(old));
        if (expectedRenamed is null) Assert.False(File.Exists(f.At(@"dst\a (2).txt")));
        else Assert.Equal(expectedRenamed, File.ReadAllText(f.At(@"dst\a (2).txt")));
        Run.AssertNoTempFiles(f.At("dst"));
    }

    [Fact]
    public void Keep_newer_leaves_a_newer_destination_alone()
    {
        using var f = new TestFolder();
        string source = f.File(@"src\a.txt", "older");
        File.SetLastWriteTimeUtc(source, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        string newer = f.File(@"dst\a.txt", "newer");

        var result = Run.Job(CopyOperation.Copy, f.At("dst"), [source],
            new JobOptions { Conflicts = ConflictPolicy.KeepNewer });

        Assert.Equal(1, result.Skipped);
        Assert.Equal("newer", File.ReadAllText(newer));
    }

    [Fact]
    public void Overwrite_replaces_read_only_and_hidden_files()
    {
        using var f = new TestFolder();
        string source = f.File(@"src\a.txt", "new");
        string old = f.File(@"dst\a.txt", "old");
        File.SetAttributes(old, FileAttributes.ReadOnly | FileAttributes.Hidden);

        var result = Run.Job(CopyOperation.Copy, f.At("dst"), [source],
            new JobOptions { Conflicts = ConflictPolicy.Overwrite });

        Run.AssertSucceeded(result);
        Assert.Equal("new", File.ReadAllText(old));
    }

    [Fact]
    public void Asks_about_conflicts_and_honours_do_this_for_all()
    {
        using var f = new TestFolder();
        string[] sources = [f.File(@"src\a.txt", "1"), f.File(@"src\b.txt", "2"), f.File(@"src\c.txt", "3")];
        foreach (string name in new[] { "a", "b", "c" }) f.File($@"dst\{name}.txt", "old");
        var prompts = new FakePrompts { OnConflict = _ => new(ConflictResolution.Overwrite, ApplyToAll: true) };

        var result = Run.Job(CopyOperation.Copy, f.At("dst"), sources, prompts: prompts);

        Run.AssertSucceeded(result);
        Assert.Equal(1, prompts.ConflictCalls);
        Assert.Equal("3", File.ReadAllText(f.At(@"dst\c.txt")));
    }

    [Fact]
    public void With_no_one_to_ask_a_conflict_is_skipped()
    {
        using var f = new TestFolder();
        string source = f.File(@"src\a.txt", "new");
        f.File(@"dst\a.txt", "old");

        var result = Run.Copy(f.At("dst"), source);

        Assert.Equal(1, result.Skipped);
        Assert.Equal("old", File.ReadAllText(f.At(@"dst\a.txt")));
    }

    [Fact]
    public void Cancel_in_the_conflict_dialog_cancels_the_job()
    {
        using var f = new TestFolder();
        string source = f.File(@"src\a.txt", "new");
        f.File(@"dst\a.txt", "old");
        var prompts = new FakePrompts { OnConflict = _ => new(ConflictResolution.CancelJob) };

        var result = Run.Job(CopyOperation.Copy, f.At("dst"), [source], prompts: prompts);

        Assert.True(result.Cancelled);
    }

    [Fact]
    public void Paths_longer_than_260_characters_work()
    {
        using var f = new TestFolder();
        string deep = Path.Combine(Enumerable.Range(0, 6).Select(i => $"folder-{i}-" + new string('x', 40)).ToArray());
        string source = f.File(Path.Combine("src", "tree", deep, "deep.txt"), "deep");
        Assert.True(source.Length > 300);

        var result = Run.Copy(f.Folder("dst"), f.At(@"src\tree"));

        Run.AssertSucceeded(result);
        Assert.Equal("deep", File.ReadAllText(Path.Combine(f.At(@"dst\tree"), deep, "deep.txt")));
    }

    [Fact]
    public void Links_to_folders_inside_a_tree_are_skipped()
    {
        using var f = new TestFolder();
        f.File(@"src\tree\a.txt", "a");
        string target = f.Folder(@"src\elsewhere");
        f.File(@"src\elsewhere\big.txt", "not wanted");
        try
        {
            Directory.CreateSymbolicLink(f.At(@"src\tree\link"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // no right to make links here; nothing to test
        }

        var result = Run.Copy(f.Folder("dst"), f.At(@"src\tree"));

        Run.AssertSucceeded(result);
        Assert.True(File.Exists(f.At(@"dst\tree\a.txt")));
        Assert.False(Path.Exists(f.At(@"dst\tree\link")));
        Assert.Contains(result.Notes, n => n.Contains("link", StringComparison.OrdinalIgnoreCase));
    }
}
