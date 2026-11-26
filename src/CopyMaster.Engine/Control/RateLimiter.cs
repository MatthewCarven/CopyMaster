using System.Diagnostics;

namespace CopyMaster.Engine.Control;

/// <summary>
/// Holds a job to a speed limit in bytes per second, adjustable while the job runs.
/// Called after each chunk with the bytes just moved; it then waits long enough that the
/// average stays at the limit. After an idle spell (a pause, a slow disk) it allows a
/// burst of up to half a second's worth, then settles back to the limit.
/// </summary>
public sealed class RateLimiter
{
    internal const double MaxBurstSeconds = 0.5;
    private static readonly TimeSpan MaxWaitSlice = TimeSpan.FromMilliseconds(100);

    private readonly Lock _gate = new();
    private readonly Func<double> _now;
    private long? _bytesPerSecond;
    private double _next; // clock time (seconds) from which the next byte may go

    public RateLimiter() : this(() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency) { }

    internal RateLimiter(Func<double> now) => _now = now;

    /// <summary>The limit in bytes per second, or null for unlimited.</summary>
    public long? BytesPerSecond
    {
        get { lock (_gate) return _bytesPerSecond; }
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value), "The limit must be positive.");
            lock (_gate)
            {
                _bytesPerSecond = value;
                _next = _now(); // a new limit starts afresh: no debt carried over
            }
        }
    }

    /// <summary>Counts bytes just moved and returns how long to wait before moving more.</summary>
    internal TimeSpan Reserve(long bytes)
    {
        lock (_gate)
        {
            if (_bytesPerSecond is not long rate) return TimeSpan.Zero;
            double now = _now();
            _next = Math.Max(_next, now - MaxBurstSeconds) + bytes / (double)rate;
            return _next > now ? TimeSpan.FromSeconds(_next - now) : TimeSpan.Zero;
        }
    }

    /// <summary>Counts bytes just moved, then waits until more may go. Cancellable.</summary>
    internal void Throttle(long bytes, CancellationToken token)
    {
        if (Reserve(bytes) == TimeSpan.Zero) return;

        // Wait in short slices, re-reading the deadline, so a limit raised (or removed)
        // mid-wait takes effect at once.
        while (true)
        {
            double wait;
            lock (_gate)
            {
                if (_bytesPerSecond is null) return;
                wait = _next - _now();
            }
            if (wait <= 0) return;

            var slice = TimeSpan.FromSeconds(wait) < MaxWaitSlice ? TimeSpan.FromSeconds(wait) : MaxWaitSlice;
            if (token.WaitHandle.WaitOne(slice)) token.ThrowIfCancellationRequested();
        }
    }
}
