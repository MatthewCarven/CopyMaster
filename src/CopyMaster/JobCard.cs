using CopyMaster.Engine;
using CopyMaster.Engine.Jobs;
using CopyMaster.Engine.Scheduling;

namespace CopyMaster;

/// <summary>
/// One job in the queue window: what it is, where it's up to, its speed limit, and the
/// buttons that make sense for its state (start now, pause, reorder, cancel, retry...).
/// </summary>
internal sealed class JobCard : UserControl
{
    private const int Pad = 12, Gap = 4, BarHeight = 6;

    private readonly JobQueue _queue;
    private readonly ToolTip _tips;
    private readonly TextLine _title = new();
    private readonly TextLine _status = new() { Alignment = HorizontalAlignment.Right };
    private readonly TextLine _detail = new();
    private readonly TextLine _stats = new();
    private readonly ProgressStrip _bar = new();
    private readonly SpeedSlider _speed = new();
    private readonly FlowLayoutPanel _buttons = new()
    {
        FlowDirection = FlowDirection.RightToLeft,
        WrapContents = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Margin = Padding.Empty,
        Padding = Padding.Empty,
    };
    private readonly Button _startNow, _pause, _up, _down, _cancel, _details, _retry, _remove;
    private double _lastFraction;
    private string _shape = "";

    public JobCard(QueuedJob job, JobQueue queue, ToolTip tips, Action<QueuedJob> showDetails)
    {
        Job = job;
        _queue = queue;
        _tips = tips;
        DoubleBuffered = true;
        BackColor = Theme.Window;
        Margin = Padding.Empty;

        _title.Font = Theme.Bold(Font);
        foreach (var line in new Control[] { _title, _status, _detail, _stats, _bar, _speed, _buttons })
        {
            line.BackColor = Theme.Card;
        }
        _title.Show(JobText.Title(job.Spec), Theme.Text);
        tips.SetToolTip(_title, JobText.Title(job.Spec));

        _speed.ForeColor = Theme.Text;
        _speed.Limit = job.Control.SpeedLimit;
        _speed.LimitChanged += limit => Job.Control.SpeedLimit = limit;
        tips.SetToolTip(_speed, "Speed limit for this job; takes effect at once");

        // Right to left: the first added sits furthest right.
        _remove = AddButton("Remove", "Take this job off the list", () => _queue.Remove(Job));
        _cancel = AddButton("Cancel", "Stop this job; a half-copied file is deleted", () => _queue.Cancel(Job));
        _retry = AddButton("Retry failed", "Try the failed items again", () => _queue.RetryFailed(Job));
        _details = AddButton("Details", "What failed, and why", () => showDetails(Job));
        _down = AddButton("▼", "Move down the queue", () => _queue.MoveDown(Job));
        _up = AddButton("▲", "Move up the queue", () => _queue.MoveUp(Job));
        _pause = AddButton("Pause", "", TogglePause);
        _startNow = AddButton("Start now", "Start beside whatever is using its disk", () => _queue.StartNow(Job));
        tips.SetToolTip(_pause, "Pause or resume this job");

        Controls.AddRange([_title, _status, _detail, _bar, _stats, _speed, _buttons]);
        Height = CardHeight();
    }

    public QueuedJob Job { get; }

