using System.Globalization;
using System.IO.Compression;
using CopyMaster.Engine.Jobs;

namespace CopyMaster.Engine.Tests;

// The right-click Paste handler, driven through ShellHost as Explorer drives it. Every run goes through
// `ShellHost isolated`: a window station of its own, whose clipboard is its own, so no test touches the
// real clipboard (ShellHost refuses --paste anywhere else). ShellHost puts the files there as Explorer's
// Copy or Cut does, through OLE, which promises each format and produces it when asked; then
// Initialize, QueryContextMenu, InvokeCommand. `isolated` waits for everything started inside it, so the
// fake engine's received\ folder is complete when the run returns.
public sealed partial class ShellExtensionTests
{
    private const string PasteVerb = "CopyMaster.Paste";
    private const string PasteItem = "item 0 CopyMaster.Paste CopyMaster Paste";

    // ERROR_TIMEOUT: the clipboard's owner didn't produce the files in time.
    private const string TimedOut = "invoke 0x800705B4";
    // DV_E_FORMATETC: no files on the clipboard (any more).
    private const string NoFiles = "invoke 0x80040064";

    /// <summary>ShellHost's paste handler run: <paramref name="clip"/> holding <paramref name="files"/>.</summary>
    private static Run Paste(string command, string app, IEnumerable<string> targets, string clip, IEnumerable<string> files,
        IEnumerable<string>? options = null, string? engine = null)
    {
        var arguments = new List<string> { "isolated", command, "--paste", "--dll", Path.Combine(app, "CopyMasterShell.dll") };
        foreach (string target in targets) arguments.AddRange(["--target", target]);
        arguments.AddRange(["--clip", clip]);
        arguments.AddRange(options ?? []);
        arguments.AddRange(files);
        return RunHost(arguments, engine);
    }

    private static Run PasteOnBackground(string command, string app, string folder, string clip, IEnumerable<string> files,
        params string[] options) =>
        Paste(command, app, [folder], clip, files, ["--background", .. options]);

    /// <summary>A paste on <paramref name="folder"/>'s background whose job the real engine runs (cmcopy.exe).</summary>
    private static Run PasteThroughEngine(string app, string folder, string clip, IEnumerable<string> files, params string[] options) =>
        Paste("invoke", app, [folder], clip, files, ["--background", .. options, "--wait-engine", "--verb", PasteVerb], ShellBuild.Engine);

    private static void AssertNoJob(string app) => Assert.False(Directory.Exists(Path.Combine(app, "received")));

    private static long Took(Run run) => long.Parse(run.Line("took")!.Split(' ')[1], CultureInfo.InvariantCulture);

    [ShellFact]
    public void A_cut_pasted_on_a_folders_background_becomes_a_move_that_knows_the_clipboard()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        var files = Sources(scratch);
        string destination = scratch.Folder("to = here [1]");

        var run = PasteOnBackground("invoke", app, destination, "cut", files, "--verb", PasteVerb);

        Assert.Equal(PasteItem, Assert.Single(run.Items));
        Assert.Equal("invoke 0x00000000", run.Line("invoke"));
        var (job, name, commandLine) = Received(app);
        Assert.Equal(CopyOperation.Move, job.Operation);
        Assert.Equal(destination, job.Destination);
        Assert.Equal(files, job.Sources);

        // clipseq is the clipboard's number for exactly what was read, and reading it didn't change it.
        var (before, holds) = run.Clipboard[0];
        Assert.Equal("files", holds);
        Assert.Equal(before, run.Clipboard[1].Sequence);
        Assert.Equal(before, job.ClipboardSequence);

