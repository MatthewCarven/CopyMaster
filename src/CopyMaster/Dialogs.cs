using System.Text;
using CopyMaster.Engine;
using CopyMaster.Engine.Jobs;
using CopyMaster.Engine.Scheduling;

namespace CopyMaster;

/// <summary>A small fixed dialog that sizes itself to its contents and opens over its owner.</summary>
internal class DialogBase : Form
{
    protected DialogBase(string title)
    {
        Text = title;
        Icon = AppIcon.Value;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(LogicalToDeviceUnits(16));
    }

    protected TableLayoutPanel Column() => new()
    {
        ColumnCount = 1,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
    };

    protected Label Words(string text, bool bold = false, Color? color = null, int width = 480, int below = 4)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            UseMnemonic = false,
            MaximumSize = new Size(LogicalToDeviceUnits(width), 0),
            Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(below)),
        };
        if (bold) label.Font = new Font(Font.FontFamily, Font.Size * 1.15f, FontStyle.Bold);
        if (color is Color c) label.ForeColor = c;
        return label;
    }

    protected Button MakeButton(string text, Action click)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(LogicalToDeviceUnits(84), 0),
            Padding = new Padding(LogicalToDeviceUnits(6), LogicalToDeviceUnits(2), LogicalToDeviceUnits(6), LogicalToDeviceUnits(2)),
            Margin = new Padding(LogicalToDeviceUnits(8), 0, 0, 0),
            UseVisualStyleBackColor = true,
        };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Buttons in a row, against the right edge.</summary>
    protected FlowLayoutPanel ButtonRow(params Control[] buttons)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, LogicalToDeviceUnits(12), 0, 0),
        };
        row.Controls.AddRange(buttons);
        return row;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = Owner is { Visible: true, WindowState: not FormWindowState.Minimized } owner
            ? owner.Bounds
            : Screen.FromPoint(Cursor.Position).WorkingArea;
        var screen = Screen.FromRectangle(area).WorkingArea;
        int x = area.Left + (area.Width - Width) / 2, y = area.Top + (area.Height - Height) / 2;
        Location = new Point(Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - Width)),
            Math.Clamp(y, screen.Top, Math.Max(screen.Top, screen.Bottom - Height)));
    }
}

/// <summary>"A file of that name is already there": both files, and what to do about it.</summary>
internal sealed class ConflictForm : DialogBase
{
    private readonly CheckBox _forAll;

    public ConflictForm(ConflictInfo conflict, QueuedJob job) : base("File already there · " + JobText.Title(job.Spec))
    {
        bool move = job.Spec.Operation == CopyOperation.Move;
        string name = Path.GetFileName(conflict.Destination);
        string folder = Path.GetDirectoryName(conflict.Destination) ?? "";
        bool sourceNewer = conflict.SourceModifiedUtc > conflict.DestinationModifiedUtc;
        bool destinationNewer = conflict.DestinationModifiedUtc > conflict.SourceModifiedUtc;

        var column = Column();
        column.Controls.Add(Words($"“{name}” is already in {JobText.NameOf(folder)}", bold: true));
        column.Controls.Add(Words(folder, color: Theme.Dim, below: 12));

        column.Controls.Add(Words(move ? "Moving:" : "Copying:", below: 0));
        column.Controls.Add(Words(Describe(conflict.SourceLength, conflict.SourceModifiedUtc, sourceNewer), color: Theme.Text, below: 0));
        column.Controls.Add(Words("from " + (Path.GetDirectoryName(conflict.Source) ?? conflict.Source), color: Theme.Dim, below: 10));
        column.Controls.Add(Words("Already there:", below: 0));
        column.Controls.Add(Words(Describe(conflict.DestinationLength, conflict.DestinationModifiedUtc, destinationNewer),
            color: Theme.Text, below: 12));

        _forAll = new CheckBox
        {
            Text = "Do this for every conflict in this job",
            AutoSize = true,
            UseMnemonic = false,
            Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(4)),
        };
        column.Controls.Add(_forAll);

