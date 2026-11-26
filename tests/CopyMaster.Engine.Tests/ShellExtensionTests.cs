using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using CopyMaster.Engine.Jobs;

namespace CopyMaster.Engine.Tests;

// The shell DLL (src/CopyMasterShell) driven by tools\ShellHost the way Explorer drives it: load,
// Initialize with a target folder and a data object, QueryContextMenu, InvokeCommand. Each test lays
// out the deployed folder in scratch, with ShellHost copied in as CopyMaster.exe: started by the DLL,
// it moves the job file into received\ instead of copying anything. Skipped until .\build.ps1 has
// built the DLL and ShellHost (dotnet test doesn't build C++, so rebuild after changing the DLL).

internal static class ShellBuild
{
    private const string Configuration =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    public static readonly string? Root = FindRoot();

    public static string? Dll => Built(Path.Combine("src", "CopyMasterShell", "bin", "x64", Configuration, "CopyMasterShell.dll"));

    public static string? Host => Built(Path.Combine("tools", "ShellHost", "bin", "x64", Configuration, "ShellHost.exe"));

    /// <summary>cmcopy.exe, the real engine for the end-to-end paste tests (the tests project builds it).</summary>
    public static string? Engine => Built(Path.Combine("src", "CopyMaster.Cli", "bin", Configuration, "net10.0-windows", "cmcopy.exe"));

    public static string? Missing => Dll is null || Host is null
        ? $"CopyMasterShell.dll or ShellHost.exe ({Configuration}) isn't built: run .\\build.ps1."
        : null;

    private static string? Built(string relative)
    {
        if (Root is null) return null;
        string path = Path.Combine(Root, relative);
        return File.Exists(path) ? path : null;
    }

    private static string? FindRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "CopyMaster.slnx"))) return folder.FullName;
        }
        return null;
    }
}

public sealed class ShellFactAttribute : FactAttribute
{
    public ShellFactAttribute()
    {
        if (ShellBuild.Missing is string reason) Skip = reason;
    }
}

/// <summary>A shell test that also runs the real engine (cmcopy.exe).</summary>
public sealed class ShellEngineFactAttribute : FactAttribute
{
    public ShellEngineFactAttribute()
    {
        if (ShellBuild.Missing is string reason) Skip = reason;
        else if (ShellBuild.Engine is null) Skip = "cmcopy.exe isn't built: run .\\build.ps1 (or dotnet build src\\CopyMaster.Cli).";
    }
}

public sealed class ShellTheoryAttribute : TheoryAttribute
{
    public ShellTheoryAttribute()
    {
        if (ShellBuild.Missing is string reason) Skip = reason;
    }
}

// One at a time: the no-engine test checks the real jobs folder is left as it was.
[CollectionDefinition("Shell", DisableParallelization = true)]
public sealed class ShellCollection;

[Collection("Shell")]
public sealed partial class ShellExtensionTests
{
    private const string CopyVerb = "CopyMaster.CopyHere";
    private const string MoveVerb = "CopyMaster.MoveHere";

    private static readonly string JobsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CopyMaster", "jobs");

    /// <summary>The deployed layout: the DLL and CopyMaster.exe in one folder (with '=', spaces and a comma).</summary>
    private static string App(TestFolder scratch, bool withEngine = true)
    {
        string app = scratch.Folder("app = one, two");
        File.Copy(ShellBuild.Dll!, Path.Combine(app, "CopyMasterShell.dll"));
        if (withEngine) File.Copy(ShellBuild.Host!, Path.Combine(app, "CopyMaster.exe"));
        return app;
    }

    /// <summary>Awkward names: '=', spaces, punctuation, accents, CJK, a folder.</summary>
    private static List<string> Sources(TestFolder scratch) =>
    [
        scratch.File(@"from\a=b c.txt", "a"),
        scratch.File(@"from\naïve ✓ 日本 #%&;,'!.txt", "b"),
        scratch.File(@"from\x  y=.txt", "c"),
        scratch.Folder("from", "Some = folder"),
    ];

    private sealed record Run(int ExitCode, IReadOnlyList<string> Lines)
    {
        public IEnumerable<string> Items => Lines.Where(l => l.StartsWith("item ", StringComparison.Ordinal));

        public string? Line(string prefix) => Lines.FirstOrDefault(l => l.StartsWith(prefix + " ", StringComparison.Ordinal));

