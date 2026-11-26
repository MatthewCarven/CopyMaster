using CopyMaster.Engine.Control;
using CopyMaster.Engine.Jobs;

namespace CopyMaster.Engine.Scheduling;

/// <summary>
/// CopyMaster.exe's queue: holds the jobs, starts each on a thread of its own when the
/// <see cref="Scheduler{T}"/> says its disks are free, and keeps the latest progress for
/// the window to read. All methods are safe from any thread; <see cref="Changed"/> is
/// raised on whichever thread made the change (the UI marshals it).
/// </summary>
public sealed class JobQueue
{
    private readonly Lock _gate = new();
    private readonly Scheduler<QueuedJob> _scheduler = new();
    private readonly Func<QueuedJob, IJobPrompts?>? _prompts;
    private readonly Func<JobSpec, IReadOnlySet<string>> _resources;
    private int _nextId;

    /// <param name="prompts">Makes the prompts (conflicts, a full disk) for each job, or null for none.</param>
    /// <param name="resources">What a job keeps busy; <see cref="Disks.ResourcesFor"/> unless a test says otherwise.</param>
    public JobQueue(Func<QueuedJob, IJobPrompts?>? prompts = null, Func<JobSpec, IReadOnlySet<string>>? resources = null)
    {
        _prompts = prompts;
        _resources = resources ?? Disks.ResourcesFor;
    }

    /// <summary>A job was added, removed, moved, started or finished.</summary>
    public event Action? Changed;

    /// <summary>All jobs in queue order, finished ones included.</summary>
    public IReadOnlyList<QueuedJob> Jobs
    {
        get { lock (_gate) return _scheduler.Entries.Select(e => e.Item).ToList(); }
    }

    /// <summary>Jobs running or waiting to run (not held, not finished).</summary>
    public int ActiveCount
    {
        get { lock (_gate) return _scheduler.Entries.Count(e => e.State is QueueState.Running or QueueState.Waiting); }
    }

    public int RunningCount
    {
        get { lock (_gate) return _scheduler.Entries.Count(e => e.State == QueueState.Running); }
    }

    public QueuedJob Add(JobSpec spec, JobOptions options, bool held = false) => Add(spec, _ => options, held);

    /// <param name="options">The job's options, chosen once its disks are known (a share has its own speed limit).</param>
    public QueuedJob Add(JobSpec spec, Func<IReadOnlySet<string>, JobOptions> options, bool held = false)
    {
        var resources = _resources(spec);
        var job = new QueuedJob(Interlocked.Increment(ref _nextId), spec, options(resources));
        job.Attach(new CopyJob(spec, job.Options, _prompts?.Invoke(job)));
        lock (_gate) job.Entry = _scheduler.Add(job, resources, held);
        Pump();
        return job;
    }

    public void MoveUp(QueuedJob job) => Change(() => _scheduler.Move(job.Entry, -1));

    public void MoveDown(QueuedJob job) => Change(() => _scheduler.Move(job.Entry, +1));

    /// <summary>Pauses a running job mid-copy (it keeps its place on its disks), or holds a waiting one.</summary>
    public void Pause(QueuedJob job)
    {
        lock (_gate)
        {
            if (job.Entry.State == QueueState.Running) job.Control.Pause();
            else _scheduler.Hold(job.Entry);
        }
        Pump();
    }

    public void Resume(QueuedJob job)
    {
        lock (_gate)
        {
            if (job.Entry.State == QueueState.Running) job.Control.Resume();
            else _scheduler.Release(job.Entry);
        }
        Pump();
    }

    /// <summary>Starts a waiting or held job now, beside whatever is using its disks.</summary>
    public void StartNow(QueuedJob job) => Change(() => _scheduler.StartNow(job.Entry));

    /// <summary>Cancels a running job (it stays listed, cancelled); removes one that hasn't started.</summary>
    public void Cancel(QueuedJob job)
    {
        bool running;
        lock (_gate)
        {
            running = job.Entry.State == QueueState.Running;
            if (running) job.Control.Cancel();
        }
        if (!running) Remove(job);
    }