    private Button AddButton(string text, string tip, Action click)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            UseMnemonic = false,
            Margin = new Padding(LogicalToDeviceUnits(6), 0, 0, 0),
            Padding = new Padding(LogicalToDeviceUnits(4), 0, LogicalToDeviceUnits(4), 0),
            UseVisualStyleBackColor = true,
        };
        button.Click += (_, _) => click();
        if (tip.Length > 0) _tips.SetToolTip(button, tip);
        _buttons.Controls.Add(button);
        return button;
    }

    private void TogglePause()
    {
        bool paused = Job.State == QueueState.Held || (Job.State == QueueState.Running && Job.Control.IsPaused);
        if (paused) _queue.Resume(Job);
        else _queue.Pause(Job);
    }

    /// <summary>Redraws the card from the job's current state. Called a few times a second.</summary>
    public void UpdateView(bool asking, bool first, bool last)
    {
        var job = Job;
        var state = job.State;
        var progress = job.Progress;

        switch (state)
        {
            case QueueState.Waiting:
                _status.Show("Waiting for its disk", Theme.Dim);
                _detail.Show(job.WaitingFor is { } blocker ? "After: " + JobText.Title(blocker.Spec) : JobText.From(job.Spec),
                    Theme.Dim);
                _stats.Show(JobText.Options(job.Options), Theme.Dim);
                _bar.Show(0, Theme.Accent);
                break;

            case QueueState.Held:
                _status.Show("Paused, not started", Theme.Warning);
                _detail.Show(JobText.From(job.Spec), Theme.Dim);
                _stats.Show(JobText.Options(job.Options), Theme.Dim);
                _bar.Show(0, Theme.Accent);
                break;

            case QueueState.Running:
                ShowRunning(job, progress, asking);
                break;

            default:
                ShowDone(job);
                break;
        }

        bool waiting = state is QueueState.Waiting or QueueState.Held;
        bool running = state == QueueState.Running;
        bool cancelling = running && job.Control.IsCancelled;
        bool paused = state == QueueState.Held || (running && job.Control.IsPaused);

        _speed.Visible = state != QueueState.Done;
        if (!_speed.ContainsFocus) _speed.Limit = job.Control.SpeedLimit;

        SuspendLayout();
        _startNow.Visible = waiting;
        _pause.Visible = waiting || (running && !cancelling);
        SetText(_pause, paused ? "Resume" : "Pause");
        _up.Visible = _down.Visible = waiting;
        _up.Enabled = !first;
        _down.Enabled = !last;
        _cancel.Visible = running;
        _cancel.Enabled = !cancelling;
        _remove.Visible = state != QueueState.Running;

        var result = state == QueueState.Done ? job.Result : null;
        int failures = result?.Failures.Count ?? 0;
        _details.Visible = result is not null && (failures > 0 || result.Notes.Count > 0);
        SetText(_details, failures > 0 ? $"Details ({failures})" : "Notes");
        _retry.Visible = job.CanRetry;
        if (_retry.Visible) SetText(_retry, $"Retry failed ({result!.Failures.Count(f => f.CanRetry)})");
        ResumeLayout(false);

        // Lay out again only when something that moves things changed: the status text's
        // width, or which buttons show (this runs four times a second).
        string shape = _status.Text + "|" + string.Join(",",
            _buttons.Controls.Cast<Control>().Select(b => b.Visible ? b.Text : "")) + "|" + _speed.Visible;
        if (shape != _shape)
        {
            _shape = shape;
            _buttons.PerformLayout();
            PerformLayout();
        }
    }

    private void ShowRunning(QueuedJob job, JobProgress? progress, bool asking)
    {
        if (progress is null || progress.Phase == JobPhase.Scanning)
        {
            if (job.Control.IsCancelled) _status.Show("Cancelling…", Theme.Dim);
            else if (job.Control.IsPaused) _status.Show("Paused", Theme.Warning);
            else _status.Show("Listing files…", Theme.Dim);
            _detail.Show(JobText.From(job.Spec), Theme.Dim);
            _stats.Show(JobText.Options(job.Options), Theme.Dim);
            _bar.Show(0, Theme.Accent);
            return;
        }

        double fraction = progress.BytesTotal > 0 ? progress.BytesDone / (double)progress.BytesTotal
            : progress.ItemsTotal > 0 ? progress.ItemsDone / (double)progress.ItemsTotal : 0;
        _lastFraction = fraction;
        string percent = $"{Math.Floor(fraction * 100):0}%";
        string phase = progress.Phase switch
        {
            JobPhase.Copying => "Copying",
            JobPhase.Verifying => "Verifying",
            JobPhase.Comparing => "Comparing",
            JobPhase.Moving => "Moving",
            JobPhase.Finishing => "Finishing",
            _ => "Working",
        };

        if (job.Control.IsCancelled) _status.Show("Cancelling…", Theme.Dim);
        else if (asking) _status.Show("Waiting for your answer", Theme.Accent);
        else if (job.Control.IsPaused) _status.Show($"Paused · {percent}", Theme.Warning);
        else _status.Show($"{phase} · {percent}", Theme.Text);

        _detail.Show(progress.CurrentSource ?? JobText.From(job.Spec), Theme.Text, path: progress.CurrentSource is not null);
        _bar.Show(fraction, job.Control.IsPaused ? Theme.Warning : Theme.Accent);

        // With verify on, every byte counts twice (copied, then compared): show file sizes.
        int stages = job.Options.Verify ? 2 : 1;
        var parts = new List<string>
        {
            $"{Units.Bytes(progress.BytesDone / stages)} of {Units.Bytes(progress.BytesTotal / stages)}",
        };
        if (!job.Control.IsPaused && !asking)
        {
            parts.Add(Units.Speed(progress.BytesPerSecond));
            if (Units.TimeLeft(progress.BytesTotal - progress.BytesDone, progress.BytesPerSecond) is string left)
            {
                parts.Add(left + " left");
            }
        }
        parts.Add($"{progress.ItemsDone:N0} of {JobText.Count(progress.ItemsTotal, "item")}");
        _stats.Show(string.Join(" · ", parts), Theme.Dim);
    }

    private void ShowDone(QueuedJob job)
    {
        var result = job.Result;
        string problems = JobText.Problems(job);
        if (job.Crash is not null || result is null)
        {
            _status.Show("Failed", Theme.Bad);
            _bar.Show(_lastFraction, Theme.Bad);
        }
        else if (result.StoppedReason is not null)
        {
            _status.Show("Stopped", Theme.Bad);
            _bar.Show(_lastFraction, Theme.Bad);
        }
        else if (result.Cancelled)
        {
            _status.Show("Cancelled", Theme.Dim);
            _bar.Show(_lastFraction, Theme.Dim);
        }
        else if (result.Failures.Count > 0)
        {
            _status.Show($"Done, {result.Failures.Count:N0} failed", Theme.Bad);
            _bar.Show(1, Theme.Bad);
        }
        else
        {
            _status.Show("Done", Theme.Good);
            _bar.Show(1, Theme.Good);
        }
        _detail.Show(JobText.Summary(job), Theme.Text);
        _stats.Show(problems, result is { Failures.Count: > 0 } || result?.StoppedReason is not null ? Theme.Bad : Theme.Dim);
    }

    private static void SetText(Button button, string text)
    {
        if (button.Text != text) button.Text = text;
    }

    private int LineHeight => Math.Max(_title.LineHeight, _detail.LineHeight);

    private int BottomRowHeight => Math.Max(_speed.GetPreferredSize(Size.Empty).Height,
        _buttons.Controls.Count > 0 ? _buttons.Controls[0].GetPreferredSize(Size.Empty).Height : 0);

    private int CardHeight()
    {
        int s(int n) => LogicalToDeviceUnits(n);
        return s(Pad) + LineHeight + s(2) + LineHeight + s(8) + s(BarHeight) + s(6) + LineHeight + s(8) + BottomRowHeight + s(Pad);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Height = CardHeight();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int s(int n) => LogicalToDeviceUnits(n);
        int pad = s(Pad), width = ClientSize.Width, inner = Math.Max(0, width - 2 * pad), line = LineHeight;

        int y = pad;
        int statusWidth = Math.Min(_status.TextWidth + s(6), inner / 2);
        _status.SetBounds(width - pad - statusWidth, y, statusWidth, line);
        _title.SetBounds(pad, y, Math.Max(0, inner - statusWidth - s(12)), line);
        y += line + s(2);
        _detail.SetBounds(pad, y, inner, line);
        y += line + s(8);
        _bar.SetBounds(pad, y, inner, s(BarHeight));
        y += s(BarHeight) + s(6);
        _stats.SetBounds(pad, y, inner, line);
        y += line + s(8);

        int row = BottomRowHeight;
        var buttons = _buttons.GetPreferredSize(Size.Empty);
        _buttons.SetBounds(width - pad - buttons.Width, y + (row - buttons.Height) / 2, buttons.Width, buttons.Height);
        var slider = _speed.GetPreferredSize(Size.Empty);
        _speed.SetBounds(pad, y + (row - slider.Height) / 2, Math.Min(slider.Width, Math.Max(0, inner - buttons.Width - s(8))),
            slider.Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _title.Font.Dispose(); // the bold one made for it
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Window);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var box = new Rectangle(0, 0, Width - 1, Height - 1);
        var radius = new Size(LogicalToDeviceUnits(8), LogicalToDeviceUnits(8));
        using var fill = new SolidBrush(Theme.Card);
        using var border = new Pen(Theme.CardBorder);
        g.FillRoundedRectangle(fill, box, radius);
        g.DrawRoundedRectangle(border, box, radius);
    }
}
