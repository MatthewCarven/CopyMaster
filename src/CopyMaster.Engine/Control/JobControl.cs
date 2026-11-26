namespace CopyMaster.Engine.Control;

/// <summary>
/// Pause, resume, cancel and the speed limit for one job. Safe to call from any thread
/// (the UI) while the job runs on its own.
/// </summary>
public sealed class JobControl : IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly ManualResetEventSlim _running = new(initialState: true);

    public RateLimiter Limiter { get; } = new();

    public CancellationToken Token => _cancel.Token;

    public bool IsPaused => !_running.IsSet;

    public bool IsCancelled => _cancel.IsCancellationRequested;

    /// <summary>Bytes per second, or null for unlimited. Takes effect at once.</summary>
    public long? SpeedLimit
    {
        get => Limiter.BytesPerSecond;
        set => Limiter.BytesPerSecond = value;
    }

    /// <summary>Pauses at the next chunk (also mid-file).</summary>
    public void Pause() => _running.Reset();

    public void Resume() => _running.Set();

    /// <summary>Stops the job; a half-copied file is deleted. Wakes a paused job.</summary>
    public void Cancel() => _cancel.Cancel();

    /// <summary>Throws if cancelled; blocks while paused.</summary>
    internal void Checkpoint()
    {
        Token.ThrowIfCancellationRequested();
        _running.Wait(Token);
    }

    /// <summary>Called after each chunk copied or verified.</summary>
    internal void AfterChunk(long bytes)
    {
        Checkpoint();
        Limiter.Throttle(bytes, Token);
    }

    public void Dispose()
    {
        _cancel.Dispose();
        _running.Dispose();
    }
}
