namespace CopyMaster.Engine.Jobs;

/// <summary>What to do: the operation, where to, and what. Written by the shell DLL as a job file.</summary>
public sealed record JobSpec(CopyOperation Operation, string Destination, IReadOnlyList<string> Sources)
{
    /// <summary>
    /// For a cut-then-paste: the clipboard's sequence number when Paste was chosen. Once the
    /// move succeeds, the clipboard is emptied only if it still holds that cut (phase 4).
    /// </summary>
    public uint? ClipboardSequence { get; init; }
}

/// <summary>How to do it.</summary>
public sealed record JobOptions
{
    /// <summary>Compare each copy with its source, read back from the disk.</summary>
    public bool Verify { get; init; }

    /// <summary>Bytes per second, or null for unlimited. Can be changed mid-job through JobControl.</summary>
    public long? SpeedLimit { get; init; }

    public ConflictPolicy Conflicts { get; init; } = ConflictPolicy.Ask;
}
