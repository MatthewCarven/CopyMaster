using CopyMaster.Engine.Control;
using CopyMaster.Engine.Jobs;
using CopyMaster.Engine.Scheduling;

namespace CopyMaster.Engine.Tests;

public class DiskTests
{
    [Fact]
    public void Two_folders_on_one_volume_share_a_disk_key()
    {
        using var folder = new TestFolder();
        var keys = Disks.KeysFor(folder.Root);
        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.StartsWith("disk:", key));
        Assert.Equal(keys, Disks.KeysFor(Path.GetTempPath()));
        Assert.Equal(keys, Disks.KeysFor(folder.At("not", "there", "yet.txt")));
    }

    [Theory]
    [InlineData(@"\\desktop\share\folder\file.txt")]
    [InlineData(@"\\Desktop\other share")]
    [InlineData(@"\\?\UNC\desktop\share\folder")]
    public void A_network_path_is_keyed_by_its_server(string path) =>
        Assert.Equal(["server:DESKTOP"], Disks.KeysFor(path));

    [Fact]
    public void A_copy_needs_the_source_and_destination_disks()
    {
        using var folder = new TestFolder();
        string disk = Disks.KeysFor(folder.Root).First();
        var spec = new JobSpec(CopyOperation.Copy, folder.Root, [@"\\desktop\share\a.txt", folder.At("b.txt")]);
        var resources = Disks.ResourcesFor(spec);
        Assert.Contains("server:DESKTOP", resources);
        Assert.Contains(disk, resources);
    }

    [Fact]
    public void A_move_within_one_volume_needs_no_disk_time()
    {
        using var folder = new TestFolder();
        string source = folder.File("a/file.txt", "x");
        var move = new JobSpec(CopyOperation.Move, folder.Folder("b"), [source]);
        Assert.Empty(Disks.ResourcesFor(move));

        var copy = move with { Operation = CopyOperation.Copy };
        Assert.NotEmpty(Disks.ResourcesFor(copy));
    }

    [Fact]
    public void Only_a_job_that_touches_a_share_uses_the_network()
    {
        using var folder = new TestFolder();
        string local = folder.File("a.txt", "x");
        Assert.True(Disks.UsesNetwork(Disks.ResourcesFor(new JobSpec(CopyOperation.Copy, folder.Root, [@"\\desktop\share\a.txt"]))));
        Assert.True(Disks.UsesNetwork(Disks.ResourcesFor(new JobSpec(CopyOperation.Copy, @"\\desktop\share", [local]))));
        Assert.False(Disks.UsesNetwork(Disks.ResourcesFor(new JobSpec(CopyOperation.Copy, folder.Folder("b"), [local]))));
        Assert.False(Disks.UsesNetwork(new HashSet<string>()));
    }
}

public class SettingsTests
{
    [Fact]
    public void A_missing_file_gives_the_defaults()
    {
        using var folder = new TestFolder();
        var settings = AppSettings.Load(folder.At("settings.json"));
        Assert.Equal(new AppSettings(), settings);
        Assert.Null(settings.SpeedLimit);
        Assert.Equal(8 * Units.MB, settings.NetworkSpeedLimit);
        Assert.True(settings.Verify);
        Assert.Equal(ConflictPolicy.Ask, settings.Conflicts);
    }

    [Fact]
    public void Settings_survive_a_save_and_load_and_read_as_mb_per_second()
    {
        using var folder = new TestFolder();
        string path = folder.At("sub", "settings.json");
        var settings = new AppSettings
        {
            SpeedLimit = 8 * Units.MB, NetworkSpeedLimit = 3 * Units.MB, Verify = false, Conflicts = ConflictPolicy.KeepNewer,
        };
        settings.Save(path);

        Assert.Equal(settings, AppSettings.Load(path));
        string text = File.ReadAllText(path);
        Assert.Contains("\"SpeedLimitMBps\": 8", text);
        Assert.Contains("\"NetworkSpeedLimitMBps\": 3", text);
        Assert.Contains("\"KeepNewer\"", text);
    }

    [Fact]
    public void An_unlimited_network_speed_is_kept_as_a_choice()
    {
        using var folder = new TestFolder();
        string path = folder.At("settings.json");
        var settings = new AppSettings { NetworkSpeedLimit = null };
        settings.Save(path);

        Assert.Contains("\"NetworkSpeedLimitMBps\": null", File.ReadAllText(path));
        Assert.Null(AppSettings.Load(path).NetworkSpeedLimit);
    }

    [Fact]
    public void A_file_from_before_the_network_limit_gets_its_default()
    {
        using var folder = new TestFolder();
        string path = folder.File("settings.json", "{ \"SpeedLimitMBps\": 16, \"Verify\": true, \"Conflicts\": \"Ask\" }");
        var settings = AppSettings.Load(path);
        Assert.Equal(16 * Units.MB, settings.SpeedLimit);
        Assert.Equal(8 * Units.MB, settings.NetworkSpeedLimit);
    }

