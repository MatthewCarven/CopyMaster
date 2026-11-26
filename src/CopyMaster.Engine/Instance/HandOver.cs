using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using CopyMaster.Engine.Jobs;
using CopyMaster.Engine.Native;

namespace CopyMaster.Engine.Instance;

/// <summary>
/// One CopyMaster.exe per user and session. The first launch claims a named mutex and
/// listens on a named pipe (<see cref="HandOverServer"/>); a later launch sends its job
/// down the pipe (<see cref="HandOverClient"/>) and exits, so every job lands in one queue.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// The mutex and pipe name for this user in this session. Pipe names are machine-wide,
    /// so the user's SID and the session go in it.
    /// </summary>
    public static string DefaultName { get; } =
        $"CopyMaster.{WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName}.{Process.GetCurrentProcess().SessionId}";

    /// <summary>Becomes the instance for <paramref name="name"/>, or returns null if one is already running.</summary>
    public static SingleInstance? TryClaim(string name)
    {
        // Whoever creates the mutex is the instance; it lives as long as that process holds it.
        var mutex = new Mutex(initiallyOwned: false, @"Local\" + name, out bool createdNew);
        if (createdNew) return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    public void Dispose() => _mutex.Dispose();
}

public enum HandOverStatus
{
    /// <summary>The running instance took the job.</summary>
    Accepted,
    /// <summary>It answered, but refused the job (the message says why).</summary>
    Rejected,
    /// <summary>Nothing answered on the pipe (none running, or it's starting or quitting).</summary>
    NoInstance,
}

public sealed record HandOverOutcome(HandOverStatus Status, string? Message = null);

/// <summary>
/// The pipe's messages: a little-endian 32-bit length, then that many bytes of UTF-8. The
/// client sends a job in <see cref="JobFile"/> format, or <see cref="ShowRequest"/>; the
/// server answers <c>OK</c> or <c>ERR</c> and a reason.
/// </summary>
internal static class PipeMessages
{
    public const string ShowRequest = "show";
    public const int MaxLength = 64 << 20; // a drag of 100,000 files is ~10 MB

    public static async Task WriteAsync(Stream pipe, string text, CancellationToken token)
    {
        byte[] body = Encoding.UTF8.GetBytes(text);
        byte[] message = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(message, body.Length);
        body.CopyTo(message, 4);
        await pipe.WriteAsync(message, token).ConfigureAwait(false);
        await pipe.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<string> ReadAsync(Stream pipe, CancellationToken token)
    {
        byte[] header = new byte[4];
        await pipe.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 0 or > MaxLength) throw new InvalidDataException("Bad message length on the pipe.");
        byte[] body = new byte[length];
        await pipe.ReadExactlyAsync(body, token).ConfigureAwait(false);
        return Encoding.UTF8.GetString(body);
    }
}

/// <summary>
/// The running instance's end of the pipe. Each request is handed to the callback on a
/// thread-pool thread: a job, or null for "just show the window".
/// </summary>
public sealed class HandOverServer : IDisposable
{
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(10);

    private readonly string _pipeName;
    private readonly Action<JobSpec?> _handle;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listening;
    private int _disposed;

    public HandOverServer(string pipeName, Action<JobSpec?> handle)
    {
        _pipeName = pipeName;
        _handle = handle;
    }

    public void Start()
    {
        if (_listening is not null) throw new InvalidOperationException("Already listening.");
        var first = CreatePipe(); // created here, so a client can connect as soon as Start returns
        _listening = Task.Run(() => ListenAsync(first, _stop.Token));
    }

    private NamedPipeServerStream CreatePipe() =>
        new(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ListenAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        while (true)
        {
            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                // The client vanished between connecting and us noticing; listen again.
                await pipe.DisposeAsync().ConfigureAwait(false);
                pipe = await NextPipeAsync(token).ConfigureAwait(false);
                continue;
            }

            var connected = pipe;
            _ = Task.Run(() => ServeAsync(connected, token), CancellationToken.None);
            pipe = await NextPipeAsync(token).ConfigureAwait(false);
        }
    }

    private async Task<NamedPipeServerStream> NextPipeAsync(CancellationToken token)
    {
        while (true)
        {
            try
            {
                return CreatePipe();
            }
            catch (IOException)
            {
                await Task.Delay(50, token).ConfigureAwait(false); // all instances busy: very unlikely
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        await using var _ = pipe.ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ClientTimeout);
        try
        {
            string request = await PipeMessages.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
            string reply;
            try
            {
                _handle(request == PipeMessages.ShowRequest ? null : JobFile.Parse(request));
                reply = "OK";
            }
            catch (Exception ex)
            {
                reply = "ERR " + ex.Message;
            }
            await PipeMessages.WriteAsync(pipe, reply, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException
                                       or ObjectDisposedException)
        {
            // The client went away or sent rubbish; it will report that itself.
        }
    }

    /// <summary>Stops listening. Safe to call more than once (the window calls it as it starts quitting).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try
        {
            _listening?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Stopping anyway.
        }
        _stop.Dispose();
    }
}

/// <summary>A later launch's end of the pipe.</summary>
public static class HandOverClient
{
    /// <summary>
    /// Hands a job (or null: "show your window") to the running instance. Waits up to
    /// <paramref name="timeout"/> for the pipe to appear, so an instance that is still
    /// starting is found.
    /// </summary>
    public static HandOverOutcome Send(string pipeName, JobSpec? spec, TimeSpan timeout) =>
        SendAsync(pipeName, spec, timeout).GetAwaiter().GetResult();

    public static async Task<HandOverOutcome> SendAsync(string pipeName, JobSpec? spec, TimeSpan timeout)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(timeout + TimeSpan.FromSeconds(10));
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            return new HandOverOutcome(HandOverStatus.NoInstance);
        }

        // We're the process the user (or Explorer) just started, so we may bring a window to
        // the front; pass that on, so the running instance can show itself.
        if (NativeMethods.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverId))
        {
            NativeMethods.AllowSetForegroundWindow(serverId);
        }

        try
        {
            await PipeMessages.WriteAsync(pipe, spec is null ? PipeMessages.ShowRequest : JobFile.Format(spec),
                deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // It hung up before taking the request: quitting, most likely. Safe to try again.
            return new HandOverOutcome(HandOverStatus.NoInstance);
        }

        try
        {
            string reply = await PipeMessages.ReadAsync(pipe, deadline.Token).ConfigureAwait(false);
            return reply == "OK"
                ? new HandOverOutcome(HandOverStatus.Accepted)
                : new HandOverOutcome(HandOverStatus.Rejected, reply.StartsWith("ERR ", StringComparison.Ordinal) ? reply[4..] : reply);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException)
        {
            // It has the request but didn't answer. It may have queued the job, so trying
            // again could run it twice: report it instead.
            return new HandOverOutcome(HandOverStatus.Rejected, "The running CopyMaster didn't answer.");
        }
    }
}
