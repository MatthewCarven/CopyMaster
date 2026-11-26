using CopyMaster.Engine.Scheduling;

namespace CopyMaster.Engine.Tests;

/// <summary>The scheduling rule on its own: which jobs start, and who waits for whom.</summary>
public class SchedulerTests
{
    private readonly Scheduler<string> _queue = new();

    private QueueEntry<string> Add(string name, params string[] disks) => _queue.Add(name, disks);

    private List<string> Start() => _queue.Start().Select(e => e.Item).ToList();

    [Fact]
    public void Jobs_on_different_disks_start_together()
    {
        Add("a", "disk:0");
        Add("b", "disk:1");
        Add("c", "server:DESKTOP");
        Assert.Equal(["a", "b", "c"], Start());
    }

    [Fact]
    public void Jobs_on_one_disk_take_turns_in_order()
    {
        var a = Add("a", "disk:0");
        var b = Add("b", "disk:0");
        var c = Add("c", "disk:0", "disk:1");

        Assert.Equal(["a"], Start());
        Assert.Same(a, b.WaitingFor);
        Assert.Same(a, c.WaitingFor);
        Assert.Empty(Start()); // nothing changed

        _queue.Finish(a);
        Assert.Equal(["b"], Start());
        _queue.Finish(b);
        Assert.Equal(["c"], Start());
        Assert.Null(c.WaitingFor);
    }

    [Fact]
    public void A_job_is_only_overtaken_by_jobs_that_share_no_disk_with_it()
    {
        var running = Add("running", "disk:0");
        Assert.Equal(["running"], Start());

        // "wide" needs disks 0 and 1. "narrow" needs only disk 1, which is free, but it
        // mustn't jump ahead, or a stream of narrow jobs could keep wide waiting for ever.
        var wide = Add("wide", "disk:0", "disk:1");
        var narrow = Add("narrow", "disk:1");
        Add("elsewhere", "disk:2");

        Assert.Equal(["elsewhere"], Start());
        Assert.Same(running, wide.WaitingFor);
        Assert.Same(wide, narrow.WaitingFor);

        _queue.Finish(running);
        Assert.Equal(["wide"], Start());
        Assert.Same(wide, narrow.WaitingFor);
    }

    [Fact]
    public void A_held_job_neither_starts_nor_holds_anyone_up()
    {
        var held = _queue.Add("held", ["disk:0"], held: true);
        Add("next", "disk:0");
        Assert.Equal(["next"], Start());
        Assert.Equal(QueueState.Held, held.State);
        Assert.Null(held.WaitingFor);

        _queue.Release(held);
        Assert.Empty(Start()); // its disk is busy now
        Assert.Equal("next", held.WaitingFor?.Item);
    }

    [Fact]
    public void Holding_a_waiting_job_lets_the_ones_behind_it_go()
    {
        Add("running", "disk:0", "disk:1");
        Start();
        var wide = Add("wide", "disk:1", "disk:2");
        Add("behind", "disk:2");
        Assert.Empty(Start());

        _queue.Hold(wide);
        Assert.Equal(["behind"], Start());
    }

    [Fact]
    public void Start_now_runs_a_job_beside_the_one_on_its_disk()
    {
        var a = Add("a", "disk:0");
        var b = Add("b", "disk:0");
        var c = Add("c", "disk:0");
        Start();

        _queue.StartNow(c);
        Assert.Equal(["c"], Start());
        Assert.Equal(QueueState.Running, c.State);
        Assert.Same(a, b.WaitingFor);

        // b still waits for both to finish: c's disk is busy too.
        _queue.Finish(a);
        Assert.Empty(Start());
        Assert.Same(c, b.WaitingFor);
        _queue.Finish(c);
        Assert.Equal(["b"], Start());
    }

    [Fact]
    public void Start_now_works_on_a_held_job()
    {
        Add("a", "disk:0");
        Start();
        var held = _queue.Add("held", ["disk:0"], held: true);
        _queue.StartNow(held);
        Assert.Equal(["held"], Start());
    }

    [Fact]
    public void Moving_a_job_up_changes_which_goes_next()
    {
        var a = Add("a", "disk:0");
        Add("b", "disk:0");
        var c = Add("c", "disk:0");
        Start();

        Assert.True(_queue.Move(c, -1));
        Assert.Equal(["a", "c", "b"], _queue.Entries.Select(e => e.Item));
        Assert.True(_queue.Move(c, -5)); // clamps to the top
        Assert.Equal(["c", "a", "b"], _queue.Entries.Select(e => e.Item));
        Assert.False(_queue.Move(c, -1));

        _queue.Finish(a);
        Assert.Equal(["c"], Start());
    }

    [Fact]
    public void Removing_a_waiting_job_lets_the_next_one_start()
    {
        var a = Add("a", "disk:0");
        var b = Add("b", "disk:0", "disk:1");
        var c = Add("c", "disk:1");
        Start();
        Assert.Same(b, c.WaitingFor);

        Assert.False(_queue.Remove(a)); // running: cancel it instead
        Assert.True(_queue.Remove(b));
        Assert.Equal(["c"], Start());
    }

    [Fact]
    public void A_requeued_job_waits_for_its_disk_again()
    {
        var a = Add("a", "disk:0");
        Start();
        _queue.Finish(a);
        var b = Add("b", "disk:0");
        Assert.Equal(["b"], Start());

        _queue.Requeue(a); // retry its failures
        Assert.Equal(QueueState.Waiting, a.State);
        Assert.Empty(Start());
        Assert.Same(b, a.WaitingFor);
        _queue.Finish(b);
        Assert.Equal(["a"], Start());
    }

    [Fact]
    public void A_job_that_needs_no_disks_starts_at_once()
    {
        Add("a", "disk:0");
        Add("b", "disk:0");
        Add("rename-only");
        Assert.Equal(["a", "rename-only"], Start());
    }

    [Fact]
    public void Disk_keys_ignore_case()
    {
        Add("a", "server:desktop");
        var b = Add("b", "SERVER:DESKTOP");
        Start();
        Assert.Equal(QueueState.Waiting, b.State);
    }
}
