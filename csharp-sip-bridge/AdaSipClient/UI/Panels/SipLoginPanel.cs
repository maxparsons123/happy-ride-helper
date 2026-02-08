using AdaSipClient.Core;

namespace AdaSipClient.UI.Panels;

/// <summary>
/// SIP server credentials + connect/disconnect.
/// Self-contained panel — reads/writes AppState.
/// </summary>
public sealed class SipLoginPanel : UserControl
{
    private readonly AppState _state;
    private readonly TextBox _txtServer, _txtPort, _txtUser, _txtPassword;
    private readonly ComboBox _cmbTransport;
    private readonly Button _btnConnect;
    private readonly Label _lblStatus;

    public event Func<Task>? OnConnectRequested;
    public event Action? OnDisconnectRequested;

    public SipLoginPanel(AppState state)
    {
        _state = state;
        BackColor = Theme.PanelBg;
        Padding = new Padding(12);

        // ── Title ──
        var title = Theme.StyledLabel("📞 SIP Connection");
        title.Font = Theme.Header;
        title.Dock = DockStyle.Top;

        // ── Fields ──
        var fieldsPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 80,
            ColumnCount = 6,
            RowCount = 2,
            AutoSize = false
        };
        fieldsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));  // label
        fieldsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));   // server
        fieldsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 65));  // port
        fieldsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));  // transport
        fieldsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));  // label
        fieldsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));   // password

        _txtServer = Theme.StyledInput("sip.example.com");
        _txtPort = Theme.StyledInput("5060");
        _txtPort.Width = 60;
        _txtUser = Theme.StyledInput("Extension");
        _txtPassword = Theme.StyledInput("Password");
        _txtPassword.UseSystemPasswordChar = true;

        _cmbTransport = new ComboBox
        {
            BackColor = Theme.InputBg,
            ForeColor = Theme.TextPrimary,
            Font = Theme.Body,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 70
        };
        _cmbTransport.Items.AddRange(new object[] { "UDP", "TCP", "TLS" });
        _cmbTransport.SelectedIndex = 0;

        // Row 0: Server | Port | Transport
        fieldsPanel.Controls.Add(Theme.StyledLabel("Server:"), 0, 0);
        fieldsPanel.Controls.Add(_txtServer, 1, 0);
        fieldsPanel.Controls.Add(_txtPort, 2, 0);
        fieldsPanel.Controls.Add(_cmbTransport, 3, 0);

        // Row 1: User | Password
        fieldsPanel.Controls.Add(Theme.StyledLabel("User:"), 0, 1);
        fieldsPanel.Controls.Add(_txtUser, 1, 1);
        fieldsPanel.Controls.Add(Theme.StyledLabel("Password:"), 4, 1);
        fieldsPanel.Controls.Add(_txtPassword, 5, 1);

        // ── Bottom bar ──
        var bottomBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        bottomBar.BackColor = Theme.PanelBg;

        _btnConnect = Theme.StyledButton("▶  Connect", Theme.AccentGreen);
        _btnConnect.Width = 130;
        _btnConnect.Click += async (_, _) => await HandleConnect();

        _lblStatus = Theme.StyledLabel("● Disconnected", Theme.TextSecondary);
        _lblStatus.Margin = new Padding(12, 8, 0, 0);

        bottomBar.Controls.Add(_btnConnect);
        bottomBar.Controls.Add(_lblStatus);

        // ── Assembly ──
        Controls.Add(bottomBar);
        Controls.Add(fieldsPanel);
        Controls.Add(title);
    }

    private async Task HandleConnect()
    {
        if (_state.IsRegistered)
        {
            OnDisconnectRequested?.Invoke();
            SetDisconnectedUI();
            return;
        }

        // Push to state
        _state.SipServer = _txtServer.Text.Trim();
        _state.SipPort = int.TryParse(_txtPort.Text, out var p) ? p : 5060;
        _state.SipUser = _txtUser.Text.Trim();
        _state.SipPassword = _txtPassword.Text;
        _state.Transport = _cmbTransport.SelectedItem?.ToString() ?? "UDP";

        _btnConnect.Enabled = false;
        _lblStatus.Text = "● Connecting...";
        _lblStatus.ForeColor = Theme.TextWarning;

        try
        {
            if (OnConnectRequested != null)
                await OnConnectRequested.Invoke();

            SetConnectedUI();
        }
        catch (Exception ex)
        {
            _lblStatus.Text = $"● Error: {ex.Message}";
            _lblStatus.ForeColor = Theme.TextError;
            _btnConnect.Enabled = true;
        }
    }

    private void SetConnectedUI()
    {
        _btnConnect.Text = "■  Disconnect";
        _btnConnect.BackColor = Theme.AccentRed;
        _btnConnect.Enabled = true;
        _lblStatus.Text = $"● Registered as {_state.SipUser}";
        _lblStatus.ForeColor = Theme.TextSuccess;
    }

    private void SetDisconnectedUI()
    {
        _btnConnect.Text = "▶  Connect";
        _btnConnect.BackColor = Theme.AccentGreen;
        _btnConnect.Enabled = true;
        _lblStatus.Text = "● Disconnected";
        _lblStatus.ForeColor = Theme.TextSecondary;
    }

    public void LoadFromState()
    {
        _txtServer.Text = _state.SipServer;
        _txtPort.Text = _state.SipPort.ToString();
        _txtUser.Text = _state.SipUser;
        _txtPassword.Text = _state.SipPassword;
        var idx = _cmbTransport.Items.IndexOf(_state.Transport);
        _cmbTransport.SelectedIndex = idx >= 0 ? idx : 0;
    }
}
