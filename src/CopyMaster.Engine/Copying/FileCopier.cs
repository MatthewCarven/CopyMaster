using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using CopyMaster.Engine.Control;
using CopyMaster.Engine.Native;
using static CopyMaster.Engine.Native.NativeMethods;

namespace CopyMaster.Engine.Copying;

/// <summary>
/// Copies one file with CopyFile2, which keeps timestamps, attributes and alternate data
/// streams (Zone.Identifier included). Pause and the speed limit work by blocking inside
/// its per-chunk progress callback; cancel returns COPYFILE2_PROGRESS_CANCEL, which makes
/// CopyFile2 delete the half-written destination itself.
/// </summary>
internal static unsafe class FileCopier
{
    /// <summary>Copies <paramref name="source"/> to <paramref name="destination"/>, which must not exist.</summary>
    /// <param name="onBytes">Called with each chunk's size as it lands.</param>
    /// <exception cref="OperationCanceledException">The job was cancelled.</exception>
    /// <exception cref="IOException">The copy failed; HResult says why.</exception>
    public static void Copy(string source, string destination, JobControl control, Action<long> onBytes)
    {
        var state = new CallbackState(control, onBytes);
        var handle = GCHandle.Alloc(state);
        try
        {
            var parameters = new COPYFILE2_EXTENDED_PARAMETERS
            {
                dwSize = (uint)sizeof(COPYFILE2_EXTENDED_PARAMETERS),
                dwCopyFlags = COPY_FILE_FAIL_IF_EXISTS,
                pProgressRoutine = &Progress,
                pvCallbackContext = (void*)GCHandle.ToIntPtr(handle),
            };

            int hr = CopyFile2(LongPath.Extended(source), LongPath.Extended(destination), &parameters);

            if (state.Failure is not null) ExceptionDispatchInfo.Throw(state.Failure);
            if (state.Cancelled) throw new OperationCanceledException(control.Token);
            if (hr < 0) throw Win32.Error(hr);
        }
        finally
        {
            handle.Free();
        }
    }

    private sealed class CallbackState(JobControl control, Action<long> onBytes)
    {
        public readonly JobControl Control = control;
        public readonly Action<long> OnBytes = onBytes;
        public ulong StreamDone; // bytes of the current stream already reported
        public bool Cancelled;
        public Exception? Failure;
    }

    [UnmanagedCallersOnly]
    private static int Progress(COPYFILE2_MESSAGE* message, void* context)
    {
        var state = (CallbackState)GCHandle.FromIntPtr((nint)context).Target!;
        try
        {
            switch (message->Type)
            {
                case COPYFILE2_CALLBACK_STREAM_STARTED:
                    state.StreamDone = 0;
                    break;

                case COPYFILE2_CALLBACK_CHUNK_STARTED:
                    state.Control.Checkpoint();
                    break;

                case COPYFILE2_CALLBACK_CHUNK_FINISHED:
                    ulong done = message->ChunkFinished.uliStreamBytesTransferred;
                    long delta = (long)(done - state.StreamDone);
                    state.StreamDone = done;
                    state.OnBytes(delta);
                    state.Control.AfterChunk(delta);
                    break;
            }
            return COPYFILE2_PROGRESS_CONTINUE;
        }
        catch (OperationCanceledException)
        {
            state.Cancelled = true;
            return COPYFILE2_PROGRESS_CANCEL;
        }
        catch (Exception ex)
        {
            // Nothing may escape into native code.
            state.Failure = ex;
            return COPYFILE2_PROGRESS_CANCEL;
        }
    }
}
