using CopyMaster.Engine;
using CopyMaster.Engine.Jobs;
using CopyMaster.Engine.Scheduling;

namespace CopyMaster;

/// <summary>The words the window uses for a job.</summary>
internal static class JobText
{
    /// <summary>"Copy “Photos” to D:\Backup", "Move 3 items to Z:\Archive".</summary>
    public static string Title(JobSpec spec)
    {
        string verb = spec.Operation == CopyOperation.Move ? "Move" : "Copy";
        string what = spec.Sources.Count == 1 ? $"“{NameOf(spec.Sources[0])}”" : $"{spec.Sources.Count} items";
        return $"{verb} {what} to {spec.Destination}";
    }

    /// <summary>Where the sources are: their folder, or "several folders".</summary>
    public static string From(JobSpec spec)
    {
        var folders = spec.Sources.Select(s => Path.GetDirectoryName(s.TrimEnd('\\')) ?? s)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return folders.Count == 1 ? $"From {folders[0]}" : $"From {folders.Count} folders";
    }

    public static string Options(JobOptions options)
    {
        var parts = new List<string> { options.Verify ? "Verify on" : "Verify off" };
        parts.Add(options.Conflicts switch
        {
            ConflictPolicy.Ask => "ask on conflicts",
            ConflictPolicy.Overwrite => "overwrite on conflicts",
            ConflictPolicy.Skip => "skip conflicts",
            ConflictPolicy.Rename => "keep both on conflicts",
            ConflictPolicy.KeepNewer => "keep the newer on conflicts",
            _ => "",
        });
        return string.Join(" · ", parts);
    }

    public static string ConflictPolicyName(ConflictPolicy policy) => policy switch
    {
        ConflictPolicy.Ask => "Ask each time",
        ConflictPolicy.Overwrite => "Overwrite",
        ConflictPolicy.Skip => "Skip",
        ConflictPolicy.Rename => "Keep both (rename the copy)",
        ConflictPolicy.KeepNewer => "Keep the newer file",
        _ => policy.ToString(),
    };

    /// <summary>What a finished job did: "Copied 12 files (1.2 GB), 3 already there, skipped 2 in 34 s".</summary>
    public static string Summary(QueuedJob job)
    {
        if (job.Crash is not null) return "Something went wrong: " + job.Crash;
        if (job.Result is not { } result) return "";

        var parts = new List<string>();
        if (result.FilesCopied > 0) parts.Add($"copied {Count(result.FilesCopied, "file")}");
        if (result.ItemsMoved > 0) parts.Add($"moved {Count(result.ItemsMoved, "item")}");
        string text = parts.Count > 0 ? string.Join(", ", parts)
            : job.Spec.Operation == CopyOperation.Move ? "nothing moved" : "nothing copied";
        if (result.BytesCopied > 0) text += $" ({Units.Bytes(result.BytesCopied)})";
        if (result.Identical > 0) text += $", {result.Identical:N0} already there";
        if (result.Skipped > 0) text += $", skipped {result.Skipped}";
        text += $" in {Units.Duration(result.Elapsed)}";
        if (result.BytesCopied > 0 && result.Elapsed.TotalSeconds >= 1)
        {
            text += $", {Units.Speed(result.BytesCopied / result.Elapsed.TotalSeconds)} on average";
        }
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>The bad news under the summary: failures, why it stopped, notes.</summary>
    public static string Problems(QueuedJob job)
    {
        if (job.Result is not { } result) return "";
        var parts = new List<string>();
        if (result.Failures.Count > 0) parts.Add($"{Count(result.Failures.Count, "item")} failed");
        if (result.StoppedReason is not null) parts.Add("Stopped: " + result.StoppedReason);
        else if (result.Cancelled) parts.Add("Cancelled");
        if (result.Notes.Count > 0) parts.Add(Count(result.Notes.Count, "note"));
        return string.Join(" · ", parts);
    }

    public static string Count(int n, string thing) => n == 1 ? $"1 {thing}" : $"{n:N0} {thing}s";

    public static string NameOf(string path)
    {
        string name = Path.GetFileName(path.TrimEnd('\\'));
        return name.Length > 0 ? name : path;
    }
}