        // Started as "<the DLL's folder>\CopyMaster.exe" --job "<jobs folder>\<guid>.cmjob", as for a right-drag.
        var match = CommandLinePattern().Match(commandLine);
        Assert.True(match.Success, commandLine);
        Assert.Equal(Path.Combine(app, "CopyMaster.exe"), match.Groups["exe"].Value);
        Assert.Equal(Path.Combine(JobsFolder, name), match.Groups["job"].Value);
        Assert.False(File.Exists(Path.Combine(JobsFolder, name)));
    }

    [ShellFact]
    public void A_copy_pasted_on_a_folder_becomes_a_copy_without_a_clipboard_number()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        var files = Sources(scratch);
        string destination = scratch.Folder("to = here [1]");

        // A right-click on the folder itself: Explorer hands over the selection. The clipboard holds the
        // shell's own data object, as after Explorer's Copy.
        var run = Paste("invoke", app, [destination], "copy", files, ["--shell-data", "--verb", PasteVerb]);

        Assert.Equal(PasteItem, Assert.Single(run.Items));
        Assert.Equal("invoke 0x00000000", run.Line("invoke"));
        var job = Received(app).Job;
        Assert.Equal(CopyOperation.Copy, job.Operation);
        Assert.Equal(destination, job.Destination);
        Assert.Equal(files, job.Sources);
        Assert.Null(job.ClipboardSequence); // a copy leaves the clipboard alone
    }

    [ShellTheory]
    [InlineData("cut", "--shell-data", "", CopyOperation.Move)]     // the shell's own data object, by menu offset
    [InlineData("cut", "--by-verb", "", CopyOperation.Move)]
    [InlineData("copy", "--by-verb-ansi", "", CopyOperation.Copy)]
    [InlineData("cut", "--by-verb", "--background", CopyOperation.Move)]
    public void Each_way_of_asking_gives_the_right_paste(string clip, string how, string where, CopyOperation operation)
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        var files = Sources(scratch);
        string destination = scratch.Folder("to");
        string[] options = where.Length > 0 ? [how, where, "--verb", PasteVerb] : [how, "--verb", PasteVerb];

        var run = Paste("invoke", app, [destination], clip, files, options);

        Assert.Equal("invoke 0x00000000", run.Line("invoke"));
        var job = Received(app).Job;
        Assert.Equal(operation, job.Operation);
        Assert.Equal(destination, job.Destination);
        Assert.Equal(files, job.Sources);
        Assert.Equal(operation == CopyOperation.Move, job.ClipboardSequence is not null);
    }

    [ShellFact]
    public void Pasting_on_the_desktop_background_pastes_into_the_desktop_folder()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);

        // "desktop" is the namespace root; the fake engine copies nothing.
        var run = PasteOnBackground("invoke", app, "desktop", "copy", Sources(scratch), "--verb", PasteVerb);

        Assert.Equal(PasteItem, Assert.Single(run.Items));
        Assert.Equal("invoke 0x00000000", run.Line("invoke"));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Received(app).Job.Destination);
    }

    [ShellFact]
    public void A_drive_takes_a_paste()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        string root = Path.GetPathRoot(scratch.Root)!;

        var run = Paste("menu", app, [root], "copy", Sources(scratch));

        Assert.Equal($"target 0x70000000 {root}", run.Line("target"));
        Assert.Equal(PasteItem, Assert.Single(run.Items));
    }

    [ShellTheory]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", false)] // This PC
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", true)]
    [InlineData("::{645FF040-5081-101B-9F08-00AA002F954E}", false)] // Recycle Bin
    [InlineData("::{645FF040-5081-101B-9F08-00AA002F954E}", true)]
    [InlineData("::{26EE0668-A00A-44D7-9371-BEB064C98683}", true)]  // Control Panel
    [InlineData("shell:Libraries\\Documents.library-ms", false)]     // a library
    [InlineData("shell:Libraries\\Documents.library-ms", true)]
    [InlineData("zip", false)]                                       // a .zip, which shows as a folder
    [InlineData("zip", true)]
    public void Places_that_arent_folders_on_disk_get_no_paste(string target, bool background)
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        if (target == "zip")
        {
            string zip = scratch.At("archive.zip");
            ZipFile.CreateFromDirectory(scratch.Folder("zipped"), zip);
            target = zip;
        }

        var run = Paste("menu", app, [target], "copy", Sources(scratch), background ? ["--background"] : null);

        Assert.Equal("query 0x00000000", run.Line("query"));
        Assert.Empty(run.Items);
    }

    [ShellFact]
    public void Two_selected_folders_get_no_paste()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);

        var run = Paste("menu", app, [scratch.Folder("one"), scratch.Folder("two")], "copy", Sources(scratch));

        Assert.Equal("query 0x00000000", run.Line("query"));
        Assert.Empty(run.Items); // as Explorer's own Paste
    }

    [ShellTheory]
    [InlineData("text")]
    [InlineData("empty")]
    public void Without_files_on_the_clipboard_there_is_no_paste(string clip)
    {
        using var scratch = new TestFolder();
        string app = App(scratch);

        var run = PasteOnBackground("menu", app, scratch.Folder("to"), clip, []);

        Assert.Equal(clip, run.Clipboard[0].Holds);
        Assert.Equal("query 0x00000000", run.Line("query"));
        Assert.Empty(run.Items);
    }

    [ShellFact]
    public void A_hung_clipboard_owner_neither_hides_the_paste_nor_hangs_the_click()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        string destination = scratch.Folder("to");

        // Files promised by a window that never answers. Showing the menu reads only the list of formats.
        var menu = PasteOnBackground("menu", app, destination, "hung", []);
        Assert.Equal(PasteItem, Assert.Single(menu.Items));

        // The click gives up after three seconds rather than hang Explorer's window.
        var run = PasteOnBackground("invoke", app, destination, "hung", [], "--verb", PasteVerb);
        Assert.Equal(TimedOut, run.Line("invoke"));
        Assert.InRange(Took(run), 2900, 4500);
        AssertNoJob(app);
    }

    [ShellFact]
    public void If_the_clipboard_loses_its_files_before_the_click_nothing_is_pasted()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);

        var run = PasteOnBackground("invoke", app, scratch.Folder("to"), "copy", Sources(scratch),
            "--clip-before-invoke", "text", "--verb", PasteVerb);

        Assert.Equal(PasteItem, Assert.Single(run.Items)); // the menu saw files
        Assert.Equal(NoFiles, run.Line("invoke"));          // the click didn't
        AssertNoJob(app);
    }

    [ShellFact]
    public void Without_CopyMaster_exe_beside_it_a_paste_fails_and_leaves_no_job_file()
    {
        using var scratch = new TestFolder();
        string app = App(scratch, withEngine: false);
        var before = Directory.Exists(JobsFolder) ? Directory.GetFiles(JobsFolder).ToHashSet() : [];

        var run = PasteOnBackground("invoke", app, scratch.Folder("to"), "cut", Sources(scratch), "--verb", PasteVerb);

        Assert.Equal("invoke 0x80070002", run.Line("invoke"));
        var after = Directory.Exists(JobsFolder) ? Directory.GetFiles(JobsFolder).ToHashSet() : [];
        Assert.Empty(after.Except(before));
        AssertNoJob(app);
    }

    [ShellFact]
    public void ShellHost_wont_put_anything_on_the_real_clipboard()
    {
        using var scratch = new TestFolder();

        // Without `isolated`: the window station everyone shares. The DLL isn't there, so even if the
        // guard failed, ShellHost would stop at loading it, before putting anything on the clipboard.
        var run = RunHost(["menu", "--paste", "--dll", scratch.At("not here.dll"), "--target", scratch.Folder("to"),
            "--background", "--clip", "text"]);

        Assert.Equal(2, run.ExitCode);
        Assert.StartsWith("error --paste changes the clipboard", run.Lines.Single());
    }

    // ---- End to end: the DLL, the job file and the real engine (cmcopy.exe) -------------------------

    [ShellEngineFact]
    public void A_cut_pasted_through_the_real_engine_moves_the_files_and_empties_the_clipboard()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        var files = Sources(scratch);
        string destination = scratch.Folder("to");

        var run = PasteThroughEngine(app, destination, "cut", files);

        Assert.Equal("invoke 0x00000000", run.Line("invoke"));
        Assert.Equal("engine 0", run.Line("engine"));
        foreach (string file in files)
        {
            Assert.False(Path.Exists(file));
            Assert.True(Path.Exists(Path.Combine(destination, Path.GetFileName(file))));
        }
        var (cut, _) = run.Clipboard[0];
        var (last, holds) = run.Clipboard[^1];
        Assert.Equal("empty", holds); // as Explorer leaves it after pasting a cut
        Assert.True(last > cut);
    }

    [ShellEngineFact]
    public void A_cut_whose_clipboard_has_changed_since_leaves_the_new_contents_alone()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        var files = Sources(scratch);
        string destination = scratch.Folder("to");

        // Some text is copied after the click, before the engine runs.
        var run = PasteThroughEngine(app, destination, "cut", files, "--clip-after-invoke", "text");

        Assert.Equal("engine 0", run.Line("engine"));
        Assert.All(files, file => Assert.True(Path.Exists(Path.Combine(destination, Path.GetFileName(file)))));
        var clipboard = run.Clipboard;
        Assert.Equal("text", clipboard[^2].Holds); // just after the text was copied
        Assert.Equal(clipboard[^2], clipboard[^1]); // and after the engine: untouched
    }

    [ShellEngineFact]
    public void A_copy_pasted_through_the_real_engine_leaves_the_clipboard_as_it_was()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        var files = Sources(scratch);
        string destination = scratch.Folder("to");

        var run = PasteThroughEngine(app, destination, "copy", files);

        Assert.Equal("engine 0", run.Line("engine"));
        Assert.All(files, file => Assert.True(Path.Exists(file) && Path.Exists(Path.Combine(destination, Path.GetFileName(file)))));
        Assert.Equal(run.Clipboard[0], run.Clipboard[^1]); // still the copy, ready to paste again
    }
}