    /// <summary>Takes a job that isn't running out of the list. False if it's running.</summary>
    public bool Remove(QueuedJob job)
    {
        bool removed;
        lock (_gate) removed = _scheduler.Remove(job.Entry);
        if (removed) job.Job.Dispose();
        Pump();
        return removed;
    }

    /// <summary>Removes every finished job.</summary>
    public void ClearFinished()
    {
        foreach (var job in Jobs.Where(j => j.State == QueueState.Done)) Remove(job);
    }

    /// <summary>Puts a finished job back in the queue to retry its failures. False if there's nothing to retry.</summary>
    public bool RetryFailed(QueuedJob job)
    {
        lock (_gate)
        {
            if (job.Entry.State != QueueState.Done || !job.CanRetry) return false;
            job.Retrying = true;
            _scheduler.Requeue(job.Entry);
        }
        Pump();
        return true;
    }

    /// <summary>Cancels every running job and removes the ones that haven't started (quitting).</summary>
    public void CancelAll()
    {
        foreach (var job in Jobs.Where(j => j.State != QueueState.Done)) Cancel(job);
    }

    private void Change(Action change)
    {
        lock (_gate) change();
        Pump();
    }

    /// <summary>Starts whatever can start, then tells the listeners.</summary>
    private void Pump()
    {
        List<QueueEntry<QueuedJob>> start;
        lock (_gate) start = _scheduler.Start();
        foreach (var entry in start)
        {
            var job = entry.Item;
            new Thread(() => RunJob(job)) { IsBackground = true, Name = $"CopyMaster job {job.Id}" }.Start();
        }
        Changed?.Invoke();
    }

    private void RunJob(QueuedJob job)
    {
        try
        {
            job.Result = job.Retrying ? job.Job.RetryFailed() : job.Job.Run();
        }
        catch (Exception ex)
        {
            // Copy failures are in the result; this is a bug, or a path Windows rejected outright.
            job.Crash = ex.Message;
        }
        lock (_gate)
        {
            job.Retrying = false;
            _scheduler.Finish(job.Entry);
        }
        Pump();
    }
}

/// <summary>A job in the <see cref="JobQueue"/>, as the window sees it.</summary>
public sealed class QueuedJob
{
    private volatile JobProgress? _progress;

    internal QueuedJob(int id, JobSpec spec, JobOptions options)
    {
        Id = id;
        Spec = spec;
        Options = options;
    }

    /// <summary>1, 2, 3, ... in the order jobs arrived.</summary>
    public int Id { get; }

    public JobSpec Spec { get; }

    public JobOptions Options { get; }

    public CopyJob Job { get; private set; } = null!;

    /// <summary>Pause, cancel and the speed limit.</summary>
    public JobControl Control => Job.Control;

    public QueueState State => Entry.State;

    public IReadOnlySet<string> Resources => Entry.Resources;

    /// <summary>For a waiting job, the job holding it up.</summary>
    public QueuedJob? WaitingFor => Entry.WaitingFor?.Item;

    /// <summary>The latest progress, or null before the job has started.</summary>
    public JobProgress? Progress => _progress;

    /// <summary>The result once the job has run (its failures are retried in place).</summary>
    public JobResult? Result { get; internal set; }

    /// <summary>The message of an unexpected exception that ended the job, or null.</summary>
    public string? Crash { get; internal set; }

    /// <summary>
    /// Finished with failures that can be tried again, and not cancelled or stopped (a job
    /// stopped by a full disk counts as cancelled: what it never reached isn't a failure).
    /// </summary>
    public bool CanRetry =>
        State == QueueState.Done && Crash is null && !Control.IsCancelled && Result is { } result &&
        result.Failures.Any(f => f.CanRetry);

    internal QueueEntry<QueuedJob> Entry { get; set; } = null!;

    internal bool Retrying { get; set; }

    internal void Attach(CopyJob job)
    {
        Job = job;
        job.ProgressChanged += p => _progress = p;
    }
}
