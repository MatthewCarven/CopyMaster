namespace CopyMaster.Engine.Jobs;

public enum JobPhase
{
    Scanning,
    Copying,
    Verifying,
    /// <summary>A file of the same size is already at the destination: checking whether it's identical.</summary>
    Comparing,
    Moving,
    Finishing,
    Done,
}

/// <summary>
/// A snapshot of a running job. Bytes count each stage: with verify on, every file
/// counts twice (copied, then compared), so the bar moves steadily through both.
/// </summary>
public sealed record JobProgress(
    JobPhase Phase,
    int ItemsDone,
    int ItemsTotal,
    long BytesDone,
    long BytesTotal,
    string? CurrentSource,
    double BytesPerSecond);
