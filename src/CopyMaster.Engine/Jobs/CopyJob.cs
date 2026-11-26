using System.Diagnostics;
using System.Runtime.InteropServices;
using CopyMaster.Engine.Control;
using CopyMaster.Engine.Copying;
using CopyMaster.Engine.Native;
using CopyMaster.Engine.Planning;

namespace CopyMaster.Engine.Jobs;

/// <summary>
/// One copy or move job. <see cref="Run"/> lists the sources, then works through them on
/// the calling thread (give it a thread of its own); <see cref="Control"/> pauses, cancels
/// and limits it from any other thread.
///
/// Each file is copied to a temporary name beside its destination, verified there if
/// verify is on, and only then renamed into place. So a file appears under its real name
/// only once it's complete (and checked), and an overwritten file is replaced in one step.
/// </summary>
public sealed class CopyJob : IDisposable
{
    private const FileAttributes KeptFolderAttributes = FileAttributes.ReadOnly | FileAttributes.Hidden |
        FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed;

    private static readonly long ReportInterval = Stopwatch.Frequency / 10;

    private readonly JobSpec _spec;
    private readonly JobOptions _options;
    private readonly IJobPrompts? _prompts;
    private readonly JobResult _result = new();
    private readonly List<CreatedFolder> _createdFolders = [];
    private readonly SpeedMeter _speed = new();
    private JobPlanner? _planner;
    private JobPlan? _plan;
    private ConflictResolution? _answerForAll;

    private JobPhase _phase = JobPhase.Scanning;
    private int _itemsDone, _itemsTotal;
    private long _bytesDone, _bytesTotal, _bytesTransferred;
    private string? _current;
    private long _lastReport;

    public CopyJob(JobSpec spec, JobOptions? options = null, IJobPrompts? prompts = null)
    {
        _spec = spec;
        _options = options ?? new JobOptions();
        _prompts = prompts;
        if (_options.SpeedLimit is long limit) Control.SpeedLimit = limit;
    }

    public JobSpec Spec => _spec;

    public JobOptions Options => _options;

    public JobControl Control { get; } = new();

    /// <summary>Raised on the job's thread, at most every 100 ms, plus at each phase change.</summary>
    public event Action<JobProgress>? ProgressChanged;

    // Test seams.
    internal bool ForceCrossVolume { get; init; }
    internal Action<string, string>? BeforeCopy { get; init; } // (source, temporary file)
    internal Action<string>? BeforeVerify { get; init; }       // (temporary file)
    internal Func<string, long?>? FreeSpace { get; init; }     // (destination) → bytes free
    internal Func<uint, ClipboardOutcome> EmptyClipboard { get; init; } = CutPaste.EmptyClipboardIfUnchanged;

    private int Stages => _options.Verify ? 2 : 1;

    public JobResult Run()
    {
        if (_plan is not null) throw new InvalidOperationException("A job runs once; use RetryFailed for its failures.");

        var clock = Stopwatch.StartNew();
        try
        {
            Report(force: true);
            _planner = new JobPlanner(_spec, Control, ForceCrossVolume);
            _plan = _planner.Plan();
            foreach (var problem in _plan.Problems) _result.Add(problem);
            foreach (string note in _plan.Notes) _result.AddNote(note);
            AddToTotals(_plan.Items);

            CheckRoom(_plan.Items);
            Execute(_plan.Items);
            Finish();
            CompleteCut();
        }
        catch (OperationCanceledException)
        {
            if (_result.StoppedReason is null) _result.Cancelled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _result.StoppedReason = ex.Message; // e.g. the destination folder isn't there
        }
        finally
        {
            _result.Elapsed += clock.Elapsed;
            SetPhase(JobPhase.Done);
        }
        return _result;
    }

    /// <summary>Runs the failed items again (not problems found while listing).</summary>
    public JobResult RetryFailed()
    {
        if (_plan is null) throw new InvalidOperationException("Run the job first.");
        if (Control.IsCancelled) throw new InvalidOperationException("A cancelled job can't be retried.");

        var retry = _result.TakeRetryable();
        if (retry.Count == 0) return _result;

        var clock = Stopwatch.StartNew();
        _result.StoppedReason = null;
        _itemsDone = _itemsTotal = 0;
        _bytesDone = _bytesTotal = 0;

        var deletes = retry.Where(f => f.Stage == FailureStage.DeleteSource).ToList();
        var items = retry.Where(f => f.Stage != FailureStage.DeleteSource)
            .SelectMany(f => f.Subtree.Prepend(f.Item!)).ToList();
        AddToTotals(items);
        _itemsTotal += deletes.Count;

        try
        {
            foreach (var failure in deletes)
            {
                Control.Checkpoint();
                DeleteSource(failure.Item!, failure.Identical);
                _itemsDone++;
            }
            Execute(items);
            Finish();
            CompleteCut();
        }
        catch (OperationCanceledException)
        {
            if (_result.StoppedReason is null) _result.Cancelled = true;
        }
        finally
        {
            _result.Elapsed += clock.Elapsed;
            SetPhase(JobPhase.Done);
        }
        return _result;
    }

