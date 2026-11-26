using CopyMaster.Engine;
using CopyMaster.Engine.Jobs;
using CopyMaster.Engine.Scheduling;

namespace CopyMaster;

/// <summary>
/// A job's questions, asked in the window. The engine calls these on the job's own thread;
/// each shows a dialog on the UI thread and blocks that job (only) until it's answered.
/// The dialogs are modeless, so the queue window stays usable and several jobs can ask
/// at once. Cancelling the job closes its dialog.
/// </summary>
internal sealed class UiPrompts(MainForm window, QueuedJob job) : IJobPrompts
{
    private volatile bool _asking;

    /// <summary>A dialog of this job's is open, waiting for an answer.</summary>
    public bool Asking => _asking;

    public ConflictAnswer ResolveConflict(ConflictInfo conflict) =>
        Ask(() => new ConflictForm(conflict, job), form => form.Answer);

    public DiskFullAnswer DiskFull(string destination, string message)
    {
        int choice = Ask(() => new QuestionForm("Disk full · " + JobText.Title(job.Spec),
                "The destination disk is full",
                $"{message}\n\nThe file being copied when it filled up has been removed. Make some room on " +
                $"the disk holding {destination}, then try again; or skip that file; or stop the job here " +
                "(what's copied so far stays).",
                ["Try again", "Skip this file", "Stop the job"], closeAnswer: 2),
            form => form.Choice);
        return choice switch
        {
            0 => DiskFullAnswer.Retry,
            1 => DiskFullAnswer.SkipFile,
            _ => DiskFullAnswer.StopJob,
        };
    }

    public bool StartWithoutRoom(string destination, long bytesNeeded, long bytesFree) =>
        Ask(() => new QuestionForm("Not enough room · " + JobText.Title(job.Spec),
                "This job may not fit",
                $"It has {Units.Bytes(bytesNeeded)} to copy, and {destination} has {Units.Bytes(bytesFree)} free. " +
                "Files skipped as conflicts or overwritten would need less. If the disk fills up, the job pauses and asks.",
                ["Start anyway", "Cancel the job"], closeAnswer: 1),
            form => form.Choice) == 0;

    private T Ask<TForm, T>(Func<TForm> create, Func<TForm, T> answer) where TForm : Form
    {
        var token = job.Control.Token;
        token.ThrowIfCancellationRequested();

        var reply = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        TForm? dialog = null; // touched only on the UI thread
        _asking = true;
        try
        {
            window.BeginInvoke(() =>
            {
                if (reply.Task.IsCompleted) return; // cancelled before it could be shown
                dialog = create();
                dialog.FormClosed += (_, _) => reply.TrySetResult(answer(dialog)); // then it disposes itself
                window.ShowQuestion(dialog);
            });

            using var cancelled = token.Register(() =>
            {
                reply.TrySetCanceled(token);
                window.BeginInvokeQuietly(() => dialog?.Close());
            });
            return reply.Task.GetAwaiter().GetResult(); // a TaskCanceledException cancels the job
        }
        catch (InvalidOperationException)
        {
            // The window is gone: CopyMaster is quitting.
            throw new OperationCanceledException(token);
        }
        finally
        {
            _asking = false;
        }
    }
}
