using System.Text.Json;
using System.Text.Json.Serialization;
using CopyMaster.Engine.Jobs;

namespace CopyMaster.Engine;

/// <summary>
/// CopyMaster.exe's settings: what new jobs start with. Kept as JSON in
/// <c>%LOCALAPPDATA%\CopyMaster\settings.json</c>; a missing or unreadable file means the defaults.
/// </summary>
public sealed record AppSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>What <see cref="NetworkSpeedLimit"/> starts as, in MB/s: a home connection's share of Wi-Fi.</summary>
    public const int DefaultNetworkMBps = 8;

    /// <summary>The speed limit for new jobs in bytes per second, or null for unlimited.</summary>
    public long? SpeedLimit { get; init; }

    /// <summary>
    /// The speed limit for new jobs that read from or write to a network share, in bytes per
    /// second, or null for none of its own. Such a job gets the lower of this and <see cref="SpeedLimit"/>.
    /// </summary>
    public long? NetworkSpeedLimit { get; init; } = DefaultNetworkMBps * Units.MB;

    /// <summary>Compare each copy with its source, read back from the disk. On by default.</summary>
    public bool Verify { get; init; } = true;

    public ConflictPolicy Conflicts { get; init; } = ConflictPolicy.Ask;

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CopyMaster", "settings.json");

    /// <param name="network">The job uses a network share (<see cref="Scheduling.Disks.UsesNetwork"/>).</param>
    public JobOptions ToJobOptions(bool network = false) => new()
    {
        Verify = Verify,
        SpeedLimit = network ? Lower(SpeedLimit, NetworkSpeedLimit) : SpeedLimit,
        Conflicts = Conflicts,
    };

    private static long? Lower(long? a, long? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);

    private static long? FromMBps(double? megabytes) => megabytes is double mb && mb > 0 ? (long)(mb * Units.MB) : null;

    private static double? ToMBps(long? limit) => limit is long bytes ? bytes / (double)Units.MB : null;

    public static AppSettings Load(string path)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json);
            if (stored is null) return new AppSettings();
            return new AppSettings
            {
                SpeedLimit = FromMBps(stored.SpeedLimitMBps),
                NetworkSpeedLimit = FromMBps(stored.NetworkSpeedLimitMBps),
                Verify = stored.Verify ?? true,
                Conflicts = stored.Conflicts is { } policy && Enum.IsDefined(policy) ? policy : ConflictPolicy.Ask,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings(); // missing, or broken by hand: the defaults
        }
    }

    /// <summary>Writes the settings, through a temporary file so a crash never leaves half of one.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stored = new Stored
        {
            SpeedLimitMBps = ToMBps(SpeedLimit),
            NetworkSpeedLimitMBps = ToMBps(NetworkSpeedLimit),
            Verify = Verify,
            Conflicts = Conflicts,
        };
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(stored, Json));
        File.Move(temp, path, overwrite: true);
    }

    // MB/s in the file, so it reads (and edits) the way the slider does. null is Unlimited.
    private sealed class Stored
    {
        public double? SpeedLimitMBps { get; set; }

        // Missing (a file from before this setting) keeps the default; null is a choice.
        public double? NetworkSpeedLimitMBps { get; set; } = DefaultNetworkMBps;

        public bool? Verify { get; set; }
        public ConflictPolicy? Conflicts { get; set; }
    }
}
