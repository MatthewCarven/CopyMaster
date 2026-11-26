using CopyMaster.Engine.Jobs;
using CopyMaster.Engine.Scheduling;

namespace CopyMaster.Engine.Tests;

/// <summary>
/// The job queue with real copies. A job is held "running" by a conflict prompt that
/// blocks until the test lets it go, so nothing here depends on timing.
/// </summary>
public sealed class QueueTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly TestFolder _folder = new();
    private readonly Dictionary<string, string[]> _disks = new(); // destination → its resources
    private readonly Dictionary<string, Gate> _gates = new();      // destination → its blocking prompt
    private readonly JobQueue _queue;

    public QueueTests()
    {
        _queue = new JobQueue(
            prompts: job => _gates.TryGetValue(job.Spec.Destination, out var gate) ? gate : null,
            resources: spec => _disks[spec.Destination].ToHashSet());
    }

    public void Dispose()
    {
        foreach (var gate in _gates.Values) gate.Open();
        _queue.CancelAll();
        WaitUntil(() => _queue.RunningCount == 0);
        _folder.Dispose();
    }

    /// <summary>A copy of one small file into a folder of its own, on the given "disks".</summary>
    private QueuedJob Add(string name, string[] disks, bool blocks = false, bool held = false)
    {
        string source = _folder.File($"{name}-src/{name}.txt", name);
        string destination = _folder.Folder($"{name}-dest");
        _disks[destination] = disks;
        if (blocks)
        {
            // Put the file there already, so the job asks what to do, and waits on the gate.
            File.WriteAllText(Path.Combine(destination, $"{name}.txt"), "older");
            _gates[destination] = new Gate();
        }
        return _queue.Add(new JobSpec(CopyOperation.Copy, destination, [source]),
            new JobOptions { Conflicts = ConflictPolicy.Ask }, held);
    }

    private Gate GateOf(QueuedJob job) => _gates[job.Spec.Destination];

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Gave up waiting.");
            Thread.Sleep(10);
        }
    }

    private static void WaitDone(QueuedJob job) => WaitUntil(() => job.State == QueueState.Done);

    [Fact]
    public void A_job_on_a_share_starts_at_the_network_speed_limit()
    {
        var settings = new AppSettings { SpeedLimit = 32 * Units.MB, NetworkSpeedLimit = 8 * Units.MB };
        QueuedJob Queue(string name, string[] disks)
        {
            string source = _folder.File($"{name}-src/{name}.txt", name);
            string destination = _folder.Folder($"{name}-dest");
            _disks[destination] = disks;
            return _queue.Add(new JobSpec(CopyOperation.Copy, destination, [source]),
                resources => settings.ToJobOptions(network: Disks.UsesNetwork(resources)), held: true);
        }

        var share = Queue("share", ["server:DESKTOP", "disk:0"]);
        var local = Queue("local", ["disk:0"]);

        Assert.Equal(8 * Units.MB, share.Options.SpeedLimit);
        Assert.Equal(8 * Units.MB, share.Control.SpeedLimit); // what the card's slider shows
        Assert.Equal(32 * Units.MB, local.Options.SpeedLimit);
    }

    [Fact]
    public void Jobs_on_one_disk_run_one_after_the_other()
    {
        var first = Add("first", ["disk:0"], blocks: true);
        var second = Add("second", ["disk:0"]);

        GateOf(first).WaitAsked();
        Assert.Equal(QueueState.Running, first.State);
        Assert.Equal(QueueState.Waiting, second.State);
        Assert.Same(first, second.WaitingFor);

        GateOf(first).Open();
        WaitDone(first);
        WaitDone(second);
        Assert.True(second.Result!.Succeeded);
        Assert.Equal("first", File.ReadAllText(_folder.At("first-dest", "first.txt"))); // overwritten
    }

    [Fact]
    public void Jobs_on_different_disks_run_side_by_side()
    {
        var first = Add("first", ["disk:0"], blocks: true);
        var second = Add("second", ["disk:1"]);

        GateOf(first).WaitAsked();
        WaitDone(second);
        Assert.Equal(QueueState.Running, first.State);
        Assert.True(File.Exists(_folder.At("second-dest", "second.txt")));
    }

    [Fact]
    public void Start_now_runs_a_waiting_job_beside_the_one_on_its_disk()
    {
        var first = Add("first", ["disk:0"], blocks: true);
        var second = Add("second", ["disk:0"]);
        GateOf(first).WaitAsked();

        _queue.StartNow(second);
        WaitDone(second);
        Assert.Equal(QueueState.Running, first.State);
    }

    [Fact]
    public void Pausing_a_waiting_job_holds_it_and_lets_the_next_go()
    {
        var first = Add("first", ["disk:0"], blocks: true);
        var second = Add("second", ["disk:0", "disk:1"]);
        var third = Add("third", ["disk:1"]);
        GateOf(first).WaitAsked();
        Assert.Same(second, third.WaitingFor);

        _queue.Pause(second);
        Assert.Equal(QueueState.Held, second.State);
        WaitDone(third);

        _queue.Resume(second);
        Assert.Equal(QueueState.Waiting, second.State);
        GateOf(first).Open();
        WaitDone(second);
    }

    [Fact]
    public void Pausing_a_running_job_pauses_its_copy_and_keeps_its_disk()
    {
        var first = Add("first", ["disk:0"], blocks: true);
        var second = Add("second", ["disk:0"]);
        GateOf(first).WaitAsked();

        _queue.Pause(first);
        Assert.True(first.Control.IsPaused);
        GateOf(first).Open();
        Thread.Sleep(200);
        Assert.Equal(QueueState.Running, first.State); // paused after the answer, not finished
        Assert.Equal(QueueState.Waiting, second.State);

        _queue.Resume(first);
        WaitDone(first);
        WaitDone(second);
    }

    [Fact]
    public void Cancel_stops_a_running_job_and_removes_a_waiting_one()
    {
        var first = Add("first", ["disk:0"], blocks: true);
        var second = Add("second", ["disk:0"]);
        GateOf(first).WaitAsked();

        _queue.Cancel(second);
        Assert.DoesNotContain(second, _queue.Jobs);

        _queue.Cancel(first);
        GateOf(first).Open(); // it answers Overwrite, but the job is cancelled by then
        WaitDone(first);
        Assert.True(first.Result!.Cancelled);
        Assert.Contains(first, _queue.Jobs); // stays listed until cleared
        _queue.ClearFinished();
        Assert.Empty(_queue.Jobs);
    }

    [Fact]
    public void Failures_are_retried_through_the_queue()
    {
        string source = _folder.File("held-src/held.txt", "held");
        string destination = _folder.Folder("held-dest");
        _disks[destination] = ["disk:0"];
        QueuedJob retried;
        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            retried = _queue.Add(new JobSpec(CopyOperation.Copy, destination, [source]), new JobOptions());
            WaitDone(retried);
            Assert.Single(retried.Result!.Failures);
            Assert.True(retried.CanRetry);
        }

        Assert.True(_queue.RetryFailed(retried));
        WaitDone(retried);
        Assert.True(retried.Result!.Succeeded, string.Join("; ", retried.Result.Failures.Select(f => f.Message)));
        Assert.False(retried.CanRetry);
        Assert.Equal("held", File.ReadAllText(Path.Combine(destination, "held.txt")));
    }

    [Fact]
    public void Changed_is_raised_as_jobs_start_and_finish()
    {
        int changes = 0;
        _queue.Changed += () => Interlocked.Increment(ref changes);
        var job = Add("one", ["disk:0"]);
        WaitDone(job);
        WaitUntil(() => Volatile.Read(ref changes) >= 2); // added (and started), finished
        Assert.NotNull(job.Progress);
        Assert.Equal(JobPhase.Done, job.Progress!.Phase);
    }

    /// <summary>Prompts that block a conflict until <see cref="Open"/>, then overwrite.</summary>
    private sealed class Gate : IJobPrompts
    {
        private readonly ManualResetEventSlim _asked = new();
        private readonly ManualResetEventSlim _open = new();

        public void WaitAsked()
        {
            if (!_asked.Wait(Patience)) throw new TimeoutException("The job never asked.");
        }

        public void Open() => _open.Set();

        public ConflictAnswer ResolveConflict(ConflictInfo conflict)
        {
            _asked.Set();
            _open.Wait();
            return new ConflictAnswer(ConflictResolution.Overwrite);
        }

        public DiskFullAnswer DiskFull(string destination, string message) => DiskFullAnswer.StopJob;
    }
}
