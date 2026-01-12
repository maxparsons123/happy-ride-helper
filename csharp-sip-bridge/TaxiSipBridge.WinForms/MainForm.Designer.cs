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
        this.Text = "🚕 Taxi AI SIP Bridge";
        this.Size = new Size(700, 600);
        this.MinimumSize = new Size(600, 500);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.Font = new Font("Segoe UI", 9F);
        this.BackColor = Color.FromArgb(248, 249, 250);

        // === Configuration Panel ===
        var grpConfig = new GroupBox
        {
            Text = "SIP Configuration",
            Location = new Point(12, 12),
            Size = new Size(660, 160),
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

        // WebSocket URL
        var lblWs = new Label { Text = "WebSocket:", Location = new Point(15, 88), Size = new Size(80, 23) };
        txtWebSocketUrl = new TextBox { Location = new Point(100, 85), Size = new Size(495, 23) };

        // Start/Stop Button
        btnStartStop = new Button
        {
            Text = "▶ Start Bridge",
            Location = new Point(100, 120),
            Size = new Size(150, 30),
            BackColor = Color.FromArgb(40, 167, 69),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        btnStartStop.FlatAppearance.BorderSize = 0;
        btnStartStop.Click += btnStartStop_Click;

        grpConfig.Controls.AddRange(new Control[] {
            lblServer, txtSipServer, lblPort, txtSipPort, lblTransport, cmbTransport,
            lblUser, txtSipUser, lblPass, txtSipPassword, lblWs, txtWebSocketUrl, btnStartStop
        });

        // === Status Panel ===
        var grpStatus = new GroupBox
        {
            Text = "Status",
            Location = new Point(12, 180),
            Size = new Size(660, 70),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        var lblStatusLabel = new Label { Text = "Registration:", Location = new Point(15, 25), Size = new Size(80, 23) };
        lblStatus = new Label { Text = "Not started", Location = new Point(100, 25), Size = new Size(200, 23), ForeColor = Color.Gray };

        var lblCallLabel = new Label { Text = "Active Call:", Location = new Point(320, 25), Size = new Size(70, 23) };
        lblActiveCall = new Label { Text = "No active call", Location = new Point(395, 25), Size = new Size(150, 23), ForeColor = Color.Gray };
        lblCallId = new Label { Text = "", Location = new Point(395, 45), Size = new Size(200, 20), ForeColor = Color.DimGray, Font = new Font("Consolas", 8F) };

        grpStatus.Controls.AddRange(new Control[] { lblStatusLabel, lblStatus, lblCallLabel, lblActiveCall, lblCallId });

        // === Logs Panel ===
        var grpLogs = new GroupBox
        {
            Text = "Logs",
            Location = new Point(12, 258),
            Size = new Size(660, 290),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };

        lstLogs = new ListBox
        {
            Location = new Point(10, 22),
            Size = new Size(640, 220),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Font = new Font("Consolas", 9F),
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.LightGreen,
            BorderStyle = BorderStyle.None
        };

        btnClearLogs = new Button
        {
            Text = "Clear Logs",
            Location = new Point(10, 250),
            Size = new Size(100, 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(108, 117, 125),
            ForeColor = Color.White
        };
        btnClearLogs.FlatAppearance.BorderSize = 0;
        btnClearLogs.Click += btnClearLogs_Click;

        grpLogs.Controls.AddRange(new Control[] { lstLogs, btnClearLogs });

        // Add all to form
        this.Controls.AddRange(new Control[] { grpConfig, grpStatus, grpLogs });
    }

    #endregion

    private TextBox txtSipServer;
    private TextBox txtSipPort;
    private TextBox txtSipUser;
    private TextBox txtSipPassword;
    private TextBox txtWebSocketUrl;
    private ComboBox cmbTransport;
    private Button btnStartStop;
    private Button btnClearLogs;
    private Label lblStatus;
    private Label lblActiveCall;
    private Label lblCallId;
    private ListBox lstLogs;
}
