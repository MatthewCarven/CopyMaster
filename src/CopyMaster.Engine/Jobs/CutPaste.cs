using CopyMaster.Engine.Native;

namespace CopyMaster.Engine.Jobs;

/// <summary>What happened to the clipboard at the end of a cut-and-paste.</summary>
public enum ClipboardOutcome
{
    /// <summary>It still held the cut, and now holds nothing.</summary>
    Emptied,
    /// <summary>Something else was copied since (or the job had no usable number): left alone.</summary>
    Changed,
    /// <summary>Another program kept it open: left alone.</summary>
    Busy,
    /// <summary>Emptying it didn't finish in time (it tells the old owner, which may be hung).</summary>
    NoAnswer,
    /// <summary>Windows refused to empty it.</summary>
    Failed,
}

/// <summary>
/// The end of a Cut in Explorer that was pasted with CopyMaster Paste. Explorer empties the clipboard
/// once a cut has been pasted, so the same files can't be pasted again from where they no longer are.
/// CopyMaster's move finishes later, in this process, so the shell DLL writes the clipboard's sequence
/// number into the job (<see cref="JobSpec.ClipboardSequence"/>), and the job empties the clipboard when
/// it's done, but only if that number hasn't changed: whatever was copied since stays.
/// </summary>
public static class CutPaste
{
    private const int OpenTries = 10;
    private static readonly TimeSpan OpenPause = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Whether a finished job completes a cut: a move from a cut-and-paste (it has a clipboard
    /// number) in which nothing failed and something moved, or was already there. A cut pasted into
    /// its own folder moves nothing, so it stays on the clipboard to be pasted where it was meant to go.
    /// </summary>
    public static bool Completes(JobSpec spec, JobResult result) =>
        spec.Operation == CopyOperation.Move && spec.ClipboardSequence is not null &&
        result.Succeeded && (result.ItemsMoved > 0 || result.Identical > 0);

    /// <summary>
    /// Empties the clipboard if its sequence number is still <paramref name="sequence"/>, as Explorer
    /// does after pasting a cut. On a thread of its own, waiting at most two seconds: emptying the
    /// clipboard tells the program that owns it, and a hung one mustn't hold up the job.
    /// </summary>
    public static ClipboardOutcome EmptyClipboardIfUnchanged(uint sequence)
    {
        if (sequence == 0) return ClipboardOutcome.Changed; // 0 is what a window station without access says
        var outcome = ClipboardOutcome.NoAnswer;
        var thread = new Thread(() => outcome = Empty(sequence)) { IsBackground = true, Name = "CopyMaster clipboard" };
        thread.Start();
        return thread.Join(Patience) ? outcome : ClipboardOutcome.NoAnswer;
    }

    private static ClipboardOutcome Empty(uint sequence)
    {
        bool open = false;
        for (int i = 0; i < OpenTries && !open; i++)
        {
            if (i > 0) Thread.Sleep(OpenPause);
            open = NativeMethods.OpenClipboard(0);
        }
        if (!open) return ClipboardOutcome.Busy;
        try
        {
            // Compared while it's open, so nothing can be copied between the check and the emptying.
            if (NativeMethods.GetClipboardSequenceNumber() != sequence) return ClipboardOutcome.Changed;
            return NativeMethods.EmptyClipboard() ? ClipboardOutcome.Emptied : ClipboardOutcome.Failed;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }
}
