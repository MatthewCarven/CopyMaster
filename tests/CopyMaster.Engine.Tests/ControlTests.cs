using System.Diagnostics;
using CopyMaster.Engine.Jobs;

namespace CopyMaster.Engine.Tests;

[Collection("Timing")]
public class ControlTests
{
    [Fact]
    public void The_speed_limit_holds()
    {
        using var f = new TestFolder();
        string source = f.RandomFile(@"src\big.bin", 3 * Units.MB);
        var clock = Stopwatch.StartNew();

        var result = Run.Job(CopyOperation.Copy, f.Folder("dst"), [source],
            new JobOptions { SpeedLimit = 2 * Units.MB });

        Run.AssertSucceeded(result);
        // 3 MB at 2 MB/s is 1.5 s.
        Assert.InRange(clock.Elapsed.TotalSeconds, 1.2, 6);
    }

    [Fact]
    public async Task Pause_stops_mid_file_and_resume_finishes()
    {
        using var f = new TestFolder();
        string source = f.RandomFile(@"src\big.bin", 4 * Units.MB);
        using var job = new CopyJob(new JobSpec(CopyOperation.Copy, f.Folder("dst"), [source]),
            new JobOptions { SpeedLimit = 4 * Units.MB });
        long bytesDone = 0;
        using var started = new ManualResetEventSlim();
        job.ProgressChanged += p =>
        {
            Interlocked.Exchange(ref bytesDone, p.BytesDone);
            if (p.BytesDone > 0) started.Set();
        };

        var running = Task.Run(job.Run);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        job.Control.Pause();
        Thread.Sleep(300); // let the current chunk land
        long atPause = Interlocked.Read(ref bytesDone);
        Thread.Sleep(700);
        Assert.Equal(atPause, Interlocked.Read(ref bytesDone));
        Assert.True(atPause < 4 * Units.MB);

        job.Control.Resume();
        Run.AssertSucceeded(await running.WaitAsync(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void Cancel_mid_file_leaves_nothing_behind()
    {
        using var f = new TestFolder();
        string source = f.RandomFile(@"src\big.bin", 8 * Units.MB);
        string destination = f.Folder("dst");
        using var job = new CopyJob(new JobSpec(CopyOperation.Copy, destination, [source]),
            new JobOptions { SpeedLimit = 2 * Units.MB });
        job.ProgressChanged += p =>
        {
            if (p.BytesDone > 0) job.Control.Cancel();
        };

        var result = job.Run();

        Assert.True(result.Cancelled);
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task Cancel_wakes_a_paused_job()
    {
        using var f = new TestFolder();
        string source = f.RandomFile(@"src\big.bin", 4 * Units.MB);
        using var job = new CopyJob(new JobSpec(CopyOperation.Copy, f.Folder("dst"), [source]),
            new JobOptions { SpeedLimit = 2 * Units.MB });
        using var started = new ManualResetEventSlim();
        job.ProgressChanged += p =>
        {
            if (p.BytesDone > 0) started.Set();
        };

        var running = Task.Run(job.Run);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        job.Control.Pause();
        Thread.Sleep(200);
        job.Control.Cancel();

        Assert.True((await running.WaitAsync(TimeSpan.FromSeconds(5))).Cancelled);
    }
}
