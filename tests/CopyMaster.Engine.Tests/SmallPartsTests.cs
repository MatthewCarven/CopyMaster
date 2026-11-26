using System.Diagnostics;
using System.Globalization;
using System.Text;
using CopyMaster.Engine.Control;
using CopyMaster.Engine.Jobs;

namespace CopyMaster.Engine.Tests;

public class RateLimiterTests
{
    private double _now = 100;

    private RateLimiter Limiter(long bytesPerSecond) =>
        new(() => _now) { BytesPerSecond = bytesPerSecond };

    [Fact]
    public void Unlimited_never_waits() =>
        Assert.Equal(TimeSpan.Zero, new RateLimiter(() => _now).Reserve(Units.GB));

    [Fact]
    public void Holds_the_rate()
    {
        var limiter = Limiter(Units.MB);
        Assert.Equal(1.0, limiter.Reserve(Units.MB).TotalSeconds, 6);
        _now = 101;
        Assert.Equal(1.0, limiter.Reserve(Units.MB).TotalSeconds, 6);
    }

    [Fact]
    public void Allows_half_a_second_of_burst_after_idling()
    {
        var limiter = Limiter(Units.MB);
        _now = 110;
        Assert.Equal(TimeSpan.Zero, limiter.Reserve(Units.MB / 2));
        Assert.Equal(0.5, limiter.Reserve(Units.MB / 2).TotalSeconds, 6);
    }

    [Fact]
    public void A_new_limit_drops_the_old_debt()
    {
        var limiter = Limiter(Units.MB);
        Assert.Equal(10.0, limiter.Reserve(10 * Units.MB).TotalSeconds, 6);
        limiter.BytesPerSecond = 100 * Units.MB;
        Assert.Equal(0.01, limiter.Reserve(Units.MB).TotalSeconds, 6);
    }

    [Fact]
    public void Rejects_a_limit_of_zero() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Limiter(Units.MB).BytesPerSecond = 0);

    [Fact]
    public void A_wait_can_be_cancelled()
    {
        var limiter = new RateLimiter { BytesPerSecond = 1 };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var clock = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() => limiter.Throttle(1000, cancel.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Raising_the_limit_mid_wait_takes_effect_at_once()
    {
        var limiter = new RateLimiter { BytesPerSecond = Units.MB };
        var clock = Stopwatch.StartNew();
        var waiter = Task.Run(() => limiter.Throttle(10 * Units.MB, CancellationToken.None));
        await Task.Delay(100);
        limiter.BytesPerSecond = Units.GB;
        await waiter.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Win32_codes_come_out_of_hresults()
    {
        Assert.Equal(112, Native.Win32.CodeFromHResult(unchecked((int)0x80070070)));
        Assert.Null(Native.Win32.CodeFromHResult(unchecked((int)0x80004005)));
        Assert.True(Native.Win32.IsDiskFull(Native.Win32.HResultFromWin32(Native.Win32.ERROR_DISK_FULL)));
        Assert.DoesNotContain("0x", Native.Win32.Message(Native.Win32.HResultFromWin32(Native.Win32.ERROR_ACCESS_DENIED)));
    }
}

public class UniqueNamesTests
{
    [Fact]
    public void Numbers_files_before_the_extension_and_skips_taken_names()
    {
        using var f = new TestFolder();
        string a = f.File("a.txt", "");
        Assert.Equal(f.At("a (2).txt"), UniqueNames.Next(a, isFolder: false));
        f.File("a (2).txt", "");
        Assert.Equal(f.At("a (3).txt"), UniqueNames.Next(a, isFolder: false));
    }

    [Fact]
    public void Numbers_folders_and_dot_files_at_the_end()
    {
        using var f = new TestFolder();
        Assert.Equal(f.At("my.folder (2)"), UniqueNames.Next(f.Folder("my.folder"), isFolder: true));
        Assert.Equal(f.At(".gitignore (2)"), UniqueNames.Next(f.File(".gitignore", ""), isFolder: false));
    }
}

public class JobFileTests
{
    [Fact]
    public void Round_trips_with_equals_signs_in_paths()
    {
        var spec = new JobSpec(CopyOperation.Move, @"C:\Users\Admin\Desktop\-=Programming=-",
            [@"C:\a=b\one.txt", @"\\server\share\two words"]) { ClipboardSequence = 4242 };

        var back = JobFile.Parse(JobFile.Format(spec));

        Assert.Equal(spec.Operation, back.Operation);
        Assert.Equal(spec.Destination, back.Destination);
        Assert.Equal(spec.Sources, back.Sources);
        Assert.Equal(4242u, back.ClipboardSequence);
    }

    [Fact]
    public void Reads_a_file_with_a_byte_order_mark_and_crlf_and_ignores_unknown_keys()
    {
        using var f = new TestFolder();
        string path = f.At("job.cmjob");
        File.WriteAllText(path, "CopyMaster job 1\r\nop=copy\r\nfuture=thing\r\ndest=C:\\x\r\nsrc=C:\\y\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var spec = JobFile.Read(path);

        Assert.Equal(CopyOperation.Copy, spec.Operation);
        Assert.Equal(@"C:\x", spec.Destination);
        Assert.Equal([@"C:\y"], spec.Sources);
        Assert.Null(spec.ClipboardSequence);
    }

    [Theory]
    [InlineData("op=copy\ndest=C:\\x\nsrc=C:\\y\n")]                 // no header
    [InlineData("CopyMaster job 1\ndest=C:\\x\nsrc=C:\\y\n")]        // no op
    [InlineData("CopyMaster job 1\nop=copy\nsrc=C:\\y\n")]           // no dest
    [InlineData("CopyMaster job 1\nop=copy\ndest=C:\\x\n")]          // no sources
    [InlineData("CopyMaster job 1\nop=shred\ndest=C:\\x\nsrc=y\n")]  // unknown op
    public void Rejects_incomplete_files(string text) =>
        Assert.Throws<FormatException>(() => JobFile.Parse(text));
}

public class UnitsTests
{
    [Theory]
    [InlineData(0L, "0 bytes")]
    [InlineData(1L, "1 byte")]
    [InlineData(1023L, "1023 bytes")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(11010048L, "10.5 MB")]
    [InlineData(132499741081L, "123 GB")]
    public void Formats_like_explorer(long bytes, string expected)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, Units.Bytes(bytes));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
}