        /// <summary>Each "clipboard &lt;sequence number&gt; &lt;files|text|empty|other&gt;" line, in order.</summary>
        public IReadOnlyList<(uint Sequence, string Holds)> Clipboard => Lines
            .Where(l => l.StartsWith("clipboard ", StringComparison.Ordinal))
            .Select(l => l.Split(' '))
            .Select(p => (uint.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), p[2]))
            .ToList();
    }

    private static Run Host(string command, string app, string target, IEnumerable<string> sources, params string[] options) =>
        RunHost([command, "--dll", Path.Combine(app, "CopyMasterShell.dll"), "--target", target, .. options, .. sources]);

    private static Run RunHost(IEnumerable<string> arguments, string? engine = null)
    {
        var start = new ProcessStartInfo(ShellBuild.Host!)
        {
            UseShellExecute = false,
            CreateNoWindow = true, // the fake engine shares this hidden console, so no window flashes
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string arg in arguments) start.ArgumentList.Add(arg);
        if (engine is not null) start.Environment["CM_SHELLHOST_ENGINE"] = engine;
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill();
            Assert.Fail("ShellHost didn't finish in 30 s.");
        }
        return new Run(process.ExitCode, output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList());
    }

    /// <summary>The job file the DLL handed to "CopyMaster.exe", and the command line it was started with.</summary>
    private static (JobSpec Job, string Name, string CommandLine) Received(string app)
    {
        string folder = Path.Combine(app, "received");
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            string? job = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.cmjob").SingleOrDefault() : null;
            if (job is not null)
            {
                return (JobFile.Read(job), Path.GetFileName(job), File.ReadAllText(job + ".cmdline", Encoding.UTF8));
            }
            Thread.Sleep(50);
        }
        Assert.Fail("CopyMaster.exe never got a job.");
        return default;
    }

    private static void AssertBothItems(Run run)
    {
        Assert.Equal(new[] { "item 0 CopyMaster.CopyHere CopyMaster Copy here", "item 1 CopyMaster.MoveHere CopyMaster Move here" }, run.Items);
    }

    [GeneratedRegex("""^"(?<exe>[^"]+)" --job "(?<job>[^"]+)"$""")]
    private static partial Regex CommandLinePattern();

    [ShellFact]
    public void A_copy_dropped_on_a_folder_reaches_CopyMaster_as_a_job_file()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        var sources = Sources(scratch);
        string destination = scratch.Folder("to = here [1]");

        var run = Host("invoke", app, destination, sources, "--verb", CopyVerb);

        AssertBothItems(run);
        Assert.Equal("invoke 0x00000000", run.Line("invoke"));
        Assert.Equal(0, run.ExitCode);
        var (job, name, commandLine) = Received(app);
        Assert.Equal(CopyOperation.Copy, job.Operation);
        Assert.Equal(destination, job.Destination);
        Assert.Equal(sources, job.Sources);
        Assert.Null(job.ClipboardSequence);

        // Started as "<the DLL's folder>\CopyMaster.exe" --job "<jobs folder>\<guid>.cmjob"
        var match = CommandLinePattern().Match(commandLine);
        Assert.True(match.Success, commandLine);
        Assert.Equal(Path.Combine(app, "CopyMaster.exe"), match.Groups["exe"].Value);
        Assert.Equal(Path.Combine(JobsFolder, name), match.Groups["job"].Value);
        Assert.Matches("^[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\\.cmjob$", name);
        Assert.False(File.Exists(Path.Combine(JobsFolder, name))); // taken, as CopyMaster.exe does
    }

    [ShellTheory]
    [InlineData(CopyVerb, "--by-verb", CopyOperation.Copy)]
    [InlineData(MoveVerb, "--by-verb", CopyOperation.Move)]
    [InlineData(MoveVerb, "--by-verb-ansi", CopyOperation.Move)]
    [InlineData(MoveVerb, "--shell-data", CopyOperation.Move)] // by offset, with the shell's own data object
    public void Each_way_of_asking_gives_the_right_job(string verb, string how, CopyOperation operation)
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        var sources = Sources(scratch);
        string destination = scratch.Folder("to = here [1]");

        var run = Host("invoke", app, destination, sources, "--verb", verb, how);

        Assert.Equal("invoke 0x00000000", run.Line("invoke"));
        var (job, _, _) = Received(app);
        Assert.Equal(operation, job.Operation);
        Assert.Equal(destination, job.Destination);
        Assert.Equal(sources, job.Sources);
    }

    [ShellFact]
    public void Long_paths_go_through_whole()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        string deep = Path.Combine(Enumerable.Repeat("a folder with a long name = 40 chars xx", 8).ToArray());
        string source = scratch.File(Path.Combine(deep, "file.txt"), "deep");
        Assert.True(source.Length > 300);
        string destination = scratch.Folder("to");

        var run = Host("invoke", app, destination, [source], "--verb", CopyVerb);

        Assert.Equal("invoke 0x00000000", run.Line("invoke"));
        Assert.Equal(new[] { source }, Received(app).Job.Sources);
    }

    [ShellFact]
    public void Dropping_on_the_desktop_copies_to_the_desktop_folder()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        var sources = Sources(scratch);

        // "desktop" is the namespace root, the target Explorer's desktop window has.
        var run = Host("invoke", app, "desktop", sources, "--verb", CopyVerb);

        AssertBothItems(run);
        Assert.Equal("invoke 0x00000000", run.Line("invoke"));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Received(app).Job.Destination);
    }

    [ShellFact]
    public void A_drive_root_is_a_target()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        string root = Path.GetPathRoot(scratch.Root)!;

        var run = Host("menu", app, root, Sources(scratch));

        AssertBothItems(run);
        Assert.Equal($"target 0x70000000 {root}", run.Line("target")); // file system, folder, has file-system children
    }

    [ShellTheory]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")] // This PC
    [InlineData("::{645FF040-5081-101B-9F08-00AA002F954E}")] // Recycle Bin
    [InlineData("::{26EE0668-A00A-44D7-9371-BEB064C98683}")] // Control Panel
    [InlineData("shell:Libraries\\Documents.library-ms")]     // a library
    [InlineData("zip")]                                       // a .zip, which shows as a folder
    public void Places_that_arent_folders_on_disk_get_no_items(string target)
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        if (target == "zip")
        {
            string zip = scratch.At("archive.zip");
            ZipFile.CreateFromDirectory(scratch.Folder("zipped"), zip);
            target = zip;
        }

        var run = Host("menu", app, target, Sources(scratch));

        Assert.Equal("query 0x00000000", run.Line("query"));
        Assert.Empty(run.Items);
    }

    [ShellTheory]
    [InlineData("")]
    [InlineData("--shell-data")] // the shell's own data object, as from an Explorer drag
    [InlineData("upper case")]   // NTFS names compare without case
    public void A_drop_into_the_folder_the_items_are_in_gets_no_items(string how)
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        var sources = Sources(scratch);
        if (how == "upper case") sources = sources.Select(s => s.ToUpperInvariant()).ToList();

        var run = Host("menu", app, scratch.At("from"), sources, how == "--shell-data" ? new[] { "--shell-data" } : Array.Empty<string>());

        Assert.Equal("query 0x00000000", run.Line("query"));
        Assert.Empty(run.Items);
    }

    [ShellFact]
    public void A_drop_at_the_top_of_a_drive_from_the_top_of_that_drive_gets_no_items()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        string root = Path.GetPathRoot(scratch.Root)!; // "C:\"; the DLL only compares the paths, so nothing is made there

        var run = Host("menu", app, root, [Path.Combine(root, "not-there-copymaster-test.txt")]);

        Assert.Empty(run.Items);
    }

    [ShellFact]
    public void A_drag_from_two_folders_onto_one_of_them_gets_the_items()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        string destination = scratch.Folder("to");
        string[] sources = [scratch.File(@"to\here.txt", "h"), scratch.File(@"from\there.txt", "t")];

        var run = Host("invoke", app, destination, sources, "--verb", CopyVerb);

        AssertBothItems(run);
        Assert.Equal("invoke 0x00000000", run.Line("invoke"));
        Assert.Equal(sources, Received(app).Job.Sources); // the engine notes the one already there
    }

    [ShellFact]
    public void A_drag_without_file_paths_gets_no_items()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);

        var run = Host("menu", app, scratch.Folder("to"), Sources(scratch), "--no-hdrop");

        Assert.Equal("query 0x00000000", run.Line("query"));
        Assert.Empty(run.Items);
    }

    [ShellFact]
    public void Without_CopyMaster_exe_beside_it_the_click_fails_and_leaves_no_job_file()
    {
        using var scratch = new TestFolder();
        string app = App(scratch, withEngine: false);
        var before = Directory.Exists(JobsFolder) ? Directory.GetFiles(JobsFolder).ToHashSet() : [];

        var run = Host("invoke", app, scratch.Folder("to"), Sources(scratch), "--verb", CopyVerb);

        Assert.Equal("invoke 0x80070002", run.Line("invoke")); // file not found (no message box: ShellHost asks for none)
        Assert.Equal(1, run.ExitCode);
        var after = Directory.Exists(JobsFolder) ? Directory.GetFiles(JobsFolder).ToHashSet() : [];
        Assert.Empty(after.Except(before));
        Assert.False(Directory.Exists(Path.Combine(app, "received")));
    }
}
