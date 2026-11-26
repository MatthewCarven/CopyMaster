using System.Collections.Concurrent;
using CopyMaster.Engine;
using CopyMaster.Engine.Jobs;
using CopyMaster.Engine.Scheduling;

namespace CopyMaster;

/// <summary>
/// The queue window: a card per job, newest at the bottom, refreshed four times a second.
/// Jobs arrive from the command line, or from later launches over the pipe.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly JobQueue _queue;
    private readonly ConcurrentDictionary<QueuedJob, UiPrompts> _prompts = new();
    private readonly Dictionary<QueuedJob, JobCard> _cards = [];
    private readonly Dictionary<QueuedJob, DetailsForm> _details = [];
    private readonly FlowLayoutPanel _list;
    private readonly Label _empty;
    private readonly Label _summary;
    private readonly Button _clear;
    private readonly ToolTip _tips = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly string _settingsPath;
    private AppSettings _settings;
    private volatile bool _quitting;
    private volatile bool _syncQueued;

    public MainForm(AppSettings settings, string settingsPath)
    {
        _settings = settings;
        _settingsPath = settingsPath;
        _queue = new JobQueue(job => _prompts.GetOrAdd(job, j => new UiPrompts(this, j)));
        _queue.Changed += QueueChanged;

        Text = "CopyMaster";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(LogicalToDeviceUnits(720), LogicalToDeviceUnits(460));
        MinimumSize = new Size(LogicalToDeviceUnits(520), LogicalToDeviceUnits(260));
        BackColor = Theme.Window;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon.Value;

        int s(int n) => LogicalToDeviceUnits(n);

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(s(12), s(10), s(12), 0),
            BackColor = Theme.Window,
        };
        var settingsButton = BarButton("Settings…", ShowSettings);
        _clear = BarButton("Clear finished", () => _queue.ClearFinished());
        _tips.SetToolTip(settingsButton, "Speed limit, verify and conflicts for new jobs");
        _tips.SetToolTip(_clear, "Take finished jobs off the list");
        _summary = new Label
        {
            AutoSize = true,
            ForeColor = Theme.Dim,
            Margin = new Padding(s(12), s(6), 0, 0),
            UseMnemonic = false,
        };
        bar.Controls.AddRange([settingsButton, _clear, _summary]);

        _list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(s(12), s(10), s(12), s(4)),
            BackColor = Theme.Window,
        };
        _list.ClientSizeChanged += (_, _) => SizeCards();

        _empty = new Label
        {
            Text = "No jobs.\n\nRight-drag files in Explorer and choose “CopyMaster Copy here” or “Move here”,\n" +
                   "or copy or cut them, right-click a folder and choose “CopyMaster Paste”,\n" +
                   "or run CopyMaster copy|move --dest <folder> <source>...",
            ForeColor = Theme.Dim,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
            UseMnemonic = false,
        };

        Controls.Add(_list);
        Controls.Add(_empty);
        Controls.Add(bar);
        _empty.BringToFront();

        _timer.Tick += (_, _) => Sync();
        _timer.Start();
        Sync();
    }

    /// <summary>Called when quitting starts, so later launches start a new instance instead.</summary>
    public Action? StopListening { get; set; }

    private Button BarButton(string text, Action click)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(LogicalToDeviceUnits(6), 0, LogicalToDeviceUnits(6), 0),
            Margin = new Padding(0, 0, LogicalToDeviceUnits(8), 0),
            UseVisualStyleBackColor = true,
        };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Queues a job with the current settings (a share's own speed limit if it uses one). UI thread.</summary>
    public void AddJob(JobSpec spec)
    {
        var settings = _settings;
        _queue.Add(spec, resources => settings.ToJobOptions(network: Disks.UsesNetwork(resources)));
    }

    /// <summary>A later launch handed over a job (or null: just show yourself). Any thread.</summary>
    public void TakeFromAnotherLaunch(JobSpec? spec)
    {
        if (_quitting) throw new InvalidOperationException("CopyMaster is quitting; try again in a moment.");
        BeginInvoke(() =>
        {
            if (spec is not null) AddJob(spec);
            ShowYourself();
        });
    }

    private void ShowYourself()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    /// <summary>Opens a job's question beside the window (and the window, if it was minimised).</summary>
    public void ShowQuestion(Form dialog)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        dialog.Show(this);
        dialog.Activate();
    }

    /// <summary>BeginInvoke that does nothing once the window is gone.</summary>
    public void BeginInvokeQuietly(Action action)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed) BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // Closing.
        }
    }

    private void QueueChanged()
    {
        // Raised on job threads too: refresh once, soon, on the UI thread.
        if (_syncQueued) return;
        _syncQueued = true;
        BeginInvokeQuietly(() =>
        {
            _syncQueued = false;
            Sync();
        });
    }

    /// <summary>Brings the cards in line with the queue, and refreshes them.</summary>
    private void Sync()
    {
        var jobs = _queue.Jobs;
        _list.SuspendLayout();

        foreach (var (job, card) in _cards.Where(c => !jobs.Contains(c.Key)).ToList())
        {
            _list.Controls.Remove(card);
            card.Dispose();
            _cards.Remove(job);
            _prompts.TryRemove(job, out _);
            if (_details.Remove(job, out var details)) details.Close();
        }

        for (int i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            if (!_cards.TryGetValue(job, out var card))
            {
                card = new JobCard(job, _queue, _tips, ShowDetails) { Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(8)) };
                _cards[job] = card;
                _list.Controls.Add(card);
                SizeCard(card);
            }
            if (_list.Controls.GetChildIndex(card) != i) _list.Controls.SetChildIndex(card, i);
            card.UpdateView(_prompts.TryGetValue(job, out var prompts) && prompts.Asking, i == 0, i == jobs.Count - 1);
        }

        _list.ResumeLayout(true);
        _empty.Visible = jobs.Count == 0;
        _list.Visible = jobs.Count > 0;
        ShowTotals(jobs);

        if (_quitting && _queue.RunningCount == 0) Close();
    }

    private void ShowTotals(IReadOnlyList<QueuedJob> jobs)
    {
        int running = jobs.Count(j => j.State == QueueState.Running);
        int waiting = jobs.Count(j => j.State is QueueState.Waiting or QueueState.Held);
        int done = jobs.Count(j => j.State == QueueState.Done);
        _clear.Enabled = done > 0;

        var parts = new List<string>();
        if (running > 0) parts.Add($"{running} running");
        if (waiting > 0) parts.Add($"{waiting} waiting");
        if (done > 0) parts.Add($"{done} finished");
        string summary = string.Join(" · ", parts);
        if (_summary.Text != summary) _summary.Text = summary;

        // The title shows how far the running jobs have got, so it reads from the taskbar.
        long doneBytes = 0, totalBytes = 0;
        foreach (var job in jobs.Where(j => j.State == QueueState.Running))
        {
            if (job.Progress is { BytesTotal: > 0 } p)
            {
                doneBytes += p.BytesDone;
                totalBytes += p.BytesTotal;
            }
        }
        string title = _quitting ? "Stopping… · CopyMaster"
            : running > 0 && totalBytes > 0 ? $"{Math.Floor(100.0 * doneBytes / totalBytes):0}% · CopyMaster"
            : "CopyMaster";
        if (Text != title) Text = title;
    }

    private void SizeCards()
    {
        foreach (var card in _cards.Values) SizeCard(card);
    }

    private void SizeCard(JobCard card)
    {
        int width = _list.ClientSize.Width - _list.Padding.Horizontal - card.Margin.Horizontal;
        if (width > 0 && card.Width != width) card.Width = width;
    }

    private void ShowDetails(QueuedJob job)
    {
        if (_details.TryGetValue(job, out var open))
        {
            open.Activate();
            return;
        }
        var details = new DetailsForm(job, j => _queue.RetryFailed(j));
        details.FormClosed += (_, _) => _details.Remove(job);
        _details[job] = details;
        details.Show(this);
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(_settings);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        _settings = form.Settings;
        try
        {
            _settings.Save(_settingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            QuestionForm.Tell(this, "The settings couldn't be saved", $"{ex.Message}\n\nThey apply until CopyMaster quits.");
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (_quitting)
        {
            e.Cancel = _queue.RunningCount > 0; // still stopping
            return;
        }

        int unfinished = _queue.Jobs.Count(j => j.State != QueueState.Done);
        if (unfinished > 0 && e.CloseReason != CloseReason.WindowsShutDown)
        {
            int choice = QuestionForm.Ask(this,
                unfinished == 1 ? "A job hasn't finished" : $"{unfinished} jobs haven't finished",
                "Quitting cancels them. Files already copied stay; a half-copied file is deleted.",
                ["Cancel and quit", "Keep going"]);
            if (choice != 0)
            {
                e.Cancel = true;
                return;
            }
        }

        _quitting = true;
        StopListening?.Invoke();
        _queue.CancelAll();
        if (_queue.RunningCount > 0)
        {
            e.Cancel = true; // Sync closes the window once they've stopped
            Sync();
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }
}
