namespace AdaMain;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
            components.Dispose();
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private void InitializeComponent()
    {
        this.components = new System.ComponentModel.Container();
        this.SuspendLayout();

        // ── Colours ──
        var bgDark   = Color.FromArgb(30, 30, 30);
        var bgPanel  = Color.FromArgb(45, 45, 48);
        var bgInput  = Color.FromArgb(60, 60, 65);
        var fgLight  = Color.FromArgb(220, 220, 220);
        var accent   = Color.FromArgb(0, 122, 204);   // blue accent
        var green    = Color.FromArgb(40, 167, 69);
        var red      = Color.FromArgb(220, 53, 69);
        var orange   = Color.FromArgb(255, 152, 0);
        var purple   = Color.FromArgb(156, 39, 176);

        // ══════════════════════════════════════════
        //  MENU STRIP
        // ══════════════════════════════════════════
        menuStrip = new MenuStrip { BackColor = bgPanel, ForeColor = fgLight };

        var mnuFile = new ToolStripMenuItem("&File");
        mnuSettings = new ToolStripMenuItem("⚙ &Settings…");
        mnuSettings.Click += mnuSettings_Click;
        var mnuSep1 = new ToolStripSeparator();
        mnuExit = new ToolStripMenuItem("Exit");
        mnuExit.Click += (s, e) => Close();
        mnuFile.DropDownItems.AddRange(new ToolStripItem[] { mnuSettings, mnuSep1, mnuExit });

        var mnuTools = new ToolStripMenuItem("&Tools");
        mnuNewBooking = new ToolStripMenuItem("📋 New &Booking…");
        mnuNewBooking.ShortcutKeys = Keys.Control | Keys.B;
        mnuNewBooking.Click += mnuNewBooking_Click;
        mnuAudioTest = new ToolStripMenuItem("🎤 Audio Test");
        mnuAudioTest.Click += mnuAudioTest_Click;
        mnuViewConfig = new ToolStripMenuItem("📄 View Config File");
        mnuViewConfig.Click += mnuViewConfig_Click;
        mnuTools.DropDownItems.AddRange(new ToolStripItem[] { mnuNewBooking, new ToolStripSeparator(), mnuAudioTest, mnuViewConfig });

        var mnuHelp = new ToolStripMenuItem("&Help");
        var mnuAbout = new ToolStripMenuItem("About");
        mnuAbout.Click += (s, e) => MessageBox.Show("AdaMain v1.0\nVoice AI Taxi Bridge", "About", MessageBoxButtons.OK, MessageBoxIcon.Information);
        mnuHelp.DropDownItems.Add(mnuAbout);

        menuStrip.Items.AddRange(new ToolStripItem[] { mnuFile, mnuTools, mnuHelp });

        // ══════════════════════════════════════════
        //  SIP REGISTRATION GROUP
        // ══════════════════════════════════════════
        grpSip = new GroupBox
        {
            Text = "📞 SIP Registration",
            Location = new Point(12, 30),
            Size = new Size(560, 165),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = fgLight,
            BackColor = bgPanel
        };

        // Row 1: Server / Port / Transport
        var lblServer = MakeLabel("Server:", 15, 25);
        txtSipServer = MakeTextBox(85, 22, 190, bgInput, fgLight);
        txtSipServer.PlaceholderText = "sip.example.com";

        var lblPort = MakeLabel("Port:", 290, 25);
        txtSipPort = MakeTextBox(330, 22, 60, bgInput, fgLight);
        txtSipPort.Text = "5060";

        var lblTransport = MakeLabel("Transport:", 405, 25);
        cmbTransport = new ComboBox { Location = new Point(475, 22), Size = new Size(70, 23), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = bgInput, ForeColor = fgLight };
        cmbTransport.Items.AddRange(new object[] { "UDP", "TCP", "TLS" });
        cmbTransport.SelectedIndex = 0;

        // Row 2: Extension / Auth ID / Password
        var lblUser = MakeLabel("Extension:", 15, 58);
        txtSipUser = MakeTextBox(85, 55, 80, bgInput, fgLight);
        txtSipUser.PlaceholderText = "e.g. 300";

        var lblAuthId = MakeLabel("Auth ID:", 180, 58);
        txtAuthId = MakeTextBox(240, 55, 100, bgInput, fgLight);
        txtAuthId.PlaceholderText = "(optional)";

        var lblPassword = MakeLabel("Password:", 355, 58);
        txtSipPassword = MakeTextBox(425, 55, 120, bgInput, fgLight);
        txtSipPassword.UseSystemPasswordChar = true;

        // Row 3: Domain / Auto-Answer
        var lblDomain = MakeLabel("Domain:", 15, 91);
        txtDomain = MakeTextBox(85, 88, 190, bgInput, fgLight);
        txtDomain.PlaceholderText = "(optional override)";

        chkAutoAnswer = new CheckBox { Text = "Auto-Answer", Location = new Point(290, 90), Size = new Size(110, 23), ForeColor = fgLight, Checked = true };

        // Row 4: Connect button
        btnConnect = MakeButton("▶ Connect", 15, 122, 120, 32, green);
        btnConnect.Click += btnConnect_Click;

        btnDisconnect = MakeButton("■ Disconnect", 145, 122, 120, 32, red);
        btnDisconnect.Enabled = false;
        btnDisconnect.Click += btnDisconnect_Click;

        lblSipStatus = new Label { Text = "● Disconnected", Location = new Point(280, 128), Size = new Size(260, 20), ForeColor = Color.Gray, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };

        grpSip.Controls.AddRange(new Control[] {
            lblServer, txtSipServer, lblPort, txtSipPort, lblTransport, cmbTransport,
            lblUser, txtSipUser, lblAuthId, txtAuthId, lblPassword, txtSipPassword,
            lblDomain, txtDomain, chkAutoAnswer,
            btnConnect, btnDisconnect, lblSipStatus
        });

        // ══════════════════════════════════════════
        //  CALL CONTROLS GROUP
        // ══════════════════════════════════════════
        grpCall = new GroupBox
        {
            Text = "🎧 Call Controls",
            Location = new Point(12, 200),
            Size = new Size(560, 95),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = fgLight,
            BackColor = bgPanel
        };

        btnAnswer = MakeButton("✅ Answer", 15, 25, 100, 30, green);
        btnAnswer.Enabled = false;
        btnAnswer.Click += btnAnswer_Click;

        btnReject = MakeButton("❌ Reject", 125, 25, 100, 30, red);
        btnReject.Enabled = false;
        btnReject.Click += btnReject_Click;

        btnHangUp = MakeButton("📴 Hang Up", 235, 25, 100, 30, Color.FromArgb(180, 50, 50));
        btnHangUp.Enabled = false;
        btnHangUp.Click += btnHangUp_Click;

        btnMute = MakeButton("🔊 Mute", 350, 25, 90, 30, Color.FromArgb(80, 80, 85));
        btnMute.Enabled = false;
        btnMute.Click += btnMute_Click;

        chkManualMode = new CheckBox { Text = "🎤 Operator Mode", Location = new Point(15, 62), Size = new Size(140, 23), ForeColor = orange, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        chkManualMode.CheckedChanged += chkManualMode_CheckedChanged;

        btnPtt = MakeButton("🎙 Push-to-Talk", 165, 58, 130, 30, purple);
        btnPtt.Enabled = false;
        btnPtt.Visible = false;
        btnPtt.MouseDown += btnPtt_MouseDown;
        btnPtt.MouseUp += btnPtt_MouseUp;

        // Operator mic volume slider
        lblOpVolume = new Label { Text = "🔊 Mic Vol:", Location = new Point(310, 62), Size = new Size(70, 20), ForeColor = fgLight, Font = new Font("Segoe UI", 8F) };
        trkOpVolume = new TrackBar
        {
            Location = new Point(380, 58),
            Size = new Size(120, 30),
            Minimum = 10,    // 1.0x
            Maximum = 60,    // 6.0x
            Value = 20,      // 2.0x default
            TickFrequency = 10,
            SmallChange = 5,
            LargeChange = 10,
            BackColor = bgPanel
        };
        trkOpVolume.ValueChanged += (s, e) =>
        {
            _operatorMicGain = trkOpVolume.Value / 10f;
            lblOpVolumeVal.Text = $"{_operatorMicGain:F1}x";
        };
        lblOpVolumeVal = new Label { Text = "2.0x", Location = new Point(500, 62), Size = new Size(40, 20), ForeColor = Color.LimeGreen, Font = new Font("Segoe UI", 8F, FontStyle.Bold) };

        lblCallInfo = new Label { Text = "No active call", Location = new Point(310, 82), Size = new Size(240, 15), ForeColor = Color.Gray, Font = new Font("Segoe UI", 8F) };

        grpCall.Controls.AddRange(new Control[] { btnAnswer, btnReject, btnHangUp, btnMute, chkManualMode, btnPtt, lblOpVolume, trkOpVolume, lblOpVolumeVal, lblCallInfo });

        // ══════════════════════════════════════════
        //  SIMLI AVATAR GROUP (right side)
        // ══════════════════════════════════════════
        grpAvatar = new GroupBox
        {
            Text = "🎭 Simli Avatar",
            Location = new Point(580, 30),
            Size = new Size(200, 265),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            ForeColor = fgLight,
            BackColor = bgPanel
        };

        // SimliAvatar placeholder panel — actual SimliAvatar is created in MainForm.cs
        // because it requires ILogger which needs the logger factory.
        pnlAvatarHost = new Panel
        {
            Location = new Point(10, 22),
            Size = new Size(180, 200),
            BackColor = Color.Black,
            BorderStyle = BorderStyle.FixedSingle
        };

        lblAvatarStatus = new Label
        {
            Text = "Not connected",
            Location = new Point(10, 228),
            Size = new Size(180, 20),
            ForeColor = Color.Gray,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 8F)
        };

        grpAvatar.Controls.AddRange(new Control[] { pnlAvatarHost, lblAvatarStatus });

        // ══════════════════════════════════════════
        //  LOGS PANEL
        // ══════════════════════════════════════════
        grpLogs = new GroupBox
        {
            Text = "📋 Logs & Transcripts",
            Location = new Point(12, 302),
            Size = new Size(768, 258),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = fgLight,
            BackColor = bgPanel
        };

        lstLogs = new ListBox
        {
            Location = new Point(10, 22),
            Size = new Size(748, 225),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Font = new Font("Consolas", 9F),
            BackColor = Color.FromArgb(20, 20, 20),
            ForeColor = Color.LightGreen,
            BorderStyle = BorderStyle.None,
            SelectionMode = SelectionMode.MultiExtended
        };
        lstLogs.KeyDown += lstLogs_KeyDown;

        // Context menu for logs
        ctxLogs = new ContextMenuStrip(this.components);
        var mnuCopySelected = new ToolStripMenuItem("Copy Selected");
        mnuCopySelected.Click += (s, e) => CopySelectedLogs();
        var mnuCopyAll = new ToolStripMenuItem("Copy All");
        mnuCopyAll.Click += (s, e) => CopyAllLogs();
        var mnuClearLogs = new ToolStripMenuItem("Clear");
        mnuClearLogs.Click += (s, e) => lstLogs.Items.Clear();
        ctxLogs.Items.AddRange(new ToolStripItem[] { mnuCopySelected, mnuCopyAll, new ToolStripSeparator(), mnuClearLogs });
        lstLogs.ContextMenuStrip = ctxLogs;

        btnClearLogs = MakeButton("Clear", 10, 252, 80, 26, Color.FromArgb(108, 117, 125));
        btnClearLogs.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        btnClearLogs.Click += (s, e) => lstLogs.Items.Clear();

        btnCopyLogs = MakeButton("📋 Copy", 100, 252, 80, 26, accent);
        btnCopyLogs.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        btnCopyLogs.Click += (s, e) => CopyAllLogs();

        grpLogs.Controls.AddRange(new Control[] { lstLogs, btnClearLogs, btnCopyLogs });

        // ══════════════════════════════════════════
        //  STATUS BAR
        // ══════════════════════════════════════════
        statusStrip = new StatusStrip { BackColor = bgPanel, ForeColor = fgLight };
        statusLabel = new ToolStripStatusLabel("Ready");
        statusCallId = new ToolStripStatusLabel("") { Spring = true, TextAlign = ContentAlignment.MiddleRight };
        statusStrip.Items.AddRange(new ToolStripItem[] { statusLabel, statusCallId });

        // ══════════════════════════════════════════
        //  FORM
        // ══════════════════════════════════════════
        this.AutoScaleDimensions = new SizeF(7F, 15F);
        this.AutoScaleMode = AutoScaleMode.Font;
        this.ClientSize = new Size(794, 590);
        this.MinimumSize = new Size(700, 520);
        this.Text = "🚕 AdaMain - Voice AI Taxi Bridge";
        this.StartPosition = FormStartPosition.CenterScreen;
        this.Font = new Font("Segoe UI", 9F);
        this.BackColor = bgDark;
        this.ForeColor = fgLight;
        this.MainMenuStrip = menuStrip;

        this.Controls.AddRange(new Control[] { menuStrip, grpSip, grpCall, grpAvatar, grpLogs, statusStrip });

        this.ResumeLayout(false);
        this.PerformLayout();
    }

    #endregion

    // ── Helper methods ──
    private static Label MakeLabel(string text, int x, int y)
        => new Label { Text = text, Location = new Point(x, y), AutoSize = true };

    private static TextBox MakeTextBox(int x, int y, int w, Color bg, Color fg)
        => new TextBox { Location = new Point(x, y), Size = new Size(w, 23), BackColor = bg, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle };

    private static Button MakeButton(string text, int x, int y, int w, int h, Color bg)
    {
        var btn = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, h),
            BackColor = bg,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };
        btn.FlatAppearance.BorderSize = 0;
        return btn;
    }

    // ── Field declarations ──
    // Menu
    private MenuStrip menuStrip;
    private ToolStripMenuItem mnuSettings;
    private ToolStripMenuItem mnuExit;
    private ToolStripMenuItem mnuNewBooking;
    private ToolStripMenuItem mnuAudioTest;
    private ToolStripMenuItem mnuViewConfig;

    // SIP Registration
    private GroupBox grpSip;
    private TextBox txtSipServer;
    private TextBox txtSipPort;
    private TextBox txtSipUser;
    private TextBox txtAuthId;
    private TextBox txtSipPassword;
    private TextBox txtDomain;
    private ComboBox cmbTransport;
    private CheckBox chkAutoAnswer;
    private Button btnConnect;
    private Button btnDisconnect;
    private Label lblSipStatus;

    // Call Controls
    private GroupBox grpCall;
    private Button btnAnswer;
    private Button btnReject;
    private Button btnHangUp;
    private Button btnMute;
    private CheckBox chkManualMode;
    private Button btnPtt;
    private Label lblCallInfo;
    private Label lblOpVolume;
    private TrackBar trkOpVolume;
    private Label lblOpVolumeVal;

    // Avatar
    private GroupBox grpAvatar;
    private Panel pnlAvatarHost;
    private Label lblAvatarStatus;

    // Logs
    private GroupBox grpLogs;
    private ListBox lstLogs;
    private ContextMenuStrip ctxLogs;
    private Button btnClearLogs;
    private Button btnCopyLogs;

    // Status
    private StatusStrip statusStrip;
    private ToolStripStatusLabel statusLabel;
    private ToolStripStatusLabel statusCallId;
}
