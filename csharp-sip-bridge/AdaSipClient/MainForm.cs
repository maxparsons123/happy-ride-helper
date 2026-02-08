using AdaSipClient.Config;
using AdaSipClient.Core;
using AdaSipClient.Sip;
using AdaSipClient.UI;
using AdaSipClient.UI.Panels;

namespace AdaSipClient;

/// <summary>
/// Main application form — assembles panels, wires events.
/// No business logic lives here; panels and services own that.
/// v2.0 — Full SIP + audio pipeline + settings persistence.
/// </summary>
public sealed class MainForm : Form
{
    private const string AppVersion = "2.0.0";

    // ── Core ──
    private readonly AppState _state = new();
    private readonly LogPanel _logPanel;
    private ISipService? _sip;
    private CallHandler? _callHandler;

    // ── UI Panels ──
    private readonly SipLoginPanel _sipPanel;
    private readonly CallControlPanel _callPanel;
    private readonly VolumePanel _volumePanel;
    private readonly AvatarPanel _avatarPanel;

    public MainForm()
    {
        // ── Load saved settings ──
        SettingsStore.Load(_state);

        // ── Form setup ──
        Text = $"Ada SIP Client v{AppVersion}";
        Size = new Size(900, 700);
        MinimumSize = new Size(780, 580);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.FormBg;
        Font = Theme.Body;
        DoubleBuffered = true;

        // ── Create panels ──
        _logPanel = new LogPanel();
        _sipPanel = new SipLoginPanel(_state);
        _callPanel = new CallControlPanel(_state);
        _volumePanel = new VolumePanel(_state);
        _avatarPanel = new AvatarPanel();

        // ── Layout ──
        var mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 320,
            BackColor = Theme.FormBg,
            Panel1MinSize = 250,
            Panel2MinSize = 120
        };

        var topSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 560,
            BackColor = Theme.FormBg,
            Panel2MinSize = 180
        };

        var leftStack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Theme.FormBg
        };

        _sipPanel.Width = 540;
        _sipPanel.Height = 140;
        _callPanel.Width = 540;
        _callPanel.Height = 110;
        _volumePanel.Width = 540;
        _volumePanel.Height = 90;

        leftStack.Controls.Add(_sipPanel);
        leftStack.Controls.Add(_callPanel);
        leftStack.Controls.Add(_volumePanel);

        leftStack.Resize += (_, _) =>
        {
            int w = leftStack.ClientSize.Width - 6;
            _sipPanel.Width = w;
            _callPanel.Width = w;
            _volumePanel.Width = w;
        };

        topSplit.Panel1.Controls.Add(leftStack);

        _avatarPanel.Dock = DockStyle.Fill;
        topSplit.Panel2.Controls.Add(_avatarPanel);

        _logPanel.Dock = DockStyle.Fill;
        mainSplit.Panel1.Controls.Add(topSplit);
        mainSplit.Panel2.Controls.Add(_logPanel);

        Controls.Add(mainSplit);

        // ── Populate UI from loaded state ──
        _sipPanel.LoadFromState();

        // ── Wire events ──
        _sipPanel.OnConnectRequested += HandleConnect;
        _sipPanel.OnDisconnectRequested += HandleDisconnect;
        _callPanel.OnAnswerCall += HandleAnswerCall;
        _callPanel.OnRejectCall += () => _sip?.RejectCall();
        _callPanel.OnHangUp += HandleHangUp;

        // ── Save settings on any state change ──
        _state.StateChanged += () => SettingsStore.Save(_state);

        // ── Log startup ──
        _logPanel.Log($"Ada SIP Client v{AppVersion} ready");
        _logPanel.Log($"Mode: {_state.Mode}");
    }

    private async Task HandleConnect()
    {
        _sip?.Dispose();
        _callHandler?.Dispose();

        _sip = new SipService(_logPanel);
        _callHandler = new CallHandler(_state, _sip, _logPanel);

        _sip.OnIncomingCall += caller =>
        {
            BeginInvoke(() =>
            {
                _state.CallerNumber = caller;
                _callPanel.ShowIncomingCall(caller);
                _avatarPanel.SetStatus("Incoming call...", Theme.TextWarning);
            });
        };

        _sip.OnCallEnded += () =>
        {
            BeginInvoke(() =>
            {
                _callHandler?.StopPipeline();
                _callPanel.ShowIdle();
                _avatarPanel.SetStatus("Waiting for call...");
            });
        };

        await _sip.RegisterAsync(
            _state.SipServer,
            _state.SipPort,
            _state.SipUser,
            _state.SipPassword,
            _state.Transport
        );

        _state.IsRegistered = true;
        _state.NotifyChanged();
    }

    private async void HandleAnswerCall()
    {
        _sip?.AnswerCall();
        _state.IsInCall = true;
        _callPanel.ShowActiveCall(_state.CallerNumber ?? "Unknown");
        _avatarPanel.SetStatus("In call", Theme.TextSuccess);

        // Start the appropriate audio pipeline
        if (_callHandler != null)
            await _callHandler.StartPipelineAsync();
    }

    private void HandleHangUp()
    {
        _callHandler?.StopPipeline();
        _sip?.HangUp();
        _state.IsInCall = false;
        _callPanel.ShowIdle();
        _avatarPanel.SetStatus("Waiting for call...");
    }

    private void HandleDisconnect()
    {
        _callHandler?.Dispose();
        _callHandler = null;
        _sip?.Dispose();
        _sip = null;
        _state.IsRegistered = false;
        _state.NotifyChanged();
        _callPanel.ShowIdle();
        _avatarPanel.SetStatus("Disconnected");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SettingsStore.Save(_state);
        _callHandler?.Dispose();
        _sip?.Dispose();
        base.OnFormClosing(e);
    }
}
