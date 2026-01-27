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
        cmbTransport.Items.AddRange(new object[] { "UDP", "TCP" });

        // SIP User
        var lblUser = new Label { Text = "Username:", Location = new Point(15, 58), Size = new Size(80, 23) };
        txtSipUser = new TextBox { Location = new Point(100, 55), Size = new Size(200, 23) };

        // SIP Password
        var lblPass = new Label { Text = "Password:", Location = new Point(320, 58), Size = new Size(65, 23) };
        txtSipPassword = new TextBox { Location = new Point(390, 55), Size = new Size(205, 23), UseSystemPasswordChar = true };

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
            Text = "🔒 Local OpenAI (Direct)", 
            Location = new Point(400, 88), 
            Size = new Size(160, 23),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(0, 123, 255)
        };
        chkLocalOpenAI.CheckedChanged += this.chkLocalOpenAI_CheckedChanged;

        // === Simli Avatar ===
        chkSimliAvatar = new CheckBox 
        { 
            Text = "🎭 Simli Avatar", 
            Location = new Point(565, 88), 
            Size = new Size(130, 23),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(156, 39, 176)
        };
        chkSimliAvatar.CheckedChanged += this.chkSimliAvatar_CheckedChanged;

        // OpenAI API Key (shown when Local mode is checked)
        lblApiKey = new Label { Text = "API Key:", Location = new Point(15, 118), Size = new Size(55, 23), Visible = false };
        txtApiKey = new TextBox { Location = new Point(75, 115), Size = new Size(300, 23), UseSystemPasswordChar = true, Visible = false };
        txtApiKey.PlaceholderText = "sk-... (OpenAI API key)";
        
        // Cheaper Pipeline checkbox (shown when Local mode is checked)
        chkCheaperPipeline = new CheckBox 
        { 
            Text = "💰 Cheaper Pipeline", 
            Location = new Point(385, 118), 
            Size = new Size(140, 23),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 167, 69),
            Visible = false
        };
        chkCheaperPipeline.CheckedChanged += this.chkCheaperPipeline_CheckedChanged;
        
        // Deepgram API Key (shown when cheaper pipeline is checked)
        lblDeepgramKey = new Label { Text = "Deepgram:", Location = new Point(525, 118), Size = new Size(65, 23), Visible = false };
        txtDeepgramKey = new TextBox { Location = new Point(595, 115), Size = new Size(100, 23), UseSystemPasswordChar = true, Visible = false };
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
            lblUser, txtSipUser, lblPass, txtSipPassword, 
            lblAudioMode, cmbAudioMode,
            lblResampler, cmbResampler,
            chkLocalOpenAI, chkSimliAvatar,
            lblApiKey, txtApiKey,
            chkCheaperPipeline, lblDeepgramKey, txtDeepgramKey,
            lblWs, txtWebSocketUrl,
            lblSimliApiKey, txtSimliApiKey, lblSimliFaceId, txtSimliFaceId,
            btnStartStop, btnMicTest
        });

        // === Status Panel ===
        var grpStatus = new GroupBox
        {
            Text = "📊 Status",
            Location = new Point(12, 210),
            Size = new Size(710, 70),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        var lblStatusLabel = new Label { Text = "Status:", Location = new Point(15, 25), Size = new Size(50, 23) };
        lblStatus = new Label { Text = "Ready", Location = new Point(70, 25), Size = new Size(300, 23), ForeColor = Color.Gray };

        var lblCallLabel = new Label { Text = "Call:", Location = new Point(400, 25), Size = new Size(40, 23) };
        lblActiveCall = new Label { Text = "No active call", Location = new Point(445, 25), Size = new Size(200, 23), ForeColor = Color.Gray };
        lblCallId = new Label { Text = "", Location = new Point(445, 45), Size = new Size(200, 20), ForeColor = Color.DimGray, Font = new Font("Consolas", 8F) };

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
        this.Controls.AddRange(new Control[] { grpConfig, grpStatus, grpLogs });
    }

    #endregion

    private TextBox txtSipServer;
    private TextBox txtSipPort;
    private TextBox txtSipUser;
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
    private CheckBox chkSimliAvatar;
    private CheckBox chkCheaperPipeline;
    private Label lblApiKey;
    private Label lblDeepgramKey;
    private Label lblWs;
    private Label lblSimliApiKey;
    private Label lblSimliFaceId;
    private Button btnStartStop;
    private Button btnMicTest;
    private Button btnClearLogs;
    private Button btnCopyLogs;
    private Label lblStatus;
    private Label lblActiveCall;
    private Label lblCallId;
    private ListBox lstLogs;
    private ContextMenuStrip ctxLogs;
    private ToolStripMenuItem mnuCopySelected;
    private ToolStripMenuItem mnuCopyAll;
}
