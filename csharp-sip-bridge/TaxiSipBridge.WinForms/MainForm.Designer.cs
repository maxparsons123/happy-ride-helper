namespace TaxiSipBridge;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private void InitializeComponent()
    {
        this.components = new System.ComponentModel.Container();
        
        // Form settings
        this.Text = "🚕 Taxi AI - SIP Auto-Answer";
        this.Size = new Size(750, 650);
        this.MinimumSize = new Size(650, 550);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.Font = new Font("Segoe UI", 9F);
        this.BackColor = Color.FromArgb(248, 249, 250);

        // === Configuration Panel ===
        var grpConfig = new GroupBox
        {
            Text = "📞 SIP Configuration",
            Location = new Point(12, 12),
            Size = new Size(710, 190),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        // SIP Server
        var lblServer = new Label { Text = "SIP Server:", Location = new Point(15, 28), Size = new Size(80, 23) };
        txtSipServer = new TextBox { Location = new Point(100, 25), Size = new Size(200, 23) };

        // SIP Port
        var lblPort = new Label { Text = "Port:", Location = new Point(320, 28), Size = new Size(40, 23) };
        txtSipPort = new TextBox { Location = new Point(365, 25), Size = new Size(60, 23) };

        // Transport
        var lblTransport = new Label { Text = "Transport:", Location = new Point(445, 28), Size = new Size(65, 23) };
        cmbTransport = new ComboBox { Location = new Point(515, 25), Size = new Size(80, 23), DropDownStyle = ComboBoxStyle.DropDownList };
        cmbTransport.Items.AddRange(new object[] { "UDP", "TCP", "TLS" });

        // SIP User (Extension)
        var lblUser = new Label { Text = "Extension:", Location = new Point(15, 58), Size = new Size(80, 23) };
        txtSipUser = new TextBox { Location = new Point(100, 55), Size = new Size(80, 23) };
        txtSipUser.PlaceholderText = "e.g. 300";

        // Auth ID (optional - for 3CX etc. where auth ID differs from extension)
        var lblAuthUser = new Label { Text = "Auth ID:", Location = new Point(190, 58), Size = new Size(55, 23) };
        txtAuthUser = new TextBox { Location = new Point(250, 55), Size = new Size(120, 23) };
        txtAuthUser.PlaceholderText = "(optional)";

        // SIP Password
        var lblPass = new Label { Text = "Password:", Location = new Point(380, 58), Size = new Size(65, 23) };
        txtSipPassword = new TextBox { Location = new Point(450, 55), Size = new Size(145, 23), UseSystemPasswordChar = true };

        // Audio Mode
        var lblAudioMode = new Label { Text = "Audio Mode:", Location = new Point(15, 88), Size = new Size(80, 23) };
        cmbAudioMode = new ComboBox { Location = new Point(100, 85), Size = new Size(120, 23), DropDownStyle = ComboBoxStyle.DropDownList };
        cmbAudioMode.Items.AddRange(new object[] { "Standard", "Jitter Buffer", "Built-in Pacer", "Simple Resample", "Test Tone" });

        // Resampler Mode
        var lblResampler = new Label { Text = "Resampler:", Location = new Point(230, 88), Size = new Size(65, 23) };
        cmbResampler = new ComboBox { Location = new Point(300, 85), Size = new Size(90, 23), DropDownStyle = ComboBoxStyle.DropDownList };
        cmbResampler.Items.AddRange(new object[] { "NAudio", "Custom" });
        cmbResampler.SelectedIndexChanged += (s, e) => {
            AudioCodecs.CurrentResamplerMode = (ResamplerMode)cmbResampler.SelectedIndex;
        };

        // === Local OpenAI Mode ===
        chkLocalOpenAI = new CheckBox 
        { 
            Text = "🔒 Local OpenAI", 
            Location = new Point(400, 88), 
            Size = new Size(105, 23),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(0, 123, 255)
        };
        chkLocalOpenAI.CheckedChanged += this.chkLocalOpenAI_CheckedChanged;

        // === Manual Answer Mode ===
        chkManualAnswer = new CheckBox 
        { 
            Text = "🎤 Manual", 
            Location = new Point(505, 88), 
            Size = new Size(75, 23),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(220, 53, 69)
        };
        chkManualAnswer.CheckedChanged += this.chkManualAnswer_CheckedChanged;

        // === Simli Avatar ===
        chkSimliAvatar = new CheckBox 
        { 
            Text = "🎭 Simli", 
            Location = new Point(580, 88), 
            Size = new Size(60, 23),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(156, 39, 176)
        };
        chkSimliAvatar.CheckedChanged += this.chkSimliAvatar_CheckedChanged;

        // === Audio Monitor (hear caller through speakers) ===
        chkMonitorAudio = new CheckBox 
        { 
            Text = "🔊 Mon", 
            Location = new Point(640, 88), 
            Size = new Size(60, 23),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(255, 152, 0)
        };
        chkMonitorAudio.CheckedChanged += this.chkMonitorAudio_CheckedChanged;

        // === G711 Mode (8kHz passthrough - experimental) ===
        chkG711Mode = new CheckBox 
        { 
            Text = "🎵 G711", 
            Location = new Point(385, 118), // Next to API Key field
            Size = new Size(75, 23),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(102, 51, 153),
            Visible = false // Only shown in Local OpenAI mode
        };
        chkG711Mode.CheckedChanged += this.chkG711Mode_CheckedChanged;

        // Answer/Reject buttons (hidden by default, shown when call rings in manual mode)
        btnAnswerCall = new Button
        {
            Text = "✅ Answer",
            Location = new Point(430, 148),
            Size = new Size(90, 32),
            BackColor = Color.FromArgb(40, 167, 69),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Visible = false
        };
        btnAnswerCall.FlatAppearance.BorderSize = 0;
        btnAnswerCall.Click += this.btnAnswerCall_Click;

        btnRejectCall = new Button
        {
            Text = "❌ Reject",
            Location = new Point(525, 148),
            Size = new Size(85, 32),
            BackColor = Color.FromArgb(220, 53, 69),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Visible = false
        };
        btnRejectCall.FlatAppearance.BorderSize = 0;
        btnRejectCall.Click += this.btnRejectCall_Click;

        btnHangUp = new Button
        {
            Text = "📴 Hang Up",
            Location = new Point(430, 148),
            Size = new Size(100, 32),
            BackColor = Color.FromArgb(220, 53, 69),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Visible = false
        };
        btnHangUp.FlatAppearance.BorderSize = 0;
        btnHangUp.Click += this.btnHangUp_Click;

        // OpenAI API Key (shown when Local mode is checked)
        lblApiKey = new Label { Text = "API Key:", Location = new Point(15, 118), Size = new Size(55, 23), Visible = false };
        txtApiKey = new TextBox { Location = new Point(75, 115), Size = new Size(300, 23), UseSystemPasswordChar = true, Visible = false };
        txtApiKey.PlaceholderText = "sk-... (OpenAI API key)";
        
        // Cheaper Pipeline checkbox (shown when Local mode is checked)
        chkCheaperPipeline = new CheckBox 
        { 
            Text = "💰 Cheaper", 
            Location = new Point(465, 118), 
            Size = new Size(90, 23),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 167, 69),
            Visible = false
        };
        chkCheaperPipeline.CheckedChanged += this.chkCheaperPipeline_CheckedChanged;
        
        // Deepgram API Key (shown when cheaper pipeline is checked)
        lblDeepgramKey = new Label { Text = "DG:", Location = new Point(560, 118), Size = new Size(25, 23), Visible = false };
        txtDeepgramKey = new TextBox { Location = new Point(588, 115), Size = new Size(105, 23), UseSystemPasswordChar = true, Visible = false };
        txtDeepgramKey.PlaceholderText = "Deepgram key";

        // WebSocket URL (hidden when Local mode is checked)
        lblWs = new Label { Text = "Ada URL:", Location = new Point(15, 118), Size = new Size(60, 23) };
        txtWebSocketUrl = new TextBox { Location = new Point(75, 115), Size = new Size(520, 23) };

        // Simli configuration row
        lblSimliApiKey = new Label { Text = "Simli Key:", Location = new Point(15, 148), Size = new Size(65, 23), Visible = false };
        txtSimliApiKey = new TextBox { Location = new Point(85, 145), Size = new Size(200, 23), UseSystemPasswordChar = true, Visible = false };
        txtSimliApiKey.PlaceholderText = "Simli API key";
        
        lblSimliFaceId = new Label { Text = "Face ID:", Location = new Point(295, 148), Size = new Size(55, 23), Visible = false };
        txtSimliFaceId = new TextBox { Location = new Point(355, 145), Size = new Size(240, 23), Visible = false };
        txtSimliFaceId.PlaceholderText = "Simli avatar face ID";

        // Start SIP Button
        btnStartStop = new Button
        {
            Text = "▶ Start SIP",
            Location = new Point(100, 148),
            Size = new Size(140, 32),
            BackColor = Color.FromArgb(40, 167, 69),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold)
        };
        btnStartStop.FlatAppearance.BorderSize = 0;
        btnStartStop.Click += this.btnStartStop_Click;

        // Mic Test Button
        btnMicTest = new Button
        {
            Text = "🎤 Test with Mic",
            Location = new Point(260, 148),
            Size = new Size(150, 32),
            BackColor = Color.FromArgb(0, 123, 255),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold)
        };
        btnMicTest.FlatAppearance.BorderSize = 0;
        btnMicTest.Click += this.btnMicTest_Click;

        grpConfig.Controls.AddRange(new Control[] {
            lblServer, txtSipServer, lblPort, txtSipPort, lblTransport, cmbTransport,
            lblUser, txtSipUser, lblAuthUser, txtAuthUser, lblPass, txtSipPassword, 
            lblAudioMode, cmbAudioMode,
            lblResampler, cmbResampler,
            chkLocalOpenAI, chkManualAnswer, chkSimliAvatar, chkMonitorAudio,
            lblApiKey, txtApiKey,
            chkCheaperPipeline, chkG711Mode, lblDeepgramKey, txtDeepgramKey,
            lblWs, txtWebSocketUrl,
            lblSimliApiKey, txtSimliApiKey, lblSimliFaceId, txtSimliFaceId,
            btnStartStop, btnMicTest,
            btnAnswerCall, btnRejectCall, btnHangUp
        });

        // === Avatar Panel (shown when Simli is enabled) ===
        grpAvatar = new GroupBox
        {
            Text = "🎭 Avatar",
            Location = new Point(530, 210),
            Size = new Size(192, 200),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Visible = false
        };

        picAvatar = new PictureBox
        {
            Location = new Point(10, 22),
            Size = new Size(172, 140),
            BackColor = Color.Black,
            SizeMode = PictureBoxSizeMode.Zoom
        };

        lblAvatarStatus = new Label
        {
            Text = "Waiting...",
            Location = new Point(10, 168),
            Size = new Size(172, 20),
            ForeColor = Color.Gray,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 9)
        };

        grpAvatar.Controls.AddRange(new Control[] { picAvatar, lblAvatarStatus });

        // === Status Panel ===
        var grpStatus = new GroupBox
        {
            Text = "📊 Status",
            Location = new Point(12, 210),
            Size = new Size(510, 70),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        var lblStatusLabel = new Label { Text = "Status:", Location = new Point(15, 25), Size = new Size(50, 23) };
        lblStatus = new Label { Text = "Ready", Location = new Point(70, 25), Size = new Size(200, 23), ForeColor = Color.Gray };

        var lblCallLabel = new Label { Text = "Call:", Location = new Point(280, 25), Size = new Size(40, 23) };
        lblActiveCall = new Label { Text = "No active call", Location = new Point(325, 25), Size = new Size(170, 23), ForeColor = Color.Gray };
        lblCallId = new Label { Text = "", Location = new Point(325, 45), Size = new Size(170, 20), ForeColor = Color.DimGray, Font = new Font("Consolas", 8F) };

        grpStatus.Controls.AddRange(new Control[] { lblStatusLabel, lblStatus, lblCallLabel, lblActiveCall, lblCallId });

        // === Logs Panel ===
        var grpLogs = new GroupBox
        {
            Text = "📋 Logs & Transcripts",
            Location = new Point(12, 288),
            Size = new Size(710, 310),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };

        lstLogs = new ListBox
        {
            Location = new Point(10, 22),
            Size = new Size(690, 270),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Font = new Font("Consolas", 9F),
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.LightGreen,
            BorderStyle = BorderStyle.None,
            SelectionMode = SelectionMode.MultiExtended
        };
        lstLogs.KeyDown += this.lstLogs_KeyDown;

        // Logs context menu
        ctxLogs = new ContextMenuStrip(this.components);
        mnuCopySelected = new ToolStripMenuItem("Copy selected");
        mnuCopySelected.Click += this.mnuCopySelected_Click;
        mnuCopyAll = new ToolStripMenuItem("Copy all");
        mnuCopyAll.Click += this.mnuCopyAll_Click;
        ctxLogs.Items.AddRange(new ToolStripItem[] { mnuCopySelected, mnuCopyAll });
        lstLogs.ContextMenuStrip = ctxLogs;

        btnClearLogs = new Button
        {
            Text = "Clear Logs",
            Location = new Point(10, 300),
            Size = new Size(100, 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(108, 117, 125),
            ForeColor = Color.White
        };
        btnClearLogs.FlatAppearance.BorderSize = 0;
        btnClearLogs.Click += this.btnClearLogs_Click;

        btnCopyLogs = new Button
        {
            Text = "📋 Copy Logs",
            Location = new Point(120, 300),
            Size = new Size(110, 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 123, 255),
            ForeColor = Color.White
        };
        btnCopyLogs.FlatAppearance.BorderSize = 0;
        btnCopyLogs.Click += this.btnCopyLogs_Click;

        grpLogs.Controls.AddRange(new Control[] { lstLogs, btnClearLogs, btnCopyLogs });

        // Add all to form
        this.Controls.AddRange(new Control[] { grpConfig, grpStatus, grpAvatar, grpLogs });
    }

    #endregion

    private TextBox txtSipServer;
    private TextBox txtSipPort;
    private TextBox txtSipUser;
    private TextBox txtAuthUser;
    private TextBox txtSipPassword;
    private TextBox txtWebSocketUrl;
    private TextBox txtApiKey;
    private TextBox txtDeepgramKey;
    private TextBox txtSimliApiKey;
    private TextBox txtSimliFaceId;
    private ComboBox cmbTransport;
    private ComboBox cmbAudioMode;
    private ComboBox cmbResampler;
    private CheckBox chkLocalOpenAI;
    private CheckBox chkManualAnswer;
    private CheckBox chkSimliAvatar;
    private CheckBox chkCheaperPipeline;
    private CheckBox chkMonitorAudio;
    private CheckBox chkG711Mode;
    private Label lblApiKey;
    private Label lblDeepgramKey;
    private Label lblWs;
    private Label lblSimliApiKey;
    private Label lblSimliFaceId;
    private Button btnStartStop;
    private Button btnMicTest;
    private Button btnAnswerCall;
    private Button btnRejectCall;
    private Button btnHangUp;
    private Button btnClearLogs;
    private Button btnCopyLogs;
    private Label lblStatus;
    private Label lblActiveCall;
    private Label lblCallId;
    private ListBox lstLogs;
    private ContextMenuStrip ctxLogs;
    private ToolStripMenuItem mnuCopySelected;
    private ToolStripMenuItem mnuCopyAll;
    private GroupBox grpAvatar;
    private PictureBox picAvatar;
    private Label lblAvatarStatus;
}
