namespace CopyMaster.Engine.Control;

/// <summary>
/// The speed slider's stops: 1 to 128 MB/s (1 MB = 1,048,576 bytes, as Explorer counts),
/// then Unlimited. Positions 0 to 13 are the limits; position 14 is Unlimited.
/// </summary>
public static class SpeedStops
{
    public static IReadOnlyList<int> MegabytesPerSecond { get; } = [1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128];

    /// <summary>Positions on the slider, Unlimited included.</summary>
    public static int Count => MegabytesPerSecond.Count + 1;

    public static int Unlimited => MegabytesPerSecond.Count;

    /// <summary>The limit at a slider position, in bytes per second, or null for Unlimited.</summary>
    public static long? BytesPerSecond(int position) =>
        position >= Unlimited ? null : MegabytesPerSecond[Math.Max(position, 0)] * Units.MB;

    /// <summary>
    /// The position for a limit: its stop, or the nearest one (by ratio) for a limit set some
    /// other way, such as cmcopy's <c>--limit 5</c>. Null is Unlimited.
    /// </summary>
    public static int PositionOf(long? bytesPerSecond)
    {
        if (bytesPerSecond is not long limit) return Unlimited;
        double megabytes = limit / (double)Units.MB;
        int best = 0;
        for (int i = 1; i < MegabytesPerSecond.Count; i++)
        {
            if (Math.Abs(Math.Log(megabytes / MegabytesPerSecond[i])) < Math.Abs(Math.Log(megabytes / MegabytesPerSecond[best])))
            {
                best = i;
            }
        }
        return best;
    }

    /// <summary>"16 MB/s", or "Unlimited".</summary>
    public static string Describe(long? bytesPerSecond) =>
        bytesPerSecond is long limit ? Units.Speed(limit) : "Unlimited";
}
