#:property TargetFramework=net10.0-windows
#:property UseWindowsForms=true
#:property PublishAot=false
#:property Nullable=enable
#:project ../src/CopyMaster/CopyMaster.csproj

// Renders CopyMaster's windows into PNGs, with real jobs in every state (finished, failed,
// running, waiting, held) and each dialog. For looking at the UI without a person: the
// windows are hosted inside a borderless form far off-screen that never takes focus, so
// nothing shows on the screen or steals the keyboard. Internal types are reached by reflection.
//
//   dotnet run --file dev\window-shots.cs -- <out folder> [dark|light]
//
// DrawToBitmap is used because PrintWindow, on a window off-screen, came back with black
// patches that differed from run to run.
using System.Diagnostics;
using System.Reflection;
using CopyMaster.Engine;
using CopyMaster.Engine.Jobs;
using CopyMaster.Engine.Scheduling;

string outDir = args.Length > 0 ? args[0] : ".";
string mode = args.Length > 1 ? args[1] : "dark";
Directory.CreateDirectory(outDir);

Application.EnableVisualStyles();
Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
Application.SetColorMode(mode == "light" ? SystemColorMode.Classic : SystemColorMode.Dark);

var ui = Assembly.Load("CopyMaster");
Type T(string name) => ui.GetType("CopyMaster." + name, throwOnError: true)!;
const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

string work = Path.Combine(Path.GetTempPath(), "CopyMasterShot", Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);
string Make(string rel, long size)
{
    string p = Path.Combine(work, rel);
    Directory.CreateDirectory(Path.GetDirectoryName(p)!);
    using var f = new FileStream(p, FileMode.Create);
    f.SetLength(size);
    return p;
}
string Dir(string rel) { string p = Path.Combine(work, rel); Directory.CreateDirectory(p); return p; }

var settings = new AppSettings { SpeedLimit = 4 * Units.MB, Verify = true, Conflicts = ConflictPolicy.Skip };
var main = (Form)Activator.CreateInstance(T("MainForm"), Any, null, [settings, Path.Combine(work, "settings.json")], null)!;
var queue = (JobQueue)T("MainForm").GetField("_queue", Any)!.GetValue(main)!;
void Add(JobSpec spec) => T("MainForm").GetMethod("AddJob")!.Invoke(main, [spec]);
void Pump(double seconds)
{
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed.TotalSeconds < seconds) { Application.DoEvents(); Thread.Sleep(15); }
}

var host = new Host { Location = new Point(-20000, -20000), Size = new Size(900, 700) };
main.TopLevel = false;
main.FormBorderStyle = FormBorderStyle.None;
main.Dock = DockStyle.Fill;
host.Controls.Add(main);
main.Show();
host.Show();
host.ClientSize = main.ClientSize = new Size(main.LogicalToDeviceUnits(760), main.LogicalToDeviceUnits(640));

// 1. A finished job, and one finished with a failure.
Add(new JobSpec(CopyOperation.Copy, Dir("done-dest"), [Make(@"photos\a.jpg", 300_000), Make(@"photos\b.jpg", 500_000)]));
string locked = Make(@"docs\locked.xlsx", 20_000);
Make(@"docs\notes.txt", 2_000);
using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
{
    Add(new JobSpec(CopyOperation.Move, Dir("failed-dest"), [Path.Combine(work, "docs")]));
    var until = DateTime.UtcNow.AddSeconds(10);
    while (queue.Jobs.Any(j => j.State != QueueState.Done) && DateTime.UtcNow < until) Pump(0.05);
}

// 2. Running (limited, verifying), waiting behind it, and held.
Add(new JobSpec(CopyOperation.Copy, Dir(@"Backup\Videos"), [Make(@"videos\Holiday 2026 - the long one.mp4", 96 * Units.MB)]));
Add(new JobSpec(CopyOperation.Copy, Dir(@"Backup\Music"), [Make(@"music\album1.flac", 30 * Units.MB), Make(@"music\album2.flac", 30 * Units.MB), Make(@"music\album3.flac", 1)]));
Add(new JobSpec(CopyOperation.Move, Dir(@"Archive"), [Make(@"old\2019.zip", 10 * Units.MB)]));
var jobs = queue.Jobs;
queue.Pause(jobs[^1]);
Pump(3.5);
Save(host, "main");

// 3. The dialogs, each hosted the same way.
var running = queue.Jobs.First(j => j.State == QueueState.Running);
var conflict = new ConflictInfo(@"C:\Users\Admin\Pictures\Holiday\IMG_2041.jpg", @"D:\Backup\Holiday\IMG_2041.jpg",
    2_413_337, new DateTime(2026, 11, 20, 4, 12, 9, DateTimeKind.Utc), 2_398_102, new DateTime(2026, 11, 2, 22, 40, 51, DateTimeKind.Utc));
Dialog((Form)Activator.CreateInstance(T("ConflictForm"), Any, null, [conflict, running], null)!, "conflict");
Dialog((Form)Activator.CreateInstance(T("SettingsForm"), Any, null, [settings], null)!, "settings");
Dialog((Form)Activator.CreateInstance(T("QuestionForm"), Any, null, ["Disk full", "The destination disk is full",
    "There is not enough space on the disk.\n\nThe file being copied when it filled up has been removed. Make some room on the disk holding D:\\Backup, then try again; or skip that file; or stop the job here (what's copied so far stays).",
    new[] { "Try again", "Skip this file", "Stop the job" }, 2], null)!, "diskfull");
var failed = queue.Jobs.First(j => j.Result is { Failures.Count: > 0 });
Dialog((Form)Activator.CreateInstance(T("DetailsForm"), Any, null, [failed, new Action<QueuedJob>(_ => { })], null)!, "details");

queue.CancelAll();
Pump(1);
host.Close();
try { Directory.Delete(work, true); } catch (IOException) { }
Console.WriteLine("done");

void Dialog(Form dialog, string name)
{
    var h = new Host { Location = new Point(-20000, -20000) };
    dialog.TopLevel = false;
    dialog.FormBorderStyle = FormBorderStyle.None;
    h.Controls.Add(dialog);
    dialog.Show();
    h.Show();
    Pump(0.3);
    dialog.Location = Point.Empty; // OnLoad centred it on the screen
    h.ClientSize = dialog.Size;
    Pump(0.5);
    h.Refresh();
    Pump(0.5);
    Save(h, name);
    h.Close();
}

void Save(Form form, string name)
{
    using var bmp = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
    form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.ClientSize));
    string path = Path.Combine(outDir, $"{name}-{mode}.png");
    bmp.Save(path);
    Console.WriteLine(path);
}

class Host : Form
{
    public Host()
    {
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
    }
    protected override bool ShowWithoutActivation => true;
}
