namespace AdaCleanVersion;

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

        var bgDark = Color.FromArgb(30, 30, 30);
        var bgPanel = Color.FromArgb(45, 45, 48);
        var bgInput = Color.FromArgb(60, 60, 65);
        var fgLight = Color.FromArgb(220, 220, 220);
        var accent = Color.FromArgb(0, 122, 204);
        var green = Color.FromArgb(40, 167, 69);
        var red = Color.FromArgb(220, 53, 69);
        var orange = Color.FromArgb(255, 152, 0);

        // ══════════════════════════════════════
        //  MENU STRIP
        // ══════════════════════════════════════
        menuStrip = new MenuStrip { BackColor = bgPanel, ForeColor = fgLight };

        var mnuFile = new ToolStripMenuItem("&File");
        mnuSettings = new ToolStripMenuItem("⚙ &Settings…");
        mnuSettings.Click += mnuSettings_Click;
        mnuExit = new ToolStripMenuItem("Exit");
        mnuExit.Click += (s, e) => Close();
        mnuFile.DropDownItems.AddRange(new ToolStripItem[] { mnuSettings, new ToolStripSeparator(), mnuExit });

        var mnuTools = new ToolStripMenuItem("&Tools");
        mnuViewConfig = new ToolStripMenuItem("📄 View Config File");
        mnuViewConfig.Click += mnuViewConfig_Click;
        mnuTools.DropDownItems.Add(mnuViewConfig);

        var mnuHelp = new ToolStripMenuItem("&Help");
        var mnuAbout = new ToolStripMenuItem("About");
        mnuAbout.Click += (s, e) => MessageBox.Show("AdaCleanVersion v1.0\nDeterministic Booking Engine + Voice AI Bridge", "About", MessageBoxButtons.OK, MessageBoxIcon.Information);
        mnuHelp.DropDownItems.Add(mnuAbout);

        menuStrip.Items.AddRange(new ToolStripItem[] { mnuFile, mnuTools, mnuHelp });

        // ══════════════════════════════════════
        //  SIP REGISTRATION GROUP
        // ══════════════════════════════════════
        grpSip = new GroupBox
        {
            Text = "📞 SIP Registration",
            Location = new Point(12, 30),
            Size = new Size(560, 228),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = fgLight,
            BackColor = bgPanel
        };

        // Row 0: Account selector
        grpSip.Controls.Add(MakeLabel("Account:", 15, 25));
        cmbSipAccount = new ComboBox
        {
            Location = new Point(85, 22), Size = new Size(250, 23),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = bgInput, ForeColor = fgLight
        };
        cmbSipAccount.SelectedIndexChanged += cmbSipAccount_SelectedIndexChanged;

        btnSaveAccount = MakeButton("💾 Save", 345, 20, 65, 26, accent);
        btnSaveAccount.Click += btnSaveAccount_Click;
        btnSaveAccount.Font = new Font("Segoe UI", 8F, FontStyle.Bold);

        btnDeleteAccount = MakeButton("🗑", 415, 20, 35, 26, red);
        btnDeleteAccount.Click += btnDeleteAccount_Click;
        btnDeleteAccount.Font = new Font("Segoe UI", 8F, FontStyle.Bold);

        btnNewAccount = MakeButton("+ New", 455, 20, 60, 26, Color.FromArgb(80, 80, 85));
        btnNewAccount.Click += btnNewAccount_Click;
        btnNewAccount.Font = new Font("Segoe UI", 8F, FontStyle.Bold);

        // Row 1: Server / Port / Transport
        grpSip.Controls.Add(MakeLabel("SIP Server:", 15, 55));
        txtSipServer = MakeTextBox(95, 52, 180, bgInput, fgLight);
        txtSipServer.PlaceholderText = "sip.example.com";

        grpSip.Controls.Add(MakeLabel("Port:", 290, 55));
        txtSipPort = MakeTextBox(330, 52, 55, bgInput, fgLight);
        txtSipPort.Text = "5060";

        grpSip.Controls.Add(MakeLabel("Transport:", 395, 55));
        cmbTransport = new ComboBox { Location = new Point(465, 52), Size = new Size(85, 23), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = bgInput, ForeColor = fgLight };
        cmbTransport.Items.AddRange(new object[] { "UDP", "TCP" });
        cmbTransport.SelectedIndex = 0;

        // Row 2: Extension / Auth ID / Password
        grpSip.Controls.Add(MakeLabel("Extension:", 15, 88));
        txtSipUser = MakeTextBox(95, 85, 80, bgInput, fgLight);

        grpSip.Controls.Add(MakeLabel("Auth ID:", 190, 88));
        txtAuthId = MakeTextBox(250, 85, 90, bgInput, fgLight);
        txtAuthId.PlaceholderText = "(optional)";

        grpSip.Controls.Add(MakeLabel("Password:", 355, 88));
        txtSipPassword = MakeTextBox(425, 85, 120, bgInput, fgLight);
        txtSipPassword.UseSystemPasswordChar = true;

        // Row 3: Domain / Display Name / Auto-Answer
        grpSip.Controls.Add(MakeLabel("Domain:", 15, 121));
        txtDomain = MakeTextBox(95, 118, 140, bgInput, fgLight);
        txtDomain.PlaceholderText = "(optional override)";

        grpSip.Controls.Add(MakeLabel("Display Name:", 250, 121));
        txtDisplayName = MakeTextBox(345, 118, 110, bgInput, fgLight);
        txtDisplayName.PlaceholderText = "(e.g. Ai Agent)";

        chkAutoAnswer = new CheckBox { Text = "Auto-Answer", Location = new Point(470, 120), Size = new Size(110, 23), ForeColor = fgLight, Checked = true };

        // Row 5: Connect / Disconnect
        btnConnect = MakeButton("▶ Connect", 15, 185, 120, 32, green);
        btnConnect.Click += btnConnect_Click;

        btnDisconnect = MakeButton("■ Disconnect", 145, 185, 120, 32, red);
        btnDisconnect.Enabled = false;
        btnDisconnect.Click += btnDisconnect_Click;

        lblSipStatus = new Label { Text = "● Disconnected", Location = new Point(280, 191), Size = new Size(260, 20), ForeColor = Color.Gray, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };

        grpSip.Controls.AddRange(new Control[] {
            cmbSipAccount, btnSaveAccount, btnDeleteAccount, btnNewAccount,
            txtSipServer, txtSipPort, cmbTransport,
            txtSipUser, txtAuthId, txtSipPassword,
            txtDomain, txtDisplayName, chkAutoAnswer,
            btnConnect, btnDisconnect, lblSipStatus
        });

        // ══════════════════════════════════════
        //  CALL CONTROLS GROUP
        // ══════════════════════════════════════
        grpCall = new GroupBox
        {
            Text = "🎧 Call Controls",
            Location = new Point(12, 263),
            Size = new Size(560, 65),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = fgLight,
            BackColor = bgPanel
        };

        btnHangUp = MakeButton("📴 Hang Up", 15, 25, 100, 30, Color.FromArgb(180, 50, 50));
        btnHangUp.Enabled = false;
        btnHangUp.Click += btnHangUp_Click;

        btnMute = MakeButton("🔊 Mute", 125, 25, 90, 30, Color.FromArgb(80, 80, 85));
        btnMute.Enabled = false;
        btnMute.Click += btnMute_Click;

        lblCallInfo = new Label { Text = "No active call", Location = new Point(230, 30), Size = new Size(320, 20), ForeColor = Color.Gray, Font = new Font("Segoe UI", 8F) };

        grpCall.Controls.AddRange(new Control[] { btnHangUp, btnMute, lblCallInfo });

        // ══════════════════════════════════════
        //  SIMLI AVATAR
        // ══════════════════════════════════════
        grpAvatar = new GroupBox
        {
            Text = "🎭 Avatar",
            Location = new Point(580, 30),
            Size = new Size(320, 180),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            ForeColor = fgLight,
            BackColor = bgPanel
        };

        pnlAvatarHost = new Panel
        {
            Location = new Point(10, 22),
            Size = new Size(300, 130),
            BackColor = Color.Black
        };

        lblAvatarStatus = new Label
        {
            Text = "Waiting…",
            Location = new Point(10, 155),
            Size = new Size(200, 20),
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8F),
            TextAlign = ContentAlignment.MiddleLeft
        };

        lblSimliStatus = new Label
        {
            Text = "● Offline",
            Location = new Point(210, 155),
            Size = new Size(100, 20),
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleRight
        };

        grpAvatar.Controls.AddRange(new Control[] { pnlAvatarHost, lblAvatarStatus, lblSimliStatus });

        // ══════════════════════════════════════
        //  ENGINE STATE GROUP
        // ══════════════════════════════════════
        grpEngine = new GroupBox
        {
            Text = "⚙ Booking Engine",
            Location = new Point(580, 215),
            Size = new Size(320, 148),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            ForeColor = fgLight,
            BackColor = bgPanel
        };

        lblEngineState = new Label
        {
            Text = "State: Idle",
            Location = new Point(10, 22),
            Size = new Size(300, 20),
            ForeColor = Color.Cyan,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };

        txtEngineSlots = new RichTextBox
        {
            Location = new Point(10, 48),
            Size = new Size(300, 90),
            Font = new Font("Cascadia Mono", 8.5F),
            BackColor = Color.FromArgb(20, 20, 20),
            ForeColor = Color.FromArgb(180, 220, 180),
            ReadOnly = true,
            BorderStyle = BorderStyle.None
        };

        grpEngine.Controls.AddRange(new Control[] { lblEngineState, txtEngineSlots });

        // ══════════════════════════════════════
        //  LOGS
        // ══════════════════════════════════════
        grpLogs = new GroupBox
        {
            Text = "📋 Logs & Transcripts",
            Location = new Point(12, 335),
            Size = new Size(880, 385),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = fgLight,
            BackColor = bgPanel
        };

        txtLog = new RichTextBox
        {
            Location = new Point(10, 22),
            Size = new Size(860, 325),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Font = new Font("Cascadia Mono", 9F),
            BackColor = Color.FromArgb(20, 20, 20),
            ForeColor = Color.LightGreen,
            ReadOnly = true,
            BorderStyle = BorderStyle.None
        };

        btnClearLogs = MakeButton("Clear", 10, 352, 80, 26, Color.FromArgb(108, 117, 125));
        btnClearLogs.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        btnClearLogs.Click += (s, e) => txtLog.Clear();

        grpLogs.Controls.AddRange(new Control[] { txtLog, btnClearLogs });

        // ══════════════════════════════════════
        //  STATUS BAR
        // ══════════════════════════════════════
        statusStrip = new StatusStrip { BackColor = bgPanel, ForeColor = fgLight };
        statusLabel = new ToolStripStatusLabel("Ready");
        statusCallId = new ToolStripStatusLabel("") { Spring = true, TextAlign = ContentAlignment.MiddleRight };
        statusStrip.Items.AddRange(new ToolStripItem[] { statusLabel, statusCallId });

        // ══════════════════════════════════════
        //  FORM
        // ══════════════════════════════════════
        this.AutoScaleDimensions = new SizeF(7F, 15F);
        this.AutoScaleMode = AutoScaleMode.Font;
        this.ClientSize = new Size(920, 750);
        this.MinimumSize = new Size(900, 680);
        this.Text = "🚕 AdaCleanVersion — Deterministic Booking Engine v1.0";
        this.StartPosition = FormStartPosition.CenterScreen;
        this.Font = new Font("Segoe UI", 9F);
        this.BackColor = bgDark;
        this.ForeColor = fgLight;
        this.MainMenuStrip = menuStrip;

        this.Controls.AddRange(new Control[] { menuStrip, grpSip, grpCall, grpAvatar, grpEngine, grpLogs, statusStrip });

        this.ResumeLayout(false);
        this.PerformLayout();
    }

    #endregion

    // ── Helpers ──
    private static Label MakeLabel(string text, int x, int y)
        => new Label { Text = text, Location = new Point(x, y), AutoSize = true };

    private static TextBox MakeTextBox(int x, int y, int w, Color bg, Color fg)
        => new TextBox { Location = new Point(x, y), Size = new Size(w, 23), BackColor = bg, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle };

    private static Button MakeButton(string text, int x, int y, int w, int h, Color bg)
    {
        var btn = new Button
        {
            Text = text, Location = new Point(x, y), Size = new Size(w, h),
            BackColor = bg, ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };
        btn.FlatAppearance.BorderSize = 0;
        return btn;
    }

    // ── Field declarations ──
    private MenuStrip menuStrip;
    private ToolStripMenuItem mnuSettings, mnuExit, mnuViewConfig;

    // SIP Registration
    private GroupBox grpSip;
    private ComboBox cmbSipAccount;
    private Button btnSaveAccount, btnDeleteAccount, btnNewAccount;
    private TextBox txtSipServer, txtSipPort, txtSipUser, txtAuthId, txtSipPassword, txtDomain, txtDisplayName;
    private ComboBox cmbTransport;
    private CheckBox chkAutoAnswer;
    private Button btnConnect, btnDisconnect;
    private Label lblSipStatus;

    // Call Controls
    private GroupBox grpCall;
    private Button btnHangUp, btnMute;
    private Label lblCallInfo;

    // Engine State
    private GroupBox grpEngine;
    private Label lblEngineState;
    private RichTextBox txtEngineSlots;

    // Avatar
    private GroupBox grpAvatar;
    private Panel pnlAvatarHost;
    private Label lblAvatarStatus;
    private Label lblSimliStatus;

    // Logs
    private GroupBox grpLogs;
    private RichTextBox txtLog;
    private Button btnClearLogs;

    // Status bar
    private StatusStrip statusStrip;
    private ToolStripStatusLabel statusLabel, statusCallId;
}
