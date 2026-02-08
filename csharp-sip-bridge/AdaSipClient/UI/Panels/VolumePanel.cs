using AdaSipClient.Core;

namespace AdaSipClient.UI.Panels;

/// <summary>
/// Input/output volume sliders with percentage readout.
/// </summary>
public sealed class VolumePanel : UserControl
{
    private readonly AppState _state;
    private readonly TrackBar _sliderIn, _sliderOut;
    private readonly Label _lblInVal, _lblOutVal;

    public VolumePanel(AppState state)
    {
        _state = state;
        BackColor = Theme.PanelBg;
        Padding = new Padding(12);
        Height = 90;

        var title = Theme.StyledLabel("🔊 Volume");
        title.Font = Theme.Header;
        title.Location = new Point(12, 4);

        // ── Input (mic / caller) ──
        var lblIn = Theme.StyledLabel("🎙 In:", Theme.TextSecondary);
        lblIn.Location = new Point(12, 32);

        _sliderIn = CreateSlider(_state.InputVolumePercent);
        _sliderIn.Location = new Point(55, 28);
        _sliderIn.Width = 160;

        _lblInVal = Theme.StyledLabel($"{_state.InputVolumePercent}%", Theme.TextPrimary);
        _lblInVal.Location = new Point(220, 32);

        _sliderIn.ValueChanged += (_, _) =>
        {
            _state.InputVolumePercent = _sliderIn.Value;
            _lblInVal.Text = $"{_sliderIn.Value}%";
        };

        // ── Output (speaker / to caller) ──
        var lblOut = Theme.StyledLabel("🔈 Out:", Theme.TextSecondary);
        lblOut.Location = new Point(12, 58);

        _sliderOut = CreateSlider(_state.OutputVolumePercent);
        _sliderOut.Location = new Point(55, 54);
        _sliderOut.Width = 160;

        _lblOutVal = Theme.StyledLabel($"{_state.OutputVolumePercent}%", Theme.TextPrimary);
        _lblOutVal.Location = new Point(220, 58);

        _sliderOut.ValueChanged += (_, _) =>
        {
            _state.OutputVolumePercent = _sliderOut.Value;
            _lblOutVal.Text = $"{_sliderOut.Value}%";
        };

        Controls.AddRange(new Control[]
        {
            title, lblIn, _sliderIn, _lblInVal,
            lblOut, _sliderOut, _lblOutVal
        });
    }

    private static TrackBar CreateSlider(int value)
    {
        return new TrackBar
        {
            Minimum = 0,
            Maximum = 200,
            Value = Math.Clamp(value, 0, 200),
            TickFrequency = 25,
            SmallChange = 5,
            LargeChange = 25,
            BackColor = Theme.PanelBg
        };
    }
}
