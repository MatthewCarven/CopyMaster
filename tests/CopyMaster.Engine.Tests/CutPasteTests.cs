using CopyMaster.Engine.Jobs;

namespace CopyMaster.Engine.Tests;

// A Cut in Explorer pasted with CopyMaster Paste: the job carries the clipboard's sequence number, and
// once everything has moved it empties the clipboard if the number still matches. These tests never
// touch the real clipboard: CopyJob's EmptyClipboard seam stands in for it. (ShellExtensionTests runs
// the real thing end to end, on a window station of its own.)
public sealed class CutPasteTests
{
    private const uint Sequence = 4242;

    private sealed class FakeClipboard
    {
        public List<uint> Calls { get; } = [];

        public ClipboardOutcome Answer { get; init; } = ClipboardOutcome.Emptied;

        public ClipboardOutcome Empty(uint sequence)
        {
            Calls.Add(sequence);
            return Answer;
        }
    }

    private static CopyJob Job(CopyOperation operation, string destination, IEnumerable<string> sources,
        FakeClipboard clipboard, uint? sequence = Sequence) =>
        new(new JobSpec(operation, destination, sources.ToList()) { ClipboardSequence = sequence })
        {
            EmptyClipboard = clipboard.Empty,
        };

    [Fact]
    public void A_cut_that_moves_everything_empties_the_clipboard_if_it_still_holds_the_cut()
    {
        using var f = new TestFolder();
        string[] sources = [f.File(@"from\a.txt", "a"), f.File(@"from\b\c.txt", "c")];
        sources[1] = Path.GetDirectoryName(sources[1])!; // a file and a folder
        var clipboard = new FakeClipboard();
        using var job = Job(CopyOperation.Move, f.Folder("to"), sources, clipboard);

        var result = job.Run();

        Run.AssertSucceeded(result);
        Assert.True(File.Exists(f.At("to", "a.txt")));
        Assert.Equal([Sequence], clipboard.Calls);
        Assert.Equal(ClipboardOutcome.Emptied, result.Clipboard);
    }

    [Fact]
    public void What_the_clipboard_said_is_kept_in_the_result()
    {
        using var f = new TestFolder();
        var clipboard = new FakeClipboard { Answer = ClipboardOutcome.Changed };
        using var job = Job(CopyOperation.Move, f.Folder("to"), [f.File(@"from\a.txt", "a")], clipboard);

        var result = job.Run();

        Run.AssertSucceeded(result); // the move is done either way; the clipboard is a courtesy
        Assert.Equal(ClipboardOutcome.Changed, result.Clipboard);
    }

    [Fact]
    public void A_copy_never_touches_the_clipboard()
    {
        using var f = new TestFolder();
        var clipboard = new FakeClipboard();
        using var job = Job(CopyOperation.Copy, f.Folder("to"), [f.File(@"from\a.txt", "a")], clipboard);

        var result = job.Run();

        Run.AssertSucceeded(result);
        Assert.Empty(clipboard.Calls);
        Assert.Null(result.Clipboard);
    }

    [Fact]
    public void A_move_without_a_clipboard_number_never_touches_the_clipboard()
    {
        using var f = new TestFolder();
        var clipboard = new FakeClipboard();
        using var job = Job(CopyOperation.Move, f.Folder("to"), [f.File(@"from\a.txt", "a")], clipboard, sequence: null);

        Run.AssertSucceeded(job.Run());
        Assert.Empty(clipboard.Calls);
    }

    [Fact]
    public void A_cut_with_a_failure_stays_on_the_clipboard_until_a_retry_moves_the_rest()
    {
        using var f = new TestFolder();
        string a = f.File(@"from\a.txt", "a");
        string b = f.File(@"from\b.txt", "b");
        var clipboard = new FakeClipboard();
        using var job = Job(CopyOperation.Move, f.Folder("to"), [a, b], clipboard);

        JobResult result;
        using (new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.None)) // can't be moved while open
        {
            result = job.Run();
        }
        Assert.Single(result.Failures);
        Assert.Empty(clipboard.Calls);
        Assert.Null(result.Clipboard);

        result = job.RetryFailed();

        Run.AssertSucceeded(result);
        Assert.Equal([Sequence], clipboard.Calls);
        Assert.Equal(ClipboardOutcome.Emptied, result.Clipboard);
    }

    [Fact]
    public void A_cut_pasted_into_its_own_folder_stays_on_the_clipboard()
    {
        using var f = new TestFolder();
        string a = f.File(@"from\a.txt", "a");
        var clipboard = new FakeClipboard();
        using var job = Job(CopyOperation.Move, f.At("from"), [a], clipboard);

        var result = job.Run();

        Run.AssertSucceeded(result); // a note ("Already in that folder"), not a failure
        Assert.NotEmpty(result.Notes);
        Assert.Empty(clipboard.Calls); // nothing moved: the cut can still be pasted where it was meant to go
    }

    [Fact]
    public void A_cut_whose_files_were_all_there_already_empties_the_clipboard()
    {
        using var f = new TestFolder();
        string a = f.File(@"from\a.txt", "same");
        f.File(@"to\a.txt", "same");
        var clipboard = new FakeClipboard();
        using var job = Job(CopyOperation.Move, f.At("to"), [a], clipboard);

        var result = job.Run();

        Run.AssertSucceeded(result);
        Assert.Equal(1, result.Identical);
        Assert.False(File.Exists(a)); // the source went, as a move's does
        Assert.Equal([Sequence], clipboard.Calls);
    }

    [Fact]
    public void A_cancelled_cut_stays_on_the_clipboard()
    {
        using var f = new TestFolder();
        var clipboard = new FakeClipboard();
        using var job = Job(CopyOperation.Move, f.Folder("to"), [f.File(@"from\a.txt", "a")], clipboard);
        job.Control.Cancel();

        var result = job.Run();

        Assert.True(result.Cancelled);
        Assert.Empty(clipboard.Calls);
    }

    [Fact]
    public void A_missing_source_keeps_the_cut_on_the_clipboard()
    {
        using var f = new TestFolder();
        var clipboard = new FakeClipboard();
        using var job = Job(CopyOperation.Move, f.Folder("to"), [f.File(@"from\a.txt", "a"), f.At("from", "gone.txt")], clipboard);

        var result = job.Run();

        Assert.False(result.Succeeded);
        Assert.Empty(clipboard.Calls);
    }

    [Fact]
    public void Number_0_never_empties_the_clipboard()
    {
        // 0 is what GetClipboardSequenceNumber says without access to the clipboard: no Win32 call at all.
        Assert.Equal(ClipboardOutcome.Changed, CutPaste.EmptyClipboardIfUnchanged(0));
    }
}
