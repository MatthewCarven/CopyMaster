using CopyMaster.Engine.Control;
using CopyMaster.Engine.Jobs;

namespace CopyMaster.Engine.Planning;

/// <summary>
/// Lists a job's sources into a <see cref="JobPlan"/>. Conflicts aren't decided here: the
/// destination can change before each item runs, so the runner checks as it goes.
/// </summary>
internal sealed class JobPlanner(JobSpec spec, JobControl control, bool forceCrossVolume)
{
    // Hidden and system files too (the default options skip them), and errors not swallowed.
    private static readonly EnumerationOptions Everything = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <exception cref="DirectoryNotFoundException">The destination folder doesn't exist.</exception>
    public JobPlan Plan()
    {
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(spec.Destination));
        if (!Directory.Exists(destination))
        {
            throw new DirectoryNotFoundException($"The destination folder doesn't exist: {destination}");
        }

        var plan = new JobPlan();
        foreach (string source in spec.Sources)
        {
            control.Checkpoint();
            AddSource(plan, source, destination);
        }
        return plan;
    }

    private void AddSource(JobPlan plan, string raw, string destination)
    {
        string source;
        try
        {
            source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Problem(plan, raw, null, ex.Message);
            return;
        }

        bool isFolder = Directory.Exists(source);
        if (!isFolder && !File.Exists(source))
        {
            Problem(plan, source, null, "It isn't there any more.");
            return;
        }

        string name = Path.GetFileName(source);
        if (name.Length == 0)
        {
            Problem(plan, source, null, "A whole drive can't be copied or moved. Open it and select what's inside.");
            return;
        }

        string target = Path.Combine(destination, name);
        if (isFolder && IsSameOrInside(destination, source))
        {
            Problem(plan, source, target, "The destination is inside this folder.");
            return;
        }

        // Copying or moving an item into the folder it's in does nothing (Matthew, 2026-11-26): a
        // right-drag on the Desktop that lands on the Desktop is usually an icon being moved, and a
        // numbered duplicate beside the original looked like a copy over itself. The shell extension
        // doesn't offer its items for such a drop; this covers the command line and a mixed drag.
        if (string.Equals(Path.GetDirectoryName(source), destination, StringComparison.OrdinalIgnoreCase))
        {
            plan.Notes.Add($"Already in that folder: {source}");
            return;
        }

        if (spec.Operation == CopyOperation.Move)
        {
            if (!forceCrossVolume && Volumes.SameVolume(source, destination))
            {
                AddRename(plan, source, target, isFolder);
            }
            else if (isFolder && IsLink(source))
            {
                // Following it would copy the folder it points at and then delete that.
                Problem(plan, source, target, "A link to a folder can't be moved to another drive.");
            }
            else
            {
                AddCopy(plan, source, target, isFolder, deleteSource: true, topLevel: true);
            }
        }
        else
        {
            AddCopy(plan, source, target, isFolder, deleteSource: false, topLevel: true);
        }
    }

    /// <summary>
    /// A move within one volume: rename the whole item, unless a folder of that name is
    /// already there, in which case merge into it item by item.
    /// </summary>
    internal void AddRename(JobPlan plan, string source, string target, bool isFolder)
    {
        control.Checkpoint();
        if (isFolder && Directory.Exists(target) && !IsLink(source))
        {
            List<FileSystemInfo> children;
            try
            {
                children = Children(source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Problem(plan, source, target, ex.Message);
                return;
            }

            foreach (var child in children)
            {
                AddRename(plan, child.FullName, Path.Combine(target, child.Name), child is DirectoryInfo);
            }
            plan.SourceFoldersToRemove.Add(source);
            return;
        }

        plan.Items.Add(new PlanItem
        {
            Kind = PlanItemKind.Rename, Source = source, Destination = target, IsFolder = isFolder,
        });
    }

    /// <summary>
    /// A copy (or a move across volumes): every folder and file. A top-level link to a
    /// folder is followed, as Explorer does; links further down are skipped, which also
    /// keeps a link loop from going round forever.
    /// </summary>
    internal void AddCopy(JobPlan plan, string source, string target, bool isFolder, bool deleteSource,
        bool topLevel, long? knownLength = null)
    {
        control.Checkpoint();
        if (!isFolder)
        {
            plan.Items.Add(new PlanItem
            {
                Kind = PlanItemKind.CopyFile, Source = source, Destination = target,
                Length = knownLength ?? new FileInfo(source).Length, DeleteSource = deleteSource,
            });
            return;
        }

        if (!topLevel && IsLink(source))
        {
            plan.Notes.Add($"Skipped a link to another folder: {source}");
            return;
        }

        plan.Items.Add(new PlanItem { Kind = PlanItemKind.MakeFolder, Source = source, Destination = target });

        List<FileSystemInfo> children;
        try
        {
            children = Children(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem(plan, source, target, ex.Message);
            return;
        }

        foreach (var child in children)
        {
            AddCopy(plan, child.FullName, Path.Combine(target, child.Name), child is DirectoryInfo, deleteSource,
                topLevel: false, (child as FileInfo)?.Length);
        }

        if (deleteSource) plan.SourceFoldersToRemove.Add(source);
    }

    /// <summary>Files first, then folders, each by name, so a job runs in a predictable order.</summary>
    private static List<FileSystemInfo> Children(string folder)
    {
        var children = new DirectoryInfo(folder).EnumerateFileSystemInfos("*", Everything).ToList();
        children.Sort((a, b) =>
        {
            int kind = (a is DirectoryInfo).CompareTo(b is DirectoryInfo);
            return kind != 0 ? kind : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });
        return children;
    }

    /// <summary>A symbolic link or junction to a folder (not a OneDrive folder, also a reparse point).</summary>
    internal static bool IsLink(string folder)
    {
        var info = new DirectoryInfo(folder);
        return info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is not null;
    }

    private static bool IsSameOrInside(string path, string folder) =>
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static void Problem(JobPlan plan, string source, string? target, string message) =>
        plan.Problems.Add(new FailedItem(source, target, message, 0, FailureStage.Plan));
}
