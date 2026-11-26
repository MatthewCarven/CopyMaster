using System.Collections.Concurrent;
using System.Diagnostics;
using CopyMaster.Engine.Instance;
using CopyMaster.Engine.Jobs;

namespace CopyMaster.Engine.Tests;

/// <summary>A second launch handing its job to the running instance over the named pipe.</summary>
public sealed class HandOverTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly string _name = "CopyMaster.Test." + Guid.NewGuid().ToString("N");
    private readonly BlockingCollection<JobSpec?> _received = new();
    private HandOverServer? _server;

    private void StartServer(Action<JobSpec?>? handle = null)
    {
        _server = new HandOverServer(_name, handle ?? (spec => _received.Add(spec)));
        _server.Start();
    }

    public void Dispose()
    {
        _server?.Dispose();
        _received.Dispose();
    }

    private static JobSpec Spec(params string[] sources) =>
        new(CopyOperation.Move, @"D:\Backup\Ünïcødé 日本", sources) { ClipboardSequence = 4242 };

    [Fact]
    public void A_job_sent_down_the_pipe_arrives_whole()
    {
        StartServer();
        var spec = Spec(@"C:\Temp\a.txt", @"C:\Temp\Some folder", @"C:\Temp\name=with=equals.txt");

        var outcome = HandOverClient.Send(_name, spec, Wait);

        Assert.Equal(HandOverStatus.Accepted, outcome.Status);
        Assert.True(_received.TryTake(out var got, Wait));
        Assert.NotNull(got);
        Assert.Equal(spec.Operation, got.Operation);
        Assert.Equal(spec.Destination, got.Destination);
        Assert.Equal(spec.Sources, got.Sources);
        Assert.Equal(4242u, got.ClipboardSequence);
    }

    [Fact]
    public void A_launch_with_no_job_asks_the_instance_to_show_itself()
    {
        StartServer();
        Assert.Equal(HandOverStatus.Accepted, HandOverClient.Send(_name, null, Wait).Status);
        Assert.True(_received.TryTake(out var got, Wait));
        Assert.Null(got);
    }

    [Fact]
    public void With_no_instance_the_launch_finds_out_quickly()
    {
        var clock = Stopwatch.StartNew();
        var outcome = HandOverClient.Send(_name, Spec(@"C:\a"), TimeSpan.FromMilliseconds(300));
        Assert.Equal(HandOverStatus.NoInstance, outcome.Status);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Took {clock.Elapsed}.");
    }

    [Fact]
    public async Task A_launch_waits_for_an_instance_that_is_still_starting()
    {
        var sending = HandOverClient.SendAsync(_name, Spec(@"C:\a"), Wait);
        await Task.Delay(300);
        Assert.False(sending.IsCompleted);
        StartServer();

        var outcome = await sending.WaitAsync(Wait * 2);
        Assert.Equal(HandOverStatus.Accepted, outcome.Status);
        Assert.True(_received.TryTake(out _, Wait));
    }

    [Fact]
    public void A_job_the_instance_refuses_comes_back_with_the_reason()
    {
        StartServer(_ => throw new InvalidOperationException("CopyMaster is quitting."));
        var outcome = HandOverClient.Send(_name, Spec(@"C:\a"), Wait);
        Assert.Equal(HandOverStatus.Rejected, outcome.Status);
        Assert.Equal("CopyMaster is quitting.", outcome.Message);
    }

    [Fact]
    public async Task Many_launches_at_once_all_get_through()
    {
        StartServer();
        var sends = Enumerable.Range(0, 12)
            .Select(i => Task.Run(() => HandOverClient.SendAsync(_name, Spec($@"C:\Temp\{i}.txt"), Wait)))
            .ToList();
        var outcomes = await Task.WhenAll(sends).WaitAsync(Wait * 3);

        Assert.All(outcomes, o => Assert.Equal(HandOverStatus.Accepted, o.Status));
        var sources = new List<string>();
        for (int i = 0; i < 12; i++)
        {
            Assert.True(_received.TryTake(out var got, Wait));
            sources.Add(got!.Sources.Single());
        }
        Assert.Equal(Enumerable.Range(0, 12).Select(i => $@"C:\Temp\{i}.txt").Order(), sources.Order());
    }

    [Fact]
    public void A_big_job_gets_through()
    {
        StartServer();
        var sources = Enumerable.Range(0, 20_000).Select(i => $@"C:\Some\Long\Folder\Name\file number {i}.dat").ToArray();
        Assert.Equal(HandOverStatus.Accepted, HandOverClient.Send(_name, Spec(sources), Wait).Status);
        Assert.True(_received.TryTake(out var got, Wait));
        Assert.Equal(sources, got!.Sources);
    }

    [Fact]
    public void A_stopped_server_takes_no_more_jobs_and_can_be_stopped_twice()
    {
        StartServer();
        Assert.Equal(HandOverStatus.Accepted, HandOverClient.Send(_name, Spec(@"C:\a"), Wait).Status);
        _server!.Dispose(); // quitting starts
        _server.Dispose();  // and the using at the end of Main

        var outcome = HandOverClient.Send(_name, Spec(@"C:\b"), TimeSpan.FromMilliseconds(300));
        Assert.Equal(HandOverStatus.NoInstance, outcome.Status);
    }

    [Fact]
    public void Only_one_process_is_the_instance()
    {
        using (var first = SingleInstance.TryClaim(_name))
        {
            Assert.NotNull(first);
            Assert.Null(SingleInstance.TryClaim(_name));
        }
        using var again = SingleInstance.TryClaim(_name); // the first one quit
        Assert.NotNull(again);
    }
}
