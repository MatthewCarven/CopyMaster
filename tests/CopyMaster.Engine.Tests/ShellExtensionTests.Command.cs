using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CopyMaster.Engine.Jobs;

namespace CopyMaster.Engine.Tests;

// Windows 11's compact-menu Paste (PasteCommand, an IExplorerCommand), driven through ShellHost as Explorer
// drives it: SetSite (a folder view), GetState, GetTitle, GetIcon, then Invoke. As with the classic Paste,
// every run is on a private window station (`ShellHost isolated`), so no test touches the real clipboard.
// The compact menu has no "no UI" flag, so ShellHost reads any message box and presses OK; its text is a
// "dialog" line. Also here: the package manifest that registers the command, and the icon.
public sealed partial class ShellExtensionTests
{
    private const string CommandClsid = "373E9CED-1AAA-4486-9EC6-DB3A03E8E315";
    private const string Enabled = "state 0x00000000 enabled";
    private const string Hidden = "state 0x00000000 hidden";

    /// <summary>ShellHost's compact-menu run: <paramref name="clip"/> holding <paramref name="files"/>.</summary>
    private static Run Command(string command, string app, IEnumerable<string> targets, string clip, IEnumerable<string> files,
        IEnumerable<string>? options = null, string? engine = null)
    {
        var arguments = new List<string> { "isolated", command, "--command", "--dll", Path.Combine(app, "CopyMasterShell.dll") };
        foreach (string target in targets) arguments.AddRange(["--target", target]);
        arguments.AddRange(["--clip", clip]);
        arguments.AddRange(options ?? []);
        arguments.AddRange(files);
        return RunHost(arguments, engine);
    }

    private static Run CommandOnBackground(string command, string app, string folder, string clip, IEnumerable<string> files,
        params string[] options) =>
        Command(command, app, [folder], clip, files, ["--background", .. options]);

    [ShellFact]
    public void The_compact_menus_paste_on_a_folder_pastes_a_cut_into_it_as_a_move()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        var files = Sources(scratch);
        string destination = scratch.Folder("to = here [1]");

        var run = Command("invoke", app, [destination], "cut", files);

        Assert.Equal(Enabled, run.Line("state"));
        Assert.Equal("title CopyMaster Paste", run.Line("title"));
        Assert.Equal("invoke 0x00000000", run.Line("invoke"));
        Assert.Null(run.Line("dialog"));
        var (job, name, commandLine) = Received(app);
        Assert.Equal(CopyOperation.Move, job.Operation);
        Assert.Equal(destination, job.Destination);
        Assert.Equal(files, job.Sources);
        var (before, holds) = run.Clipboard[0];
        Assert.Equal("files", holds);
        Assert.Equal(before, job.ClipboardSequence); // the number for exactly what was read
        Assert.Equal(before, run.Clipboard[1].Sequence);

