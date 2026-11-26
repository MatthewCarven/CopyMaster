using CopyMaster.Engine.Control;

namespace CopyMaster;

/// <summary>
/// The speed limit slider: 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128 MB/s, then
/// Unlimited, with the current value written beside it. Raises <see cref="LimitChanged"/>
/// as it moves (dragging, keys or the wheel), so a running job slows or speeds up at once.
/// </summary>
internal sealed class SpeedSlider : UserControl
{
    private readonly TrackBar _track;
    private readonly Label _caption;
    private readonly TextLine _value;
    private bool _setting;

    public SpeedSlider(string caption = "Limit")
    {
        _caption = new Label { Text = caption, AutoSize = true, UseMnemonic = false };
        _track = new TrackBar
        {
            Minimum = 0,
            Maximum = SpeedStops.Count - 1,
            TickFrequency = 1,
            SmallChange = 1,
            LargeChange = 1,
            TickStyle = TickStyle.None,
            AutoSize = false,
            Value = SpeedStops.Unlimited,
        };
        _value = new TextLine();
        _track.ValueChanged += (_, _) =>
        {
            ShowValue();
            if (!_setting) LimitChanged?.Invoke(Limit);
        };
        Controls.AddRange([_caption, _track, _value]);
        ShowValue();
    }

    /// <summary>The new limit in bytes per second, or null for Unlimited.</summary>
    public event Action<long?>? LimitChanged;

    /// <summary>Bytes per second, or null for Unlimited. Setting it doesn't raise <see cref="LimitChanged"/>.</summary>
    public long? Limit
    {
        get => SpeedStops.BytesPerSecond(_track.Value);
        set
        {
            int position = SpeedStops.PositionOf(value);
            if (position == _track.Value) return;
            _setting = true;
            try
            {
                _track.Value = position;
            }
            finally
            {
                _setting = false;
            }
        }
    }

    /// <summary>The slider's width, in logical (96 dpi) pixels.</summary>
    public int TrackWidth { get; set; } = 170;

    private void ShowValue() => _value.Show(SpeedStops.Describe(Limit), ForeColor);

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        _track.BackColor = BackColor;
        _value.BackColor = BackColor;
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        ShowValue();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int valueWidth = TextRenderer.MeasureText("128 MB/s", Font).Width;
        valueWidth = Math.Max(valueWidth, TextRenderer.MeasureText("Unlimited", Font).Width);
        int width = _caption.PreferredWidth + LogicalToDeviceUnits(4) + LogicalToDeviceUnits(TrackWidth) +
                    LogicalToDeviceUnits(4) + valueWidth;
        return new Size(width, Math.Max(LogicalToDeviceUnits(28), _caption.PreferredHeight));
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int height = ClientSize.Height;
        int gap = LogicalToDeviceUnits(4);
        int x = 0;
        _caption.SetBounds(x, (height - _caption.PreferredHeight) / 2, _caption.PreferredWidth, _caption.PreferredHeight);
        x += _caption.PreferredWidth + gap;
        int trackWidth = LogicalToDeviceUnits(TrackWidth);
        _track.SetBounds(x, 0, trackWidth, height);
        x += trackWidth + gap;
        _value.SetBounds(x, 0, Math.Max(0, ClientSize.Width - x), height);
    }
}