    /// <summary>
    /// A cut in Explorer pasted with CopyMaster Paste: once all of it has moved, empty the clipboard
    /// as Explorer would, if it still holds that cut (<see cref="CutPaste"/>). Run and RetryFailed
    /// both end here, so a cut whose last failures are retried successfully is finished too.
    /// </summary>
    private void CompleteCut()
    {
        if (CutPaste.Completes(_spec, _result)) _result.Clipboard = EmptyClipboard(_spec.ClipboardSequence!.Value);
    }

    /// <summary>
    /// Before copying: if the files to copy add up to more than the destination has free,
    /// ask whether to start anyway. Renames (a move within a volume) take no room. A rough
    /// check: conflicts skipped later, and files overwritten, would need less.
    /// </summary>
    private void CheckRoom(List<PlanItem> items)
    {
        if (_prompts is null) return;
        long needed = items.Where(i => i.Kind == PlanItemKind.CopyFile).Sum(i => i.Length);
        if (needed == 0) return;

        string destination = Path.GetFullPath(_spec.Destination);
        if ((FreeSpace ?? Volumes.FreeSpace)(destination) is not long free || free >= needed) return;
        if (_prompts.StartWithoutRoom(destination, needed, free)) return;

        _result.StoppedReason = $"Not enough room: it needs {Units.Bytes(needed)}, and there's {Units.Bytes(free)} free.";
        Control.Cancel();
        throw new OperationCanceledException(Control.Token);
    }

    private void Execute(List<PlanItem> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            Control.Checkpoint();
            var item = items[i];
            try
            {
                switch (item.Kind)
                {
                    case PlanItemKind.MakeFolder: MakeFolder(item); break;
                    case PlanItemKind.CopyFile: CopyFileItem(item); break;
                    case PlanItemKind.Rename: Rename(item, items, i); break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (item.Kind != PlanItemKind.MakeFolder)
                {
                    Fail(item, ex, FailureStage.Copy);
                    continue;
                }

                // Nothing can go into a folder that couldn't be made: its contents wait with
                // the failure and are retried with it.
                int end = i + 1;
                while (end < items.Count && IsInside(items[end].Destination, item.Destination)) end++;
                var subtree = items.GetRange(i + 1, end - i - 1);
                foreach (var skipped in subtree.Where(s => s.CountsAsItem))
                {
                    _itemsDone++;
                    _bytesDone += Stages * skipped.Length;
                }
                Fail(item, ex, FailureStage.Copy, subtree: subtree);
                i = end - 1;
            }
        }
    }

    private void MakeFolder(PlanItem item)
    {
        if (Directory.Exists(item.Destination)) return; // merge into the folder that's there
        if (File.Exists(item.Destination)) throw new IOException("A file with that name is already there.");

        // Read the source folder's details now: deleting files from it (a move) changes its date.
        var source = new DirectoryInfo(item.Source);
        var details = new CreatedFolder(item.Destination, source.CreationTimeUtc, source.LastWriteTimeUtc,
            source.Attributes & KeptFolderAttributes);
        Directory.CreateDirectory(item.Destination);
        _createdFolders.Add(details);
    }

    private void CopyFileItem(PlanItem item)
    {
        long itemStart = _bytesDone;
        long itemBytes = Stages * item.Length;
        _current = item.Source;
        try
        {
            string destination = item.Destination;
            bool replace = false;
            if (Path.Exists(destination))
            {
                if (Directory.Exists(destination)) throw new IOException("A folder with that name is already there.");

                if (Volumes.SameFile(item.Source, destination))
                {
                    if (item.DeleteSource)
                    {
                        _result.Skipped++; // moving a file onto itself
                        return;
                    }
                    destination = UniqueNames.Next(destination, isFolder: false);
                }
                else
                {
                    bool identical = Identical(item.Source, destination, out long compared);
                    itemBytes += compared;
                    if (identical)
                    {
                        // Nothing to copy and nothing to ask; a move only has to drop the source.
                        _result.Identical++;
                        if (item.DeleteSource) DeleteIdenticalSource(item, destination);
                        return;
                    }

                    switch (Resolve(item.Source, destination))
                    {
                        case ConflictResolution.Skip:
                            _result.Skipped++;
                            return;
                        case ConflictResolution.Overwrite:
                            replace = true;
                            break;
                        case ConflictResolution.Rename:
                            destination = UniqueNames.Next(destination, isFolder: false);
                            break;
                    }
                }
            }

            CopyThroughTemp(item.Source, destination, replace, item.Length, ref itemBytes);
            _result.FilesCopied++;
            _result.BytesCopied += item.Length;
            if (item.DeleteSource) DeleteSource(item);
        }
        finally
        {
            _itemsDone++;
            _bytesDone = itemStart + itemBytes;
            Report();
        }
    }

