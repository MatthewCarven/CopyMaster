using System.Globalization;

namespace CopyMaster.Engine;

/// <summary>Sizes as Explorer shows them: 1 KB = 1,024 bytes, 1 MB = 1,048,576.</summary>
public static class Units
{
    public const long KB = 1L << 10;
    public const long MB = 1L << 20;
    public const long GB = 1L << 30;
    public const long TB = 1L << 40;

    public static string Bytes(long bytes) => bytes switch
    {
        < KB => bytes == 1 ? "1 byte" : $"{bytes} bytes",
        < MB => Scaled(bytes, KB, "KB"),
        < GB => Scaled(bytes, MB, "MB"),
        < TB => Scaled(bytes, GB, "GB"),
        _ => Scaled(bytes, TB, "TB"),
    };

    public static string Speed(double bytesPerSecond) => Bytes((long)bytesPerSecond) + "/s";

    /// <summary>A length of time to two units at most: "45 s", "3 min 20 s", "2 h 5 min".</summary>
    public static string Duration(TimeSpan time)
    {
        long seconds = (long)Math.Round(Math.Max(time.TotalSeconds, 0));
        if (seconds < 60) return $"{seconds} s";
        long minutes = seconds / 60;
        if (minutes < 60) return seconds % 60 == 0 ? $"{minutes} min" : $"{minutes} min {seconds % 60} s";
        long hours = minutes / 60;
        if (hours < 24) return minutes % 60 == 0 ? $"{hours} h" : $"{hours} h {minutes % 60} min";
        return hours % 24 == 0 ? $"{hours / 24} d" : $"{hours / 24} d {hours % 24} h";
    }

    /// <summary>
    /// How long the rest will take at this speed, rounded the way a person would say it
    /// ("about 3 min"), or null if there's no telling yet.
    /// </summary>
    public static string? TimeLeft(long bytesLeft, double bytesPerSecond)
    {
        if (bytesPerSecond < 1 || bytesLeft < 0) return null;
        double seconds = bytesLeft / bytesPerSecond;
        if (seconds > TimeSpan.FromDays(100).TotalSeconds) return null;
        if (seconds < 5) return "a few seconds";

        // Coarser as it grows, so the number doesn't flicker: 5 s steps under a minute,
        // whole minutes under an hour, then 5-minute steps.
        double step = seconds < 60 ? 5 : seconds < 3600 ? 60 : 300;
        return "about " + Duration(TimeSpan.FromSeconds(Math.Round(seconds / step) * step));
    }

    // Three significant figures, as Explorer does: 1.23 GB, 12.3 GB, 123 GB.
    private static string Scaled(long bytes, long unit, string name)
    {
        double value = bytes / (double)unit;
        string format = value < 10 ? "0.##" : value < 100 ? "0.#" : "0";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + name;
    }
}
