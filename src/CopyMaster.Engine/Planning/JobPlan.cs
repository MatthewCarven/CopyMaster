using CopyMaster.Engine.Jobs;

namespace CopyMaster.Engine.Planning;

internal enum PlanItemKind
{
    /// <summary>Create the destination folder (or merge into the one that's there).</summary>
    MakeFolder,
    /// <summary>Copy one file; for a move across volumes, delete the source afterwards.</summary>
    CopyFile,
    /// <summary>Move a file or a whole folder by renaming it (same volume).</summary>
    Rename,
}

internal sealed class PlanItem
{
    public required PlanItemKind Kind { get; init; }
    public required string Source { get; init; }
    public required string Destination { get; init; }

    /// <summary>CopyFile: the file's size when listed.</summary>
    public long Length { get; init; }

    /// <summary>Rename: whether it's a folder.</summary>
    public bool IsFolder { get; init; }

    /// <summary>CopyFile: delete the source once copied (a move across volumes).</summary>
    public bool DeleteSource { get; init; }

    public bool CountsAsItem => Kind != PlanItemKind.MakeFolder;
}

/// <summary>Everything a job will do, in order: folders before what goes in them.</summary>
internal sealed class JobPlan
{
    public List<PlanItem> Items { get; } = [];

    /// <summary>Moves: source folders to delete at the end if they're empty, children first.</summary>
    public List<string> SourceFoldersToRemove { get; } = [];

    public List<FailedItem> Problems { get; } = [];

    public List<string> Notes { get; } = [];
}