    private void CopyThroughTemp(string source, string destination, bool replace, long length, ref long itemBytes)
    {
        string folder = Path.GetDirectoryName(destination)!;
        int mismatches = 0;
        while (true)
        {
            string temp = Path.Combine(folder, $".~cm{Random.Shared.Next():x8}.tmp");
            try
            {
                SetPhase(JobPhase.Copying);
                BeforeCopy?.Invoke(source, temp);
                FileCopier.Copy(source, temp, Control, OnBytes);

                // CopyFile2 keeps the modified date, but the copy gets a new created date.
                File.SetCreationTimeUtc(temp, File.GetCreationTimeUtc(source));

                if (_options.Verify)
                {
                    BeforeVerify?.Invoke(temp);
                    SetPhase(JobPhase.Verifying);
                    if (!Verifier.SameContents(source, temp, Control, OnBytes))
                    {
                        DeleteQuietly(temp);
                        if (++mismatches >= 2)
                        {
                            throw new IOException("The copy didn't match the original, twice. The destination " +
                                                  "disk, or the connection to it, may be faulty.");
                        }
                        GrowItem(length, ref itemBytes); // copy it again
                        continue;
                    }
                }

                Commit(temp, destination, replace);
                return;
            }
            catch (IOException ex) when (Win32.IsDiskFull(ex.HResult))
            {
                DeleteQuietly(temp);
                switch (_prompts?.DiskFull(destination, ex.Message) ?? DiskFullAnswer.StopJob)
                {
                    case DiskFullAnswer.Retry:
                        GrowItem(length, ref itemBytes);
                        continue;
                    case DiskFullAnswer.SkipFile:
                        throw;
                    default:
                        _result.StoppedReason = "The destination disk is full.";
                        Control.Cancel();
                        throw new OperationCanceledException(Control.Token);
                }
            }
            catch
            {
                DeleteQuietly(temp);
                throw;
            }
        }
    }

    /// <summary>Renames the finished temporary file into place.</summary>
    private static void Commit(string temp, string destination, bool replace)
    {
        if (replace) ClearReadOnly(destination);
        uint flags = replace ? NativeMethods.MOVEFILE_REPLACE_EXISTING : 0;
        if (NativeMethods.MoveFileEx(LongPath.Extended(temp), LongPath.Extended(destination), flags)) return;

        int code = Marshal.GetLastPInvokeError();
        throw code is Win32.ERROR_ALREADY_EXISTS or Win32.ERROR_FILE_EXISTS
            ? new IOException("Something with that name appeared at the destination during the copy.",
                Win32.HResultFromWin32(code))
            : Win32.Error(Win32.HResultFromWin32(code));
    }