        var tips = new ToolTip();
        FormClosed += (_, _) => tips.Dispose();
        var overwrite = MakeButton("Overwrite", () => Choose(ConflictResolution.Overwrite));
        var skip = MakeButton("Skip", () => Choose(ConflictResolution.Skip));
        var rename = MakeButton("Keep both", () => Choose(ConflictResolution.Rename));
        var newer = MakeButton("Keep newer", () => Choose(ConflictResolution.KeepNewer));
        var cancel = MakeButton("Cancel job", () => Choose(ConflictResolution.CancelJob));
        tips.SetToolTip(overwrite, "Replace the file that's there");
        tips.SetToolTip(skip, "Leave the file that's there, and don't copy this one");
        tips.SetToolTip(rename, $"Copy this one as “{SafeNextName(conflict.Destination)}”");
        tips.SetToolTip(newer, sourceNewer ? "This one is newer, so it replaces the one there"
            : "The one there is as new or newer, so this one is skipped");
        tips.SetToolTip(cancel, "Stop the whole job; what's copied so far stays");
        cancel.Margin = new Padding(LogicalToDeviceUnits(24), 0, 0, 0);
        column.Controls.Add(ButtonRow(overwrite, skip, rename, newer, cancel));

        Controls.Add(column);
        CancelButton = skip; // Esc, or closing the window, skips this file
    }

    /// <summary>The answer. Closing the window counts as Skip.</summary>
    public ConflictAnswer Answer { get; private set; } = new(ConflictResolution.Skip);

    private void Choose(ConflictResolution resolution)
    {
        Answer = new ConflictAnswer(resolution, resolution != ConflictResolution.CancelJob && _forAll.Checked);
        Close();
    }

    private static string Describe(long length, DateTime modifiedUtc, bool newer) =>
        $"{Units.Bytes(length)} ({length:N0} bytes) · modified {modifiedUtc.ToLocalTime():G}" + (newer ? " · newer" : "");

    private static string SafeNextName(string path)
    {
        try
        {
            return Path.GetFileName(UniqueNames.Next(path, isFolder: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "name (2).ext";
        }
    }
}

/// <summary>A question with a few answers, for the rarer prompts (a full disk, not enough room, quitting).</summary>
internal sealed class QuestionForm : DialogBase
{
    public QuestionForm(string title, string heading, string body, IReadOnlyList<string> answers, int closeAnswer) : base(title)
    {
        Choice = closeAnswer;
        var column = Column();
        column.Controls.Add(Words(heading, bold: true, below: 8));
        if (body.Length > 0) column.Controls.Add(Words(body));
        var buttons = answers.Select((text, i) => (Control)MakeButton(text, () => { Choice = i; Close(); })).ToArray();
        column.Controls.Add(ButtonRow(buttons));
        Controls.Add(column);
        CancelButton = (IButtonControl)buttons[closeAnswer];
    }

    /// <summary>The index of the answer chosen; closing the window picks the close answer.</summary>
    public int Choice { get; private set; }

    /// <summary>Asks and waits (a modal dialog).</summary>
    public static int Ask(IWin32Window? owner, string heading, string body, IReadOnlyList<string> answers, int closeAnswer = -1)
    {
        using var form = new QuestionForm("CopyMaster", heading, body, answers, closeAnswer < 0 ? answers.Count - 1 : closeAnswer);
        if (owner is null) form.ShowInTaskbar = true;
        form.ShowDialog(owner);
        return form.Choice;
    }

    public static void Tell(IWin32Window? owner, string heading, string body) => Ask(owner, heading, body, ["OK"]);
}

/// <summary>A finished job's failures and notes, with Retry failed.</summary>
internal sealed class DetailsForm : Form
{
    private readonly ListView _list;
    private readonly QueuedJob _job;

    public DetailsForm(QueuedJob job, Action<QueuedJob> retry)
    {
        _job = job;
        Text = "Details · " + JobText.Title(job.Spec);
        Icon = AppIcon.Value;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        MinimizeBox = false;
        Size = new Size(LogicalToDeviceUnits(760), LogicalToDeviceUnits(380));
        Padding = new Padding(LogicalToDeviceUnits(12));

        _list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            Dock = DockStyle.Fill,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _list.Columns.Add("Item", LogicalToDeviceUnits(380));
        _list.Columns.Add("Problem", LogicalToDeviceUnits(330));

        var result = job.Result!;
        foreach (var failure in result.Failures)
        {
            _list.Items.Add(new ListViewItem([failure.Source, failure.Message]) { ToolTipText = failure.Destination ?? "" });
        }
        foreach (string note in result.Notes) _list.Items.Add(new ListViewItem(["(note)", note]) { ForeColor = Theme.Dim });
        _list.ShowItemToolTips = true;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, LogicalToDeviceUnits(10), 0, 0),
        };
        buttons.Controls.Add(Button("Close", Close));
        int retryable = result.Failures.Count(f => f.CanRetry);
        if (job.CanRetry)
        {
            buttons.Controls.Add(Button($"Retry failed ({retryable})", () =>
            {
                retry(job);
                Close();
            }));
        }
        buttons.Controls.Add(Button("Copy list", CopyList));

        Controls.Add(_list);
        Controls.Add(buttons);
    }

    public QueuedJob Job => _job;

    private Button Button(string text, Action click)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(LogicalToDeviceUnits(84), 0),
            Margin = new Padding(LogicalToDeviceUnits(8), 0, 0, 0),
            UseVisualStyleBackColor = true,
        };
        button.Click += (_, _) => click();
        return button;
    }

    private void CopyList()
    {
        var text = new StringBuilder().AppendLine(JobText.Title(_job.Spec));
        foreach (ListViewItem item in _list.Items) text.Append(item.SubItems[0].Text).Append('\t').AppendLine(item.SubItems[1].Text);
        Clipboard.SetText(text.ToString());
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (Owner is { } owner)
        {
            Location = new Point(owner.Left + (owner.Width - Width) / 2, owner.Top + (owner.Height - Height) / 2);
        }
    }
}

