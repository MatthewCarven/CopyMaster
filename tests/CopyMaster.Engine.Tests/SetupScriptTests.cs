using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CopyMaster.Engine.Tests;

// The installer, uninstaller and check (setup\*.ps1, setup\CopyMasterSetup.psm1), run as Matthew runs them but only
// in their -FilesOnly and -WhatIf modes, on scratch folders: nothing here registers anything, touches the package or
// HKLM, or leaves a delete waiting for the next restart (CM_TEST_NO_RESTART_DELETE=1 makes the scripts only say so).
// The real install and uninstall are tried by hand (TODO.md, phase 5b). Skipped until .\build.ps1 has built
// CopyMaster.exe and the DLL in this configuration.

internal static class Setup
{
    public static string Folder => Path.Combine(ShellBuild.Root!, "setup");

    public static string ExeBuild =>
        Path.Combine(ShellBuild.Root!, "src", "CopyMaster", "bin", ShellBuild.Configuration, "net10.0-windows");

    public static string? Missing =>
        ShellBuild.Root is null ? "The repo folder wasn't found."
        : !File.Exists(Path.Combine(ExeBuild, "CopyMaster.exe")) || ShellBuild.Dll is null
            ? $"CopyMaster.exe or CopyMasterShell.dll ({ShellBuild.Configuration}) isn't built: run .\\build.ps1."
            : null;

    /// <summary>Windows PowerShell 5.1 (always there) or PowerShell 7 (if installed).</summary>
    public static string? Shell(string name)
    {
        if (name == "powershell") return Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (File.Exists(installed)) return installed;
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')
            .Select(folder => Path.Combine(folder.Trim(), "pwsh.exe"))
            .FirstOrDefault(File.Exists);
    }

    public sealed record Result(int ExitCode, string Output);

    public static Result Script(string shell, string script, params string[] args) =>
        Start(shell, ["-File", Path.Combine(Folder, script), .. args]);

    public static Result Command(string shell, string command) => Start(shell, ["-Command", command]);

    private static Result Start(string shell, string[] args)
    {
        var start = new ProcessStartInfo(Shell(shell) ?? throw new InvalidOperationException($"{shell} isn't installed."))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = ShellBuild.Root!,
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass" }.Concat(args))
        {
            start.ArgumentList.Add(arg);
        }
        start.Environment["CM_TEST_NO_RESTART_DELETE"] = "1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{shell} {string.Join(' ', args)} took over 2 minutes.");
        }
        return new Result(process.ExitCode, output.Result + errors.Result);
    }

    public static Result Install(string shell, string folder, params string[] more) =>
        Script(shell, "install.ps1", ["-NoBuild", "-FilesOnly", "-Configuration", ShellBuild.Configuration, "-Path", folder, .. more]);

    public static Result Uninstall(string shell, string folder, params string[] more) =>
        Script(shell, "uninstall.ps1", ["-FilesOnly", "-Path", folder, .. more]);
}

public sealed class SetupFactAttribute : FactAttribute
{
    public SetupFactAttribute()
    {
        if (Setup.Missing is string reason) Skip = reason;
    }
}

public sealed class SetupTheoryAttribute : TheoryAttribute
{
    public SetupTheoryAttribute()
    {
        if (Setup.Missing is string reason) Skip = reason;
    }
}

// One at a time: each starts PowerShell (5.1 and 7), and one loads the DLL into this process.
[CollectionDefinition("Setup", DisableParallelization = true)]
public sealed class SetupCollection;

[Collection("Setup")]
public sealed class SetupScriptTests
{
    private static string Module => File.ReadAllText(Path.Combine(Setup.Folder, "CopyMasterSetup.psm1"));

    // ---- What the module knows agrees with the DLL and the package ------------------------------------------------

