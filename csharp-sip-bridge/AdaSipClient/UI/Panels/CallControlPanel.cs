using AdaSipClient.Core;

namespace AdaSipClient.UI.Panels;

/// <summary>
/// Call mode selector (Auto Bot / Manual Listen) + answer/reject/hangup buttons.
/// </summary>
public sealed class CallControlPanel : UserControl
{
    private readonly AppState _state;
    private readonly RadioButton _rbAutoBot, _rbManual;
    private readonly Button _btnAnswer, _btnReject, _btnHangUp;
    private readonly Label _lblCallInfo;

    public event Action? OnAnswerCall;
    public event Action? OnRejectCall;
    public event Action? OnHangUp;

    public CallControlPanel(AppState state)
    {
        _state = state;
        BackColor = Theme.PanelBg;
        Padding = new Padding(12);
        Height = 110;

        var title = Theme.StyledLabel("🎧 Call Mode");
        title.Font = Theme.Header;
        title.Location = new Point(12, 8);

        // ── Mode selection ──
        _rbAutoBot = new RadioButton
        {
            Text = "🤖 Auto → Bot",
            ForeColor = Theme.AccentBlue,
            Font = Theme.BodyBold,
            Location = new Point(14, 34),
            Size = new Size(130, 22),
            Checked = _state.Mode == CallMode.AutoBot
        };
        _rbAutoBot.CheckedChanged += (_, _) =>
        {
            if (_rbAutoBot.Checked) _state.Mode = CallMode.AutoBot;
            _state.NotifyChanged();
        };

        _rbManual = new RadioButton
        {
            Text = "🎤 Manual Listen",
            ForeColor = Theme.AccentPurple,
            Font = Theme.BodyBold,
            Location = new Point(150, 34),
            Size = new Size(140, 22),
            Checked = _state.Mode == CallMode.ManualListen
        };
        _rbManual.CheckedChanged += (_, _) =>
        {
            if (_rbManual.Checked) _state.Mode = CallMode.ManualListen;
            _state.NotifyChanged();
        };

        // ── Call buttons ──
        _btnAnswer = Theme.StyledButton("✅ Answer", Theme.AccentGreen);
        _btnAnswer.Location = new Point(14, 64);
        _btnAnswer.Width = 100;
        _btnAnswer.Visible = false;
        _btnAnswer.Click += (_, _) => OnAnswerCall?.Invoke();

        _btnReject = Theme.StyledButton("❌ Reject", Theme.AccentRed);
        _btnReject.Location = new Point(120, 64);
        _btnReject.Width = 100;
        _btnReject.Visible = false;
        _btnReject.Click += (_, _) => OnRejectCall?.Invoke();

        _btnHangUp = Theme.StyledButton("📴 Hang Up", Theme.AccentRed);
        _btnHangUp.Location = new Point(14, 64);
        _btnHangUp.Width = 110;
        _btnHangUp.Visible = false;
        _btnHangUp.Click += (_, _) => OnHangUp?.Invoke();

        _lblCallInfo = Theme.StyledLabel("No active call", Theme.TextSecondary);
        _lblCallInfo.Location = new Point(240, 70);

        Controls.AddRange(new Control[]
        {
            title, _rbAutoBot, _rbManual,
            _btnAnswer, _btnReject, _btnHangUp, _lblCallInfo
        });
    }

    public void ShowIncomingCall(string caller)
    {
        _lblCallInfo.Text = $"📞 Incoming: {caller}";
        _lblCallInfo.ForeColor = Theme.TextWarning;

        if (_state.Mode == CallMode.AutoBot)
        {
            // Auto-answer — no buttons needed
            OnAnswerCall?.Invoke();
        }
        else
        {
            _btnAnswer.Visible = true;
            _btnReject.Visible = true;
            _btnHangUp.Visible = false;
        }
    }

    public void ShowActiveCall(string caller)
    {
        _lblCallInfo.Text = $"🔊 In call: {caller}";
        _lblCallInfo.ForeColor = Theme.TextSuccess;
        _btnAnswer.Visible = false;
        _btnReject.Visible = false;
        _btnHangUp.Visible = true;
    }

    public void ShowIdle()
    {
        _lblCallInfo.Text = "No active call";
        _lblCallInfo.ForeColor = Theme.TextSecondary;
        _btnAnswer.Visible = false;
        _btnReject.Visible = false;
        _btnHangUp.Visible = false;
    }
}
