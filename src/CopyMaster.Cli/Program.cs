using System.Globalization;
using CopyMaster.Engine;
using CopyMaster.Engine.Jobs;

namespace CopyMaster.Cli;

internal static class Program
{
    private const string Usage = """
        cmcopy: copy or move files with CopyMaster's engine, from a terminal.

          cmcopy copy|move --dest <folder> [options] <source>...
          cmcopy --job <job file> [options]

        Options:
          --verify             compare each copy with its source, read back from the disk
          --limit <MB/s>       speed limit, 1 MB = 1,048,576 bytes (default: unlimited)
          --conflict <what>    when a file is already there: skip (default), overwrite,
                               rename (keep both) or newer (overwrite only with a newer file)
          --quiet              no progress line

        Ctrl+C cancels; a half-copied file is deleted.
        Exit codes: 0 all done, 1 some items failed, 2 bad arguments, 3 cancelled or stopped.
        """;

    private static int Main(string[] args)
    {
        Arguments arguments;
        try
        {
            arguments = Arguments.Parse(args);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (arguments.Help)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        using var job = new CopyJob(arguments.Spec!, arguments.Options);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // let the job stop cleanly
            job.Control.Cancel();
        };

        var line = new ProgressLine(enabled: !arguments.Quiet && !Console.IsOutputRedirected);
        job.ProgressChanged += line.Update;
        var result = job.Run();
        line.Clear();

        PrintSummary(result);
        if (result.Cancelled || result.StoppedReason is not null) return 3;
        return result.Failures.Count > 0 ? 1 : 0;
    }

    private static void PrintSummary(JobResult result)
    {
        var parts = new List<string>();
        if (result.FilesCopied > 0) parts.Add($"copied {result.FilesCopied} file(s)");
        if (result.ItemsMoved > 0) parts.Add($"moved {result.ItemsMoved} item(s)");
        if (result.Identical > 0) parts.Add($"{result.Identical} already there");
        if (result.Skipped > 0) parts.Add($"skipped {result.Skipped}");
        if (parts.Count == 0) parts.Add("nothing copied");
        string text = string.Join(", ", parts);
        Console.WriteLine($"{char.ToUpper(text[0], CultureInfo.CurrentCulture)}{text[1..]}; " +
                          $"{Units.Bytes(result.BytesCopied)} in {result.Elapsed.TotalSeconds:0.0} s.");

        foreach (string note in result.Notes) Console.WriteLine($"Note: {note}");

        if (result.Failures.Count > 0)
        {
            Console.WriteLine($"{result.Failures.Count} failed:");
            foreach (var failure in result.Failures) Console.WriteLine($"  {failure.Source}: {failure.Message}");
        }

        if (result.StoppedReason is not null) Console.WriteLine($"Stopped: {result.StoppedReason}");
        if (result.Cancelled) Console.WriteLine("Cancelled.");
    }
}

internal sealed class Arguments
{
    public bool Help { get; private set; }
    public bool Quiet { get; private set; }
    public JobSpec? Spec { get; private set; }
    public JobOptions Options { get; private set; } = new() { Conflicts = ConflictPolicy.Skip };

    public static Arguments Parse(string[] args)
    {
        var parsed = new Arguments();
        if (args.Length == 0 || args is ["-h" or "--help" or "/?", ..])
        {
            parsed.Help = true;
            return parsed;
        }

        CopyOperation? operation = null;
        string? destination = null;
        string? jobFile = null;
        var sources = new List<string>();
        bool verify = false;
        long? limit = null;
        var conflicts = ConflictPolicy.Skip;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{arg} needs a value.");

            switch (arg)
            {
                case "copy" when i == 0: operation = CopyOperation.Copy; break;
                case "move" when i == 0: operation = CopyOperation.Move; break;
                case "--dest": destination = Value(); break;
                case "--job": jobFile = Value(); break;
                case "--verify": verify = true; break;
                case "--quiet": parsed.Quiet = true; break;
                case "--limit":
                    string mb = Value();
                    if (!double.TryParse(mb, NumberStyles.Float, CultureInfo.InvariantCulture, out double megabytes) ||
                        megabytes <= 0)
                    {
                        throw new ArgumentException($"--limit wants a speed in MB/s above 0, not \"{mb}\".");
                    }
                    limit = (long)(megabytes * Units.MB);
                    break;
                case "--conflict":
                    string what = Value();
                    conflicts = what switch
                    {
                        "skip" => ConflictPolicy.Skip,
                        "overwrite" => ConflictPolicy.Overwrite,
                        "rename" => ConflictPolicy.Rename,
                        "newer" => ConflictPolicy.KeepNewer,
                        _ => throw new ArgumentException($"--conflict wants skip, overwrite, rename or newer, not \"{what}\"."),
                    };
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option {arg}.");
                    sources.Add(arg);
                    break;
            }
        }

        if (jobFile is not null)
        {
            if (operation is not null || destination is not null || sources.Count > 0)
            {
                throw new ArgumentException("--job takes everything from the job file; give only options with it.");
            }
            parsed.Spec = JobFile.Read(jobFile);
        }
        else
        {
            if (operation is null) throw new ArgumentException("Say copy or move first.");
            if (destination is null) throw new ArgumentException("--dest <folder> is needed.");
            if (sources.Count == 0) throw new ArgumentException("Nothing to copy: give one or more sources.");
            parsed.Spec = new JobSpec(operation.Value, destination, sources);
        }

        parsed.Options = new JobOptions { Verify = verify, SpeedLimit = limit, Conflicts = conflicts };
        return parsed;
    }
}

/// <summary>One line, rewritten in place at most four times a second.</summary>
internal sealed class ProgressLine(bool enabled)
{
    private long _lastDraw;
    private int _drawnLength;

    public void Update(JobProgress p)
    {
        if (!enabled) return;
        long now = Environment.TickCount64;
        if (p.Phase != JobPhase.Done && now - _lastDraw < 250) return;
        _lastDraw = now;

        string phase = p.Phase switch
        {
            JobPhase.Scanning => "Listing",
            JobPhase.Copying => "Copying",
            JobPhase.Verifying => "Verifying",
            JobPhase.Comparing => "Comparing",
            JobPhase.Moving => "Moving",
            JobPhase.Finishing => "Finishing",
            _ => "Done",
        };
        string text = $"{phase,-9} {p.ItemsDone}/{p.ItemsTotal}  {Units.Bytes(p.BytesDone)} of " +
                      $"{Units.Bytes(p.BytesTotal)}  {Units.Speed(p.BytesPerSecond)}  {Path.GetFileName(p.CurrentSource)}";

        int width = Math.Max(20, SafeWidth() - 1);
        if (text.Length > width) text = text[..width];
        Console.Write("\r" + text.PadRight(_drawnLength));
        _drawnLength = text.Length;
    }

    public void Clear()
    {
        if (!enabled || _drawnLength == 0) return;
        Console.Write("\r" + new string(' ', _drawnLength) + "\r");
        _drawnLength = 0;
    }

    private static int SafeWidth()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch (IOException)
        {
            return 80;
        }
    }
}