    [Fact]
    public void The_module_names_the_same_CLSIDs_and_keys_as_the_DLLs_registration()
    {
        Assert.NotNull(ShellBuild.Root);
        string shell = Path.Combine(ShellBuild.Root!, "src", "CopyMasterShell");
        string clsids = File.ReadAllText(Path.Combine(shell, "Clsids.h"));
        string registration = File.ReadAllText(Path.Combine(shell, "Registration.cpp"));
        string module = Module;

        foreach (string name in new[] { "DRAGDROP", "PASTE", "PASTE_COMMAND" })
        {
            string clsid = Regex.Match(clsids, $"#define COPYMASTER_{name}_CLSID \"([0-9A-F-]{{36}})\"").Groups[1].Value;
            Assert.Matches("^[0-9A-F-]{36}$", clsid);
            Assert.Contains($"'{{{clsid}}}'", module);
        }

        foreach ((string cpp, string ps) in new[] { ("kDragDropKeys", "DragDropKeys"), ("kPasteKeys", "ClassicPasteKeys") })
        {
            string block = Regex.Match(registration, cpp + @"\[\] = \{(.*?)\};", RegexOptions.Singleline).Groups[1].Value;
            string[] expected = Regex.Matches(block, "L\"(.*?)\"").Select(m => m.Groups[1].Value.Replace(@"\\", @"\")).ToArray();
            string list = Regex.Match(module, @"\$script:" + ps + @" = @\((.*?)\)", RegexOptions.Singleline).Groups[1].Value;
            string[] actual = Regex.Matches(list, "'(.*?)'").Select(m => m.Groups[1].Value).ToArray();
            Assert.Equal(3, expected.Length);
            Assert.Equal(expected, actual);
        }
        Assert.Contains("Shell Extensions\\\\Approved", registration);
        Assert.Contains(@"'Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved'", module);
    }

    [Fact]
    public void The_package_family_name_is_the_one_Windows_derives_from_the_manifests_publisher()
    {
        var manifest = XDocument.Load(Path.Combine(ShellBuild.Root!, "package", "AppxManifest.xml"));
        var identity = manifest.Descendants().Single(e => e.Name.LocalName == "Identity");
        string name = (string)identity.Attribute("Name")!;
        string publisher = (string)identity.Attribute("Publisher")!;

        // The publisher ID: the first 8 bytes of SHA-256 over the UTF-16 publisher, 5 bits a letter (Crockford's
        // base 32, a 0 bit added to make 65).
        byte[] hash = SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
        string bits = string.Concat(hash.Take(8).Select(b => Convert.ToString(b, 2).PadLeft(8, '0'))) + "0";
        const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        string id = string.Concat(Enumerable.Range(0, 13).Select(i => Alphabet[Convert.ToInt32(bits.Substring(i * 5, 5), 2)]));

        Assert.Equal("gzvqk2jf034f6", id); // the registered package's, 2026-11-27
        Assert.Contains($"$script:PackageFamilyName = '{name}_{id}'", Module);
    }

    [SetupFact]
    public void Every_file_CopyMaster_exe_builds_to_is_named_CopyMaster_so_the_uninstaller_takes_it()
    {
        // The uninstaller deletes CopyMaster* files only (CopyMasterSetup.psm1): a dependency named otherwise would
        // be installed and then left behind. install.ps1 refuses such a build; this says so sooner.
        Assert.Empty(Directory.GetDirectories(Setup.ExeBuild));
        Assert.All(Directory.GetFiles(Setup.ExeBuild), file => Assert.StartsWith("CopyMaster", Path.GetFileName(file)));
    }

    [Fact]
    public void The_package_holds_only_its_manifest_and_logos()
    {
        if (FindSdkTool("makeappx.exe") is null) return; // no Windows SDK: nothing to build with
        using var scratch = new TestFolder();
        string msix = scratch.At("CopyMaster.msix");
        var result = Setup.Command("powershell",
            $"Import-Module '{Path.Combine(Setup.Folder, "CopyMasterSetup.psm1")}'; New-CopyMasterPackage '{ShellBuild.Root}' '{msix}'");
        Assert.True(result.ExitCode == 0, result.Output);

        using var zip = ZipFile.OpenRead(msix);
        Assert.Equal(
            ["AppxBlockMap.xml", "AppxManifest.xml", "Assets/Square150x150Logo.png", "Assets/Square44x44Logo.png", "Assets/StoreLogo.png", "[Content_Types].xml"],
            zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
    }

    // ---- Install and uninstall, files only ------------------------------------------------------------------------

    [SetupTheory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public void An_install_lays_out_the_build_and_the_uninstall_leaves_nothing_of_it(string shell)
    {
        if (Setup.Shell(shell) is null) return;
        using var scratch = new TestFolder();
        string install = scratch.At("Program = Files, too", "CopyMaster");
        string beside = scratch.File(Path.Combine("Program = Files, too", "someone else.txt"), "not CopyMaster's");

        var installed = Setup.Install(shell, install);
        Assert.True(installed.ExitCode == 0, installed.Output);
        string[] expected = Directory.GetFiles(Setup.ExeBuild).Select(Path.GetFileName)
            .Where(name => !name!.EndsWith(".runtimeconfig.dev.json", StringComparison.Ordinal))
            .Append("CopyMasterShell.dll").Append("CopyMasterShell.pdb").Order(StringComparer.Ordinal).ToArray()!;
        Assert.Equal(expected, Directory.GetFiles(install).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(File.ReadAllBytes(ShellBuild.Dll!), File.ReadAllBytes(Path.Combine(install, "CopyMasterShell.dll")));

        var removed = Setup.Uninstall(shell, install);
        Assert.True(removed.ExitCode == 0, removed.Output);
        Assert.Contains("is gone", removed.Output);
        Assert.False(Directory.Exists(install));
        Assert.True(File.Exists(beside)); // the folder around it is someone else's
    }

    [SetupFact]
    public void Uninstall_deletes_only_CopyMasters_files_and_keeps_a_folder_that_holds_others()
    {
        using var scratch = new TestFolder();
        string install = scratch.At("CopyMaster");
        Assert.Equal(0, Setup.Install("powershell", install).ExitCode);
        string notes = scratch.File(Path.Combine("CopyMaster", "notes.txt"), "mine");
        string sub = scratch.Folder("CopyMaster", "plugins");

        var removed = Setup.Uninstall("powershell", install);
        Assert.Equal(1, removed.ExitCode); // the folder is still there
        Assert.Contains("isn't CopyMaster's alone", removed.Output);
        Assert.Equal(["notes.txt", "plugins"], Directory.GetFileSystemEntries(install).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(File.Exists(notes) && Directory.Exists(sub));
    }

    [SetupFact]
    public void A_DLL_in_use_is_renamed_aside_by_an_update_and_left_for_the_restart_by_an_uninstall()
    {
        using var scratch = new TestFolder();
        string install = scratch.At("CopyMaster");
        Assert.Equal(0, Setup.Install("powershell", install).ExitCode);
        string dll = Path.Combine(install, "CopyMasterShell.dll");

        // Loaded, as Explorer would have it: it can be renamed, not deleted.
        IntPtr module = NativeLibrary.Load(dll);
        try
        {
            var updated = Setup.Install("powershell", install);
            Assert.True(updated.ExitCode == 0, updated.Output);
            string aside = Assert.Single(Directory.GetFiles(install, "CopyMasterShell.dll.*.old"));
            Assert.Equal(File.ReadAllBytes(ShellBuild.Dll!), File.ReadAllBytes(dll)); // a fresh copy in its place

            var removed = Setup.Uninstall("powershell", install);
            Assert.Contains("so deleted when Windows next restarts", removed.Output);
            Assert.Contains($"(test) not scheduled: {aside}", removed.Output);
            Assert.Contains($"(test) not scheduled: {install}", removed.Output); // the folder, once empty
            Assert.Equal([aside], Directory.GetFileSystemEntries(install));
        }
        finally
        {
            NativeLibrary.Free(module);
        }
    }

    [SetupFact]
    public void Install_and_uninstall_refuse_while_CopyMaster_runs_from_the_folder()
    {
        using var scratch = new TestFolder();
        string install = scratch.At("CopyMaster");
        Assert.Equal(0, Setup.Install("powershell", install).ExitCode);

        // Something named CopyMaster.exe running from there, standing in for a copy in progress.
        string exe = Path.Combine(install, "CopyMaster.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe, overwrite: true);
        var start = new ProcessStartInfo(exe, "/c ping -n 60 127.0.0.1 > nul") { UseShellExecute = false, CreateNoWindow = true };
        using var running = Process.Start(start)!;
        try
        {
            var installed = Setup.Install("powershell", install);
            Assert.NotEqual(0, installed.ExitCode);
            Assert.Contains($"CopyMaster is running from {install} (pid {running.Id})", installed.Output);

            var removed = Setup.Uninstall("powershell", install);
            Assert.NotEqual(0, removed.ExitCode);
            Assert.Contains("CopyMaster is running from", removed.Output);
            Assert.True(File.Exists(Path.Combine(install, "CopyMasterShell.dll"))); // nothing touched
            Assert.False(running.HasExited); // and it wasn't stopped
        }
        finally
        {
            running.Kill(entireProcessTree: true);
            running.WaitForExit();
        }
    }

    [SetupFact]
    public void WhatIf_says_what_it_would_do_and_does_nothing()
    {
        using var scratch = new TestFolder();
        string install = scratch.At("CopyMaster");
        var installed = Setup.Install("powershell", install, "-WhatIf");
        Assert.True(installed.ExitCode == 0, installed.Output);
        Assert.Contains("What if", installed.Output);
        Assert.False(Directory.Exists(install));

        Assert.Equal(0, Setup.Install("powershell", install).ExitCode);
        var removed = Setup.Uninstall("powershell", install, "-WhatIf");
        Assert.True(removed.ExitCode == 0, removed.Output);
        Assert.Contains("What if", removed.Output);
        Assert.True(File.Exists(Path.Combine(install, "CopyMasterShell.dll")));
    }

    [SetupFact]
    public void FilesOnly_needs_a_folder_to_lay_out_or_remove()
    {
        var installed = Setup.Script("powershell", "install.ps1", "-NoBuild", "-FilesOnly");
        Assert.NotEqual(0, installed.ExitCode);
        Assert.Contains("say where with -Path", installed.Output);
        var removed = Setup.Script("powershell", "uninstall.ps1", "-FilesOnly");
        Assert.NotEqual(0, removed.ExitCode);
        Assert.Contains("say where with -Path", removed.Output);
    }

    // ---- The check -------------------------------------------------------------------------------------------------

    [SetupFact]
    public void The_check_lists_an_install_folder_it_is_shown_and_not_one_that_is_gone()
    {
        using var scratch = new TestFolder();
        string install = scratch.At("CopyMaster");
        Assert.Equal(0, Setup.Install("powershell", install).ExitCode);

        var found = Setup.Script("powershell", "check.ps1", "-Path", install);
        Assert.Contains($"[Folder] {install}", found.Output);
        Assert.Contains("CopyMaster's own", found.Output);
        Assert.Equal(1, found.ExitCode);

        Assert.Equal(0, Setup.Uninstall("powershell", install).ExitCode);
        var gone = Setup.Script("powershell", "check.ps1", "-Path", install);
        Assert.DoesNotContain(install, gone.Output);
    }

    private static string? FindSdkTool(string tool)
    {
        string kits = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "bin");
        if (!Directory.Exists(kits)) return null;
        return Directory.GetDirectories(kits)
            .Where(d => Version.TryParse(Path.GetFileName(d), out _))
            .OrderByDescending(d => Version.Parse(Path.GetFileName(d)))
            .Select(d => Path.Combine(d, "x64", tool))
            .FirstOrDefault(File.Exists);
    }
}
