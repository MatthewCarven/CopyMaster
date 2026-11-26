using CopyMaster.Engine.Planning;

namespace CopyMaster.Engine.Jobs;

public enum FailureStage
{
    /// <summary>Found while listing the sources (missing, unreadable folder, ...). Not retried.</summary>
    Plan,
    /// <summary>Copying, moving or making a folder failed.</summary>
    Copy,
    /// <summary>The copy is done, but the original couldn't be deleted (a move across volumes).</summary>
    DeleteSource,
}

/// <param name="HResult">The failure's HRESULT (0x8007xxxx wraps a Win32 error code), or 0.</param>
public sealed record FailedItem(string Source, string? Destination, string Message, int HResult, FailureStage Stage)
{
    internal PlanItem? Item { get; init; }

    /// <summary>For a folder that couldn't be made: everything that was to go inside it.</summary>
    internal IReadOnlyList<PlanItem> Subtree { get; init; } = [];

    /// <summary>A DeleteSource failure for a file that was already at the destination (nothing was copied).</summary>
    internal bool Identical { get; init; }

    public bool CanRetry => Item is not null;
}

public sealed class JobResult
{
    private readonly List<FailedItem> _failures = [];
    private readonly List<string> _notes = [];

    public int FilesCopied { get; internal set; }

    /// <summary>Files and folders moved: renames within a volume, plus files copied then deleted.</summary>
    public int ItemsMoved { get; internal set; }

    public int Skipped { get; internal set; }

    /// <summary>
    /// Files the destination already had, byte for byte: nothing copied and nothing asked.
    /// In a move their sources were deleted. Not counted in FilesCopied or ItemsMoved.
    /// </summary>
    public int Identical { get; internal set; }

    public long BytesCopied { get; internal set; }

    public IReadOnlyList<FailedItem> Failures => _failures;

    /// <summary>Things worth telling that aren't failures (a link to another folder skipped, ...).</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Someone cancelled the job.</summary>
    public bool Cancelled { get; internal set; }

    /// <summary>Why the job stopped by itself before the end (the disk filled up), or null.</summary>
    public string? StoppedReason { get; internal set; }

    public TimeSpan Elapsed { get; internal set; }

    public bool Succeeded => !Cancelled && StoppedReason is null && _failures.Count == 0;

    internal void Add(FailedItem failure) => _failures.Add(failure);

    internal void AddNote(string note) => _notes.Add(note);

    /// <summary>Removes and returns the failures that can be retried.</summary>
    internal List<FailedItem> TakeRetryable()
    {
        var retry = _failures.Where(f => f.CanRetry).ToList();
        _failures.RemoveAll(f => f.CanRetry);
        return retry;
    }
}
