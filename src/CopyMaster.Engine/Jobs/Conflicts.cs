namespace CopyMaster.Engine.Jobs;

/// <summary>What to do when a file of the same name is already at the destination.</summary>
public enum ConflictPolicy
{
    /// <summary>Ask through <see cref="IJobPrompts"/> (skip if there's no one to ask).</summary>
    Ask,
    Overwrite,
    Skip,
    /// <summary>Keep both: the new one becomes <c>name (2).ext</c>.</summary>
    Rename,
    /// <summary>Overwrite only if the source was modified more recently; else skip.</summary>
    KeepNewer,
}

public enum ConflictResolution
{
    Overwrite,
    Skip,
    Rename,
    KeepNewer,
    CancelJob,
}

/// <summary>Both files' details, for the conflict dialog.</summary>
public sealed record ConflictInfo(
    string Source,
    string Destination,
    long SourceLength,
    DateTime SourceModifiedUtc,
    long DestinationLength,
    DateTime DestinationModifiedUtc);

/// <param name="ApplyToAll">Use this answer for every later conflict in the job.</param>
public sealed record ConflictAnswer(ConflictResolution Resolution, bool ApplyToAll = false);

public static class UniqueNames
{
    /// <summary>
    /// The first free name of the form <c>name (2).ext</c>, <c>name (3).ext</c>, ... beside
    /// <paramref name="path"/>. Folders, and names that are all extension (".gitignore"),
    /// get the number at the end.
    /// </summary>
    public static string Next(string path, bool isFolder)
    {
        string folder = Path.GetDirectoryName(path)!;
        string fileName = Path.GetFileName(path);
        string stem = isFolder ? fileName : Path.GetFileNameWithoutExtension(fileName);
        string extension = isFolder ? "" : Path.GetExtension(fileName);
        if (stem.Length == 0)
        {
            stem = fileName;
            extension = "";
        }

        for (int n = 2; ; n++)
        {
            string candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
            if (!Path.Exists(candidate)) return candidate;
        }
    }
}