    [Theory]
    [InlineData(null, 8, 8)]    // Unlimited locally: the share's limit
    [InlineData(4, 8, 4)]       // the lower of the two
    [InlineData(16, null, 16)]  // no limit of the share's own
    [InlineData(null, null, null)]
    public void A_job_on_a_share_gets_the_lower_limit(int? local, int? network, int? expected)
    {
        var settings = new AppSettings { SpeedLimit = local * Units.MB, NetworkSpeedLimit = network * Units.MB };
        Assert.Equal(expected * Units.MB, settings.ToJobOptions(network: true).SpeedLimit);
        Assert.Equal(local * Units.MB, settings.ToJobOptions(network: false).SpeedLimit);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"Conflicts\": \"Sometimes\" }")]
    [InlineData("null")]
    public void A_broken_file_gives_the_defaults(string text)
    {
        using var folder = new TestFolder();
        string path = folder.File("settings.json", text);
        Assert.Equal(new AppSettings(), AppSettings.Load(path));
    }

    [Fact]
    public void Settings_become_job_options()
    {
        var options = new AppSettings { SpeedLimit = 4 * Units.MB, Verify = true, Conflicts = ConflictPolicy.Rename }.ToJobOptions();
        Assert.Equal(new JobOptions { SpeedLimit = 4 * Units.MB, Verify = true, Conflicts = ConflictPolicy.Rename }, options);
    }

    [Fact]
    public void The_slider_has_fourteen_limits_then_unlimited()
    {
        Assert.Equal(15, SpeedStops.Count);
        Assert.Equal(1 * Units.MB, SpeedStops.BytesPerSecond(0));
        Assert.Equal(16 * Units.MB, SpeedStops.BytesPerSecond(7));
        Assert.Equal(128 * Units.MB, SpeedStops.BytesPerSecond(13));
        Assert.Null(SpeedStops.BytesPerSecond(SpeedStops.Unlimited));
        for (int i = 0; i < SpeedStops.Count; i++) Assert.Equal(i, SpeedStops.PositionOf(SpeedStops.BytesPerSecond(i)));
    }

    [Theory]
    [InlineData(5.0, 6)]       // nearer 6 than 4 by ratio
    [InlineData(0.25, 1)]
    [InlineData(1000.0, 128)]
    [InlineData(10.0, 12)]
    [InlineData(9.0, 8)]
    public void A_limit_off_the_stops_lands_on_the_nearest(double megabytes, int stop) =>
        Assert.Equal(stop, SpeedStops.MegabytesPerSecond[SpeedStops.PositionOf((long)(megabytes * Units.MB))]);

    [Fact]
    public void Limits_are_described_as_explorer_would()
    {
        Assert.Equal("16 MB/s", SpeedStops.Describe(16 * Units.MB));
        Assert.Equal("Unlimited", SpeedStops.Describe(null));
    }

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(45, "45 s")]
    [InlineData(60, "1 min")]
    [InlineData(200, "3 min 20 s")]
    [InlineData(7500, "2 h 5 min")]
    [InlineData(90000, "1 d 1 h")]
    public void Durations_read_naturally(int seconds, string text) =>
        Assert.Equal(text, Units.Duration(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(100, 50, "a few seconds")]
    [InlineData(1000, 50, "about 20 s")]
    [InlineData(10_000, 50, "about 3 min")]          // 200 s rounds to whole minutes
    [InlineData(1_000_000, 100, "about 2 h 45 min")] // 10,000 s rounds to 5 minutes
    public void Time_left_is_rounded_so_it_doesnt_flicker(long bytesLeft, double speed, string text) =>
        Assert.Equal(text, Units.TimeLeft(bytesLeft, speed));

    [Fact]
    public void No_time_left_without_a_speed() => Assert.Null(Units.TimeLeft(1000, 0));
}

public class RoomTests
{
    private static JobResult Copy(TestFolder folder, long free, FakePrompts prompts, CopyOperation operation = CopyOperation.Copy)
    {
        string source = folder.RandomFile("src/big.bin", 64 * 1024);
        string destination = folder.Folder("dest");
        using var job = new CopyJob(new JobSpec(operation, destination, [source]), new JobOptions(), prompts)
        {
            FreeSpace = _ => free,
        };
        return job.Run();
    }

    [Fact]
    public void A_job_that_wont_fit_asks_first_and_can_stop()
    {
        using var folder = new TestFolder();
        long asked = 0;
        var prompts = new FakePrompts { OnNoRoom = (needed, _) => { asked = needed; return false; } };

        var result = Copy(folder, free: 1000, prompts);

        Assert.Equal(64 * 1024, asked);
        Assert.Contains("Not enough room", result.StoppedReason);
        Assert.False(File.Exists(folder.At("dest", "big.bin")));
        Assert.Equal(0, result.FilesCopied);
    }

    [Fact]
    public void Starting_anyway_copies()
    {
        using var folder = new TestFolder();
        var prompts = new FakePrompts { OnNoRoom = (_, _) => true };
        var result = Copy(folder, free: 1000, prompts);
        Assert.Equal(1, prompts.NoRoomCalls);
        Run.AssertSucceeded(result);
    }

    [Fact]
    public void No_question_when_it_fits()
    {
        using var folder = new TestFolder();
        var prompts = new FakePrompts();
        Run.AssertSucceeded(Copy(folder, free: 1L << 40, prompts));
        Assert.Equal(0, prompts.NoRoomCalls);
    }

    [Fact]
    public void A_move_within_a_volume_needs_no_room()
    {
        using var folder = new TestFolder();
        var prompts = new FakePrompts { OnNoRoom = (_, _) => false };
        Run.AssertSucceeded(Copy(folder, free: 0, prompts, CopyOperation.Move));
        Assert.Equal(0, prompts.NoRoomCalls);
    }
}