    /// <param name="identical">
    /// The destination already held the same bytes, so nothing was copied (and the file
    /// is counted in Identical, not in FilesCopied).
    /// </param>
    private void DeleteSource(PlanItem item, bool identical = false)
    {
        try
        {
            ClearReadOnly(item.Source);
            File.Delete(item.Source);
            if (!identical)
            {
                _result.FilesCopied--;
                _result.ItemsMoved++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(item, ex, FailureStage.DeleteSource,
                identical
                    ? "It's already at the destination, but the original couldn't be deleted: "
                    : "Copied, but the original couldn't be deleted: ",
                identical: identical);
        }
    }

    /// <summary>
    /// A move whose file is already at the destination, byte for byte: deletes the source,
    /// but only once the two are proven to be different files. A hard link, or one share
    /// reached by two names, compares identical because it is the same file.
    /// </summary>
    private void DeleteIdenticalSource(PlanItem item, string destination)
    {
        if (!Volumes.DifferentFiles(item.Source, destination))
        {
            _result.Identical--;
            _result.Skipped++;
            _result.AddNote($"Left in place: couldn't make sure it isn't the destination itself: {item.Source}");
            return;
        }
        DeleteSource(item, identical: true);
    }

    /// <summary>
    /// True if the destination already holds exactly the source's bytes, so there's nothing
    /// to copy and nothing to ask. Sizes first (free), then byte for byte, with the
    /// destination read back from its disk, as verify does. Anything unreadable counts as
    /// different, which leads to the usual conflict handling.
    /// </summary>
    /// <param name="compared">Bytes read to compare, which the plan didn't count; 0 if the sizes differ.</param>
    private bool Identical(string source, string destination, out long compared)
    {
        compared = 0;
        long length;
        try
        {
            length = new FileInfo(source).Length;
            if (new FileInfo(destination).Length != length) return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        compared = length;
        _bytesTotal += length;
        SetPhase(JobPhase.Comparing);
        try
        {
            return Verifier.SameContents(source, destination, Control, OnBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A move within one volume: one rename, whatever the size.</summary>
    private void Rename(PlanItem item, List<PlanItem> items, int index)
    {
        _current = item.Source;
        SetPhase(JobPhase.Moving);
        try
        {
            string destination = item.Destination;
            uint flags = 0;
            if (Path.Exists(destination))
            {
                if (Volumes.SameFile(item.Source, destination))
                {
                    _result.Skipped++;
                    return;
                }

                bool destinationIsFolder = Directory.Exists(destination);
                if (item.IsFolder && destinationIsFolder)
                {
                    // A folder of that name turned up after planning: merge into it.
                    var merge = new JobPlan();
                    _planner!.AddRename(merge, item.Source, destination, isFolder: true);
                    Splice(merge, items, index);
                    return;
                }
                if (item.IsFolder != destinationIsFolder)
                {
                    throw new IOException(destinationIsFolder
                        ? "A folder with that name is already there."
                        : "A file with that name is already there.");
                }

                // Two files from here on.
                if (Identical(item.Source, destination, out _))
                {
                    _result.Identical++;
                    DeleteIdenticalSource(item, destination);
                    return;
                }
                SetPhase(JobPhase.Moving);

                switch (Resolve(item.Source, destination))
                {
                    case ConflictResolution.Skip:
                        _result.Skipped++;
                        return;
                    case ConflictResolution.Overwrite:
                        ClearReadOnly(destination);
                        flags = NativeMethods.MOVEFILE_REPLACE_EXISTING;
                        break;
                    case ConflictResolution.Rename:
                        destination = UniqueNames.Next(destination, isFolder: false);
                        break;
                }
            }

            if (NativeMethods.MoveFileEx(LongPath.Extended(item.Source), LongPath.Extended(destination), flags))
            {
                _result.ItemsMoved++;
                return;
            }

            int code = Marshal.GetLastPInvokeError();
            if (code != Win32.ERROR_NOT_SAME_DEVICE) throw Win32.Error(Win32.HResultFromWin32(code));

            // Another volume after all (a mount point, or one share by two names): copy, then delete.
            if (item.IsFolder && JobPlanner.IsLink(item.Source))
            {
                throw new IOException("A link to a folder can't be moved to another drive.");
            }
            var copy = new JobPlan();
            _planner!.AddCopy(copy, item.Source, destination, item.IsFolder, deleteSource: true, topLevel: true);
            Splice(copy, items, index);
        }
        finally
        {
            _itemsDone++;
            Report();
        }
    }

    /// <summary>Puts newly planned items straight after the current one.</summary>
    private void Splice(JobPlan extra, List<PlanItem> items, int index)
    {
        items.InsertRange(index + 1, extra.Items);
        AddToTotals(extra.Items);
        _plan!.SourceFoldersToRemove.InsertRange(0, extra.SourceFoldersToRemove); // descendants go first
        foreach (var problem in extra.Problems) _result.Add(problem);
        foreach (string note in extra.Notes) _result.AddNote(note);
    }

    private ConflictResolution Resolve(string source, string destination)
    {
        ConflictResolution answer;
        if (_answerForAll is { } forAll)
        {
            answer = forAll;
        }
        else if (_options.Conflicts != ConflictPolicy.Ask)
        {
            answer = _options.Conflicts switch
            {
                ConflictPolicy.Overwrite => ConflictResolution.Overwrite,
                ConflictPolicy.Rename => ConflictResolution.Rename,
                ConflictPolicy.KeepNewer => ConflictResolution.KeepNewer,
                _ => ConflictResolution.Skip,
            };
        }
        else if (_prompts is null)
        {
            answer = ConflictResolution.Skip;
        }
        else
        {
            var s = new FileInfo(source);
            var d = new FileInfo(destination);
            var reply = _prompts.ResolveConflict(new ConflictInfo(source, destination,
                s.Length, s.LastWriteTimeUtc, d.Length, d.LastWriteTimeUtc));
            if (reply.ApplyToAll) _answerForAll = reply.Resolution;
            answer = reply.Resolution;
        }

        switch (answer)
        {
            case ConflictResolution.CancelJob:
                Control.Cancel();
                throw new OperationCanceledException(Control.Token);
            case ConflictResolution.KeepNewer:
                return File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(destination)
                    ? ConflictResolution.Overwrite
                    : ConflictResolution.Skip;
            default:
                return answer;
        }
    }

    private void Finish()
    {
        SetPhase(JobPhase.Finishing);

        // Folder dates last: files written into a folder change its modified date.
        for (int i = _createdFolders.Count - 1; i >= 0; i--)
        {
            var folder = _createdFolders[i];
            try
            {
                var info = new DirectoryInfo(folder.Path)
                {
                    CreationTimeUtc = folder.CreatedUtc,
                    LastWriteTimeUtc = folder.ModifiedUtc,
                };
                info.Attributes = (info.Attributes & ~KeptFolderAttributes) | folder.Attributes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Cosmetic; the files are what matter.
            }
        }
        _createdFolders.Clear();

        foreach (string folder in _plan!.SourceFoldersToRemove) RemoveIfEmpty(folder);

        Volumes.NotifyExplorer(Path.GetFullPath(_spec.Destination));
        if (_spec.Operation == CopyOperation.Move)
        {
            foreach (string parent in _spec.Sources.Select(s => Path.GetDirectoryName(Path.GetFullPath(s)))
                         .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Volumes.NotifyExplorer(parent);
            }
        }
    }

    private static void RemoveIfEmpty(string folder)
    {
        try
        {
            if (!Directory.Exists(folder) || Directory.EnumerateFileSystemEntries(folder).Any()) return;
            var info = new DirectoryInfo(folder);
            if (info.Attributes.HasFlag(FileAttributes.ReadOnly)) info.Attributes &= ~FileAttributes.ReadOnly;
            info.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in place; nothing was lost.
        }
    }

    private void Fail(PlanItem item, Exception ex, FailureStage stage, string? prefix = null,
        IReadOnlyList<PlanItem>? subtree = null, bool identical = false) =>
        _result.Add(new FailedItem(item.Source, item.Destination, prefix + ex.Message, ex.HResult, stage)
        {
            Item = item,
            Subtree = subtree ?? [],
            Identical = identical,
        });

    private void AddToTotals(IEnumerable<PlanItem> items)
    {
        foreach (var item in items.Where(i => i.CountsAsItem))
        {
            _itemsTotal++;
            _bytesTotal += Stages * item.Length;
        }
    }

    /// <summary>A file is going round again (a verify mismatch, a retry after disk full).</summary>
    private void GrowItem(long length, ref long itemBytes)
    {
        itemBytes += Stages * length;
        _bytesTotal += Stages * length;
    }

    private void OnBytes(long bytes)
    {
        _bytesDone += bytes;
        _bytesTransferred += bytes;
        Report();
    }

    private void SetPhase(JobPhase phase)
    {
        if (_phase == phase) return;
        _phase = phase;
        Report(force: true);
    }

    private void Report(bool force = false)
    {
        long now = Stopwatch.GetTimestamp();
        if (!force && now - _lastReport < ReportInterval) return;
        _lastReport = now;
        double speed = _speed.Sample(now, _bytesTransferred);
        ProgressChanged?.Invoke(new JobProgress(_phase, _itemsDone, _itemsTotal, _bytesDone, _bytesTotal,
            _current, speed));
    }

    private static bool IsInside(string path, string folder) =>
        path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void ClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stray temporary file is untidy, not harmful.
        }
    }

    public void Dispose() => Control.Dispose();

    private sealed record CreatedFolder(string Path, DateTime CreatedUtc, DateTime ModifiedUtc, FileAttributes Attributes);

    /// <summary>Bytes per second over the last few seconds.</summary>
    private sealed class SpeedMeter
    {
        private static readonly long Window = Stopwatch.Frequency * 3;
        private readonly Queue<(long Time, long Bytes)> _samples = new();

        public double Sample(long now, long bytes)
        {
            _samples.Enqueue((now, bytes));
            while (_samples.Count > 2 && now - _samples.Peek().Time > Window) _samples.Dequeue();
            var (time, start) = _samples.Peek();
            double seconds = (now - time) / (double)Stopwatch.Frequency;
            return seconds > 0.25 ? (bytes - start) / seconds : 0;
        }
    }
}
