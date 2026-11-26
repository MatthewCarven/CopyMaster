using CopyMaster.Engine;
using CopyMaster.Engine.Instance;
using CopyMaster.Engine.Jobs;

namespace CopyMaster;

/// <summary>
/// CopyMaster.exe. One runs per user session: a launch while one is running hands its job
/// over the pipe and exits, so every job lands in the same queue window.
/// <code>
/// CopyMaster.exe                                       show the window
/// CopyMaster.exe --job &lt;file&gt;                          queue a job file (how the shell extension asks)
/// CopyMaster.exe copy|move --dest &lt;folder&gt; &lt;source&gt;...  queue a job from a terminal
/// </code>
/// </summary>
internal static class Program
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);

        Launch launch;
        try
        {
            launch = Launch.Parse(args);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or UnauthorizedAccessException)
        {
            QuestionForm.Tell(null, "CopyMaster didn't understand that", $"{ex.Message}\n\n{Launch.Usage}");
            return 2;
        }

        string name = SingleInstance.DefaultName;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            using (var instance = SingleInstance.TryClaim(name))
            {
                if (instance is not null) return RunInstance(name, launch);
            }

            var outcome = HandOverClient.Send(name, launch.Spec, ConnectTimeout);
            switch (outcome.Status)
            {
                case HandOverStatus.Accepted:
                    launch.DeleteJobFile();
                    return 0;
                case HandOverStatus.Rejected:
                    QuestionForm.Tell(null, "CopyMaster couldn't take the job", outcome.Message ?? "");
                    return 1;
            }
            // No answer: the running one is starting up or quitting. Look again.
            Thread.Sleep(250);
        }

        QuestionForm.Tell(null, "CopyMaster is running but didn't answer",
            "Try again in a moment. If it keeps happening, end CopyMaster in Task Manager.");
        return 1;
    }

    private static int RunInstance(string name, Launch launch)
    {
        // A bug in the window mustn't take running copies down with it: log it, say so once, carry on.
        bool told = false;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            string log = Log(e.Exception);
            if (told) return;
            told = true;
            QuestionForm.Tell(null, "Something went wrong in CopyMaster's window",
                $"{e.Exception.Message}\n\nJobs carry on. The details are in {log}.");
        };

        string settingsPath = AppSettings.DefaultPath;
        using var form = new MainForm(AppSettings.Load(settingsPath), settingsPath);
        _ = form.Handle; // so jobs from the pipe can be marshalled before the window shows

        using var server = new HandOverServer(name, form.TakeFromAnotherLaunch);
        form.StopListening = server.Dispose;
        try
        {
            server.Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the pipe, later launches can't hand over (they'll say so), but this one still works.
            Log(ex);
        }

        if (launch.Spec is not null)
        {
            form.AddJob(launch.Spec);
            launch.DeleteJobFile();
        }
        Application.Run(form);
        return 0;
    }

    /// <summary>Appends an exception to %LOCALAPPDATA%\CopyMaster\errors.log; returns the log's path.</summary>
    private static string Log(Exception exception)
    {
        string path = Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "errors.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {exception}\n\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere to write it; the dialog still says what happened.
        }
        return path;
    }
}

/// <summary>What this launch was asked to do.</summary>
internal sealed class Launch
{
    public const string Usage = """
        CopyMaster.exe                                       show the window
        CopyMaster.exe --job <file>                          queue a job file
        CopyMaster.exe copy|move --dest <folder> <source>...  queue a copy or move

        New jobs take the speed limit, verify and conflict settings from Settings.
        """;

    /// <summary>Where the shell extension writes job files; those are deleted once taken.</summary>
    private static readonly string JobsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CopyMaster", "jobs");

    /// <summary>The job, or null to just open (or show) the window.</summary>
    public JobSpec? Spec { get; private init; }

    public string? JobFilePath { get; private init; }

    public static Launch Parse(string[] args)
    {
        if (args.Length == 0) return new Launch();

        if (args[0] == "--job")
        {
            if (args.Length != 2) throw new ArgumentException("--job takes one job file and nothing else.");
            string path = Path.GetFullPath(args[1]);
            return new Launch { Spec = JobFile.Read(path), JobFilePath = path };
        }

        var operation = args[0] switch
        {
            "copy" => CopyOperation.Copy,
            "move" => CopyOperation.Move,
            _ => throw new ArgumentException($"Unknown command: {args[0]}"),
        };
        string? destination = null;
        var sources = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--dest")
            {
                if (++i >= args.Length) throw new ArgumentException("--dest needs a folder.");
                destination = args[i];
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unknown option {args[i]}. Speed, verify and conflicts come from Settings.");
            }
            else
            {
                sources.Add(args[i]);
            }
        }
        if (destination is null) throw new ArgumentException("--dest <folder> is needed.");
        if (sources.Count == 0) throw new ArgumentException("Nothing to copy: give one or more sources.");

        // Relative paths mean this launch's folder; the running instance has its own.
        return new Launch
        {
            Spec = new JobSpec(operation, Path.GetFullPath(destination), sources.Select(Path.GetFullPath).ToList()),
        };
    }

    /// <summary>Deletes the job file once its job is queued, if the shell extension wrote it.</summary>
    public void DeleteJobFile()
    {
        if (JobFilePath is null ||
            !string.Equals(Path.GetDirectoryName(JobFilePath), JobsFolder, StringComparison.OrdinalIgnoreCase))
        {
            return; // someone's own file: leave it
        }
        try
        {
            File.Delete(JobFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale job file is harmless.
        }
    }
}