        var match = CommandLinePattern().Match(commandLine);
        Assert.True(match.Success, commandLine);
        Assert.Equal(Path.Combine(app, "CopyMaster.exe"), match.Groups["exe"].Value);
        Assert.Equal(Path.Combine(JobsFolder, name), match.Groups["job"].Value);
    }

    [ShellTheory]
    [InlineData()]                                  // no selection: the site's view says where
    [InlineData("--empty-items")]                   // an empty one, as Explorer sometimes gives
    [InlineData("--site-shellfolder")]              // a view that gives only the folder's IShellFolder
    [InlineData("--empty-items", "--site-shellfolder")]
    public void On_a_background_it_pastes_into_the_folder_the_view_shows(params string[] how)
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        var files = Sources(scratch);
        string destination = scratch.Folder("to");

        var run = CommandOnBackground("invoke", app, destination, "copy", files, how);

        Assert.Equal(Enabled, run.Line("state"));
        Assert.Equal("invoke 0x00000000", run.Line("invoke"));
        var job = Received(app).Job;
        Assert.Equal(CopyOperation.Copy, job.Operation);
        Assert.Equal(destination, job.Destination);
        Assert.Equal(files, job.Sources);
        Assert.Null(job.ClipboardSequence); // a copy leaves the clipboard alone
    }

    [ShellFact]
    public void The_folder_right_clicked_wins_over_the_folder_the_view_shows()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        string folder = scratch.Folder("view", "clicked");

        var run = Command("invoke", app, [folder], "copy", Sources(scratch), ["--site", scratch.Folder("view"), "--shell-data"]);

        Assert.Equal("invoke 0x00000000", run.Line("invoke"));
        Assert.Equal(folder, Received(app).Job.Destination);
    }

    [ShellFact]
    public void On_the_desktop_background_it_pastes_into_the_desktop_folder()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);

        // "desktop" is the namespace root; the fake engine copies nothing.
        var run = CommandOnBackground("invoke", app, "desktop", "copy", Sources(scratch));

        Assert.Equal(Enabled, run.Line("state"));
        Assert.Equal("invoke 0x00000000", run.Line("invoke"));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Received(app).Job.Destination);
    }

    [ShellFact]
    public void A_drive_gets_the_compact_paste()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        string root = Path.GetPathRoot(scratch.Root)!;

        var run = Command("menu", app, [root], "copy", Sources(scratch));

        Assert.Equal($"target 0x70000000 {root}", run.Line("target"));
        Assert.Equal(Enabled, run.Line("state"));
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
    public void Places_that_arent_folders_on_disk_get_no_compact_paste(string target, bool background)
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        if (target == "zip")
        {
            string zip = scratch.At("archive.zip");
            ZipFile.CreateFromDirectory(scratch.Folder("zipped"), zip);
            target = zip;
        }

        var run = Command("menu", app, [target], "copy", Sources(scratch), background ? ["--background"] : null);

        Assert.Equal(Hidden, run.Line("state"));
    }

    [ShellFact]
    public void Two_selected_folders_get_no_compact_paste()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);

        var run = Command("menu", app, [scratch.Folder("one"), scratch.Folder("two")], "copy", Sources(scratch));

        Assert.Equal(Hidden, run.Line("state")); // as Explorer's own Paste
    }

    [ShellTheory]
    [InlineData("text")]
    [InlineData("empty")]
    public void Without_files_on_the_clipboard_the_compact_paste_hides(string clip)
    {
        using var scratch = new TestFolder();
        string app = App(scratch);

        var run = CommandOnBackground("menu", app, scratch.Folder("to"), clip, []);

        Assert.Equal(clip, run.Clipboard[0].Holds);
        Assert.Equal(Hidden, run.Line("state"));
    }

    [ShellFact]
    public void A_hung_clipboard_owner_neither_hides_the_compact_paste_nor_hangs_it()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        string destination = scratch.Folder("to");

        // Showing the menu reads only the list of formats.
        Assert.Equal(Enabled, CommandOnBackground("menu", app, destination, "hung", []).Line("state"));

        // The click gives up after three seconds, and says so.
        var run = CommandOnBackground("invoke", app, destination, "hung", []);
        Assert.Equal(TimedOut, run.Line("invoke"));
        Assert.InRange(Took(run), 2900, 4500);
        Assert.StartsWith("dialog owned The clipboard didn't answer", run.Line("dialog"));
        AssertNoJob(app);
    }

    [ShellFact]
    public void If_the_clipboard_loses_its_files_before_the_click_the_compact_paste_says_so()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);

        var run = CommandOnBackground("invoke", app, scratch.Folder("to"), "copy", Sources(scratch), "--clip-before-invoke", "text");

        Assert.Equal(Enabled, run.Line("state")); // the menu saw files
        Assert.Equal(NoFiles, run.Line("invoke")); // the click didn't
        // Owned by the window Explorer's site gives.
        Assert.Equal("dialog owned There are no files on the clipboard to paste. |  | Copy or cut some in Explorer first.",
            run.Line("dialog"));
        AssertNoJob(app);
    }

    [ShellFact]
    public void Without_CopyMaster_exe_the_compact_paste_says_where_it_looked_and_leaves_no_job_file()
    {
        using var scratch = new TestFolder();
        string app = App(scratch, withEngine: false);
        var before = Directory.Exists(JobsFolder) ? Directory.GetFiles(JobsFolder).ToHashSet() : [];

        var run = CommandOnBackground("invoke", app, scratch.Folder("to"), "cut", Sources(scratch));

        Assert.Equal("invoke 0x80070002", run.Line("invoke"));
        Assert.Equal($"dialog owned CopyMaster.exe isn't where it should be: | {Path.Combine(app, "CopyMaster.exe")} |  | " +
                     "CopyMaster's Explorer extension starts the CopyMaster.exe in its own folder.", run.Line("dialog"));
        var after = Directory.Exists(JobsFolder) ? Directory.GetFiles(JobsFolder).ToHashSet() : [];
        Assert.Empty(after.Except(before));
        AssertNoJob(app);
    }

    [ShellFact]
    public void The_compact_paste_names_itself_and_its_icon_in_the_DLL()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);

        var run = CommandOnBackground("menu", app, scratch.Folder("to"), "copy", Sources(scratch));

        Assert.Equal($"icon {Path.Combine(app, "CopyMasterShell.dll")},-101", run.Line("icon"));
        Assert.Equal("icons 1", run.Line("icons"));
        Assert.Equal($"canonical {{{CommandClsid}}}", run.Line("canonical"));
        Assert.Equal("flags 0x00000000", run.Line("flags")); // ECF_DEFAULT: no submenu, no separators
    }

    [ShellFact]
    public void The_classic_menus_items_show_the_icon()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        string destination = scratch.Folder("to");

        var paste = PasteOnBackground("menu", app, destination, "copy", Sources(scratch));
        var drag = Host("menu", app, destination, Sources(scratch));

        // A 32-bit picture (alpha) at the small-icon size, beside each item.
        Assert.Matches(@"^bitmap 0 (\d+)x\1 32$", paste.Line("bitmap"));
        Assert.Equal(2, drag.Lines.Count(l => Regex.IsMatch(l, @"^bitmap [01] (\d+)x\1 32$")));
    }

    [ShellEngineFact]
    public void A_cut_pasted_from_the_compact_menu_through_the_real_engine_moves_the_files_and_empties_the_clipboard()
    {
        using var scratch = new TestFolder();
        string app = App(scratch);
        var files = Sources(scratch);
        string destination = scratch.Folder("to");

        var run = Command("invoke", app, [destination], "cut", files, ["--background", "--wait-engine"], ShellBuild.Engine);

        Assert.Equal("invoke 0x00000000", run.Line("invoke"));
        Assert.Equal("engine 0", run.Line("engine"));
        foreach (string file in files)
        {
            Assert.False(Path.Exists(file));
            Assert.True(Path.Exists(Path.Combine(destination, Path.GetFileName(file))));
        }
        Assert.Equal("empty", run.Clipboard[^1].Holds); // as Explorer leaves it after pasting a cut
    }

    // ---- The package that puts the command in the compact menu ------------------------------------------

    private static string PackageFolder => Path.Combine(ShellBuild.Root!, "package");

    [Fact]
    public void The_package_manifest_registers_the_commands_CLSID_for_folders_backgrounds_and_drives()
    {
        Assert.NotNull(ShellBuild.Root);
        string clsids = File.ReadAllText(Path.Combine(ShellBuild.Root!, "src", "CopyMasterShell", "Clsids.h"));
        string clsid = Regex.Match(clsids, "#define COPYMASTER_PASTE_COMMAND_CLSID \"([0-9A-F-]{36})\"").Groups[1].Value;
        Assert.Equal(CommandClsid, clsid);

        var manifest = XDocument.Load(Path.Combine(PackageFolder, "AppxManifest.xml"));
        var all = manifest.Descendants().ToList();
        var verbs = all.Where(e => e.Name.LocalName == "Verb").ToList();
        Assert.Equal(["Directory", "Directory\\Background", "Drive"], verbs.Select(v => (string)v.Parent!.Attribute("Type")!));
        Assert.All(verbs, v => Assert.Equal(clsid, (string?)v.Attribute("Clsid")));

        var server = Assert.Single(all, e => e.Name.LocalName == "Class");
        Assert.Equal(clsid, (string?)server.Attribute("Id"));
        Assert.Equal("CopyMasterShell.dll", (string?)server.Attribute("Path")); // in the external location
        Assert.Equal("SurrogateServer", server.Parent!.Name.LocalName);

        var application = Assert.Single(all, e => e.Name.LocalName == "Application");
        Assert.Equal("CopyMaster.exe", (string?)application.Attribute("Executable"));
        Assert.Equal("none", (string?)all.Single(e => e.Name.LocalName == "VisualElements").Attribute("AppListEntry"));

        // Unsigned: Windows 11 installs it with -AllowUnsigned only if the Publisher carries this OID.
        var identity = all.Single(e => e.Name.LocalName == "Identity");
        Assert.EndsWith(", OID.2.25.311729368913984317654407730594956997722=1", (string?)identity.Attribute("Publisher"));

        // Every logo it names is in the package.
        foreach (string logo in all.SelectMany(e => e.Attributes()).Where(a => a.Name.LocalName.EndsWith("Logo", StringComparison.Ordinal))
                     .Select(a => a.Value).Concat(all.Where(e => e.Name.LocalName == "Logo").Select(e => e.Value)))
        {
            Assert.True(File.Exists(Path.Combine(PackageFolder, logo)), logo);
        }
    }

    [Fact]
    public void The_package_manifest_passes_Windows_schema_check()
    {
        string? makeappx = FindMakeAppx();
        if (makeappx is null) return; // no Windows SDK: nothing to check with
        using var scratch = new TestFolder();
        var start = new ProcessStartInfo(makeappx)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // /nv: the DLL and the exe it names are in the external location, not the package.
        foreach (string arg in new[] { "pack", "/d", PackageFolder, "/p", scratch.At("CopyMaster.msix"), "/nv", "/o" })
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, output);
        Assert.True(File.Exists(scratch.At("CopyMaster.msix")));
    }

    private static string? FindMakeAppx()
    {
        string kits = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "bin");
        if (!Directory.Exists(kits)) return null;
        return Directory.GetDirectories(kits)
            .Where(d => Version.TryParse(Path.GetFileName(d), out _))
            .OrderByDescending(d => Version.Parse(Path.GetFileName(d)))
            .Select(d => Path.Combine(d, "x64", "makeappx.exe"))
            .FirstOrDefault(File.Exists);
    }

    // ---- The icon --------------------------------------------------------------------------------------

    [ShellFact]
    public void The_DLL_and_the_exes_carry_CopyMasters_icon()
    {
        Assert.True(IconCount(ShellBuild.Dll!) >= 1);
        if (ShellBuild.Engine is string cmcopy) Assert.True(IconCount(cmcopy) >= 1);
        string window = Path.Combine(ShellBuild.Root!, "src", "CopyMaster", "bin", ShellBuild.Configuration, "net10.0-windows", "CopyMaster.exe");
        if (File.Exists(window)) Assert.True(IconCount(window) >= 1);
        Assert.True(File.Exists(Path.Combine(ShellBuild.Root!, "assets", "CopyMaster.ico")));
    }

    private static uint IconCount(string file) => ExtractIconExW(file, -1, IntPtr.Zero, IntPtr.Zero, 0);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ExtractIconExW(string file, int index, IntPtr large, IntPtr small, uint count);
}
