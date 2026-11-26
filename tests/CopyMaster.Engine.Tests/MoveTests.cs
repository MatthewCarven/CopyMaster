using CopyMaster.Engine.Jobs;

namespace CopyMaster.Engine.Tests;

public class MoveTests
{
    [Fact]
    public void A_move_within_a_volume_is_one_rename()
    {
        using var f = new TestFolder();
        f.File(@"src\tree\a.txt", "a");
        f.File(@"src\tree\sub\b.txt", "b");

        var result = Run.Move(f.Folder("dst"), f.At(@"src\tree"));

        Run.AssertSucceeded(result);
        Assert.Equal(1, result.ItemsMoved);
        Assert.Equal(0, result.FilesCopied);
        Assert.False(Directory.Exists(f.At(@"src\tree")));
        Assert.Equal("b", File.ReadAllText(f.At(@"dst\tree\sub\b.txt")));
    }

    [Fact]
    public void A_move_merges_into_a_folder_that_is_already_there()
    {
        using var f = new TestFolder();
        f.File(@"src\tree\a.txt", "a");
        f.File(@"src\tree\sub\b.txt", "b");
        f.File(@"dst\tree\x.txt", "x");

        var result = Run.Move(f.At("dst"), f.At(@"src\tree"));

        Run.AssertSucceeded(result);
        Assert.Equal("x", File.ReadAllText(f.At(@"dst\tree\x.txt")));
        Assert.Equal("a", File.ReadAllText(f.At(@"dst\tree\a.txt")));
        Assert.Equal("b", File.ReadAllText(f.At(@"dst\tree\sub\b.txt")));
        Assert.False(Directory.Exists(f.At(@"src\tree")), "the emptied source folder goes");
    }

    [Fact]
    public void A_move_to_another_volume_copies_then_deletes()
    {
        using var f = new TestFolder();
        f.File(@"src\tree\a.txt", "a");
        string readOnly = f.File(@"src\tree\sub\b.txt", "b");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        var result = Run.Job(CopyOperation.Move, f.Folder("dst"), [f.At(@"src\tree")], forceCrossVolume: true);

        Run.AssertSucceeded(result);
        Assert.Equal(2, result.ItemsMoved);
        Assert.Equal(0, result.FilesCopied);
        Assert.Equal("b", File.ReadAllText(f.At(@"dst\tree\sub\b.txt")));
        Assert.False(Directory.Exists(f.At(@"src\tree")));
    }

    [Fact]
    public void A_skipped_conflict_keeps_its_source()
    {
        using var f = new TestFolder();
        f.File(@"src\tree\a.txt", "new a");
        f.File(@"src\tree\b.txt", "b");
        f.File(@"dst\tree\a.txt", "old a");

        var result = Run.Job(CopyOperation.Move, f.At("dst"), [f.At(@"src\tree")],
            new JobOptions { Conflicts = ConflictPolicy.Skip }, forceCrossVolume: true);

        Run.AssertSucceeded(result);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("new a", File.ReadAllText(f.At(@"src\tree\a.txt")));
        Assert.Equal("old a", File.ReadAllText(f.At(@"dst\tree\a.txt")));
        Assert.False(File.Exists(f.At(@"src\tree\b.txt")));
        Assert.True(Directory.Exists(f.At(@"src\tree")), "not empty, so it stays");
    }

    [Fact]
    public void A_rename_move_can_overwrite()
    {
        using var f = new TestFolder();
        string source = f.File(@"src\a.txt", "new");
        f.File(@"dst\a.txt", "old");

        var result = Run.Job(CopyOperation.Move, f.At("dst"), [source],
            new JobOptions { Conflicts = ConflictPolicy.Overwrite });

        Run.AssertSucceeded(result);
        Assert.Equal("new", File.ReadAllText(f.At(@"dst\a.txt")));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void Moving_into_its_own_folder_does_nothing()
    {
        using var f = new TestFolder();
        string file = f.File(@"src\a.txt", "a");

        var result = Run.Move(f.At("src"), file);

        Run.AssertSucceeded(result);
        Assert.True(File.Exists(file));
        Assert.Single(result.Notes);
    }
}
