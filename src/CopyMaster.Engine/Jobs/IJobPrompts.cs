namespace CopyMaster.Engine.Jobs;

/// <summary>
/// Questions a job asks while it runs. Called on the job's own thread, which waits for the
/// answer (the UI shows a dialog; other jobs carry on). A job with no prompts skips
/// conflicts and stops when the disk is full.
/// </summary>
public interface IJobPrompts
{
    ConflictAnswer ResolveConflict(ConflictInfo conflict);

    /// <summary>The destination disk filled up mid-copy. The partial file is already deleted.</summary>
    DiskFullAnswer DiskFull(string destination, string message);

    /// <summary>
    /// Asked once, before copying starts, when the files to copy add up to more than the
    /// destination has free. True to start anyway (some may be skipped as conflicts, or the
    /// user will make room), false to stop the job. Without prompts a job just starts.
    /// </summary>
    bool StartWithoutRoom(string destination, long bytesNeeded, long bytesFree) => true;
}

public enum DiskFullAnswer
{
    /// <summary>Copy the file again (after making room).</summary>
    Retry,
    /// <summary>Record the file as failed and carry on with the rest.</summary>
    SkipFile,
    /// <summary>Stop the job.</summary>
    StopJob,
}