/// <summary>What new jobs start with: the speed limit, verify, and what to do about conflicts.</summary>
internal sealed class SettingsForm : DialogBase
{
    private readonly SpeedSlider _speed = new("") { TrackWidth = 220 };
    private readonly SpeedSlider _networkSpeed = new("") { TrackWidth = 220 };
    private readonly CheckBox _verify;
    private readonly ComboBox _conflicts;

    public SettingsForm(AppSettings settings) : base("CopyMaster settings")
    {
        Settings = settings;
        var column = Column();
        column.Controls.Add(Words("These apply to jobs added from now on. A running job's speed can be changed on its card.",
            color: Theme.Dim, below: 12));

        column.Controls.Add(Words("Speed limit", bold: true, below: 2));
        _speed.Limit = settings.SpeedLimit;
        _speed.Size = _speed.GetPreferredSize(Size.Empty);
        _speed.Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(12));
        column.Controls.Add(_speed);

        column.Controls.Add(Words("Speed limit on network shares", bold: true, below: 2));
        _networkSpeed.Limit = settings.NetworkSpeedLimit;
        _networkSpeed.Size = _networkSpeed.GetPreferredSize(Size.Empty);
        _networkSpeed.Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(2));
        column.Controls.Add(_networkSpeed);
        column.Controls.Add(Words(@"For jobs that read from or write to a share (\\server\share or a mapped drive). " +
            "They get whichever of the two limits is lower.", color: Theme.Dim, below: 12));

        column.Controls.Add(Words("Verify", bold: true, below: 2));
        _verify = new CheckBox
        {
            Text = "Read each copy back from the disk and compare it with the original",
            Checked = settings.Verify,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(2)),
        };
        column.Controls.Add(_verify);
        column.Controls.Add(Words("Catches a copy that came out wrong (it's copied again). Takes about twice as long.",
            color: Theme.Dim, below: 12));

        column.Controls.Add(Words("When a file of the same name is already there", bold: true, below: 4));
        _conflicts = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = LogicalToDeviceUnits(260),
            Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(4)),
        };
        foreach (var policy in Enum.GetValues<ConflictPolicy>()) _conflicts.Items.Add(JobText.ConflictPolicyName(policy));
        _conflicts.SelectedIndex = (int)settings.Conflicts;
        column.Controls.Add(_conflicts);

        var ok = MakeButton("OK", Save);
        var cancel = MakeButton("Cancel", Close);
        column.Controls.Add(ButtonRow(ok, cancel));
        Controls.Add(column);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>The settings as they stand (changed only by OK).</summary>
    public AppSettings Settings { get; private set; }

    private void Save()
    {
        Settings = new AppSettings
        {
            SpeedLimit = _speed.Limit,
            NetworkSpeedLimit = _networkSpeed.Limit,
            Verify = _verify.Checked,
            Conflicts = (ConflictPolicy)_conflicts.SelectedIndex,
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}
