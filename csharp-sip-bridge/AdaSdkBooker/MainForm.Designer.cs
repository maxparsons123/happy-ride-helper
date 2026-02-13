namespace AdaSdkBooker;

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

        var bgDark = Color.FromArgb(24, 24, 28);
        var bgPanel = Color.FromArgb(36, 36, 40);
        var bgInput = Color.FromArgb(52, 52, 58);
        var fgLight = Color.FromArgb(220, 220, 225);
        var accent = Color.FromArgb(0, 122, 204);
        var green = Color.FromArgb(40, 167, 69);
        var red = Color.FromArgb(200, 50, 50);
        var orange = Color.FromArgb(255, 152, 0);
        var borderColor = Color.FromArgb(60, 60, 68);

        // ══════════════════════════════════════
        //  TOOLSTRIP (top bar — all menus here)
        // ══════════════════════════════════════
        toolStrip = new ToolStrip
        {
            BackColor = Color.FromArgb(30, 30, 34),
            ForeColor = fgLight,
            GripStyle = ToolStripGripStyle.Hidden,
            Padding = new Padding(4, 0, 4, 0),
            RenderMode = ToolStripRenderMode.Professional
        };

        // Settings dropdown
        var tsiSettings = new ToolStripDropDownButton("⚙ Settings");
        tsiSettings.ForeColor = fgLight;
        tsiOpenAi = new ToolStripMenuItem("🤖 OpenAI / Audio / Dispatch…");
        tsiOpenAi.Click += tsiSettings_Click;
        tsiViewConfig = new ToolStripMenuItem("📄 View Config File");
        tsiViewConfig.Click += tsiViewConfig_Click;
        tsiSettings.DropDownItems.AddRange(new ToolStripItem[] { tsiOpenAi, tsiViewConfig });

        // Ada toggle button
        tsiAdaToggle = new ToolStripButton("🤖 Ada: ON") { ForeColor = Color.LimeGreen, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        tsiAdaToggle.Click += tsiAdaToggle_Click;

        // Log toggle
        tsiLogToggle = new ToolStripButton("📋 Log") { ForeColor = fgLight };
        tsiLogToggle.Click += tsiLogToggle_Click;

        // About
        var tsiAbout = new ToolStripButton("ℹ About") { ForeColor = fgLight };
        tsiAbout.Click += (s, e) => MessageBox.Show("AdaSdkBooker v1.0\nAI-Powered Taxi Booking System\n\nBuilt on AdaSdkModel engine.", "About", MessageBoxButtons.OK, MessageBoxIcon.Information);

        toolStrip.Items.AddRange(new ToolStripItem[]
        {
            tsiSettings,
            new ToolStripSeparator(),
            tsiAdaToggle,
            new ToolStripSeparator(),
            tsiLogToggle,
            new ToolStripSeparator(),
            tsiAbout
        });

        // ══════════════════════════════════════
        //  MAIN SPLIT — LEFT (booking+jobs) | RIGHT (ada/map+sip+call)
        // ══════════════════════════════════════
        splitMain = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 520,
            SplitterWidth = 3,
            BackColor = bgDark,
            BorderStyle = BorderStyle.None,
            FixedPanel = FixedPanel.None
        };

        // ══════════════════════════════════════════════════
        //  LEFT PANEL — BOOKING FORM + JOB GRID
        // ══════════════════════════════════════════════════
        var pnlLeft = splitMain.Panel1;
        pnlLeft.BackColor = bgDark;
        pnlLeft.Padding = new Padding(6, 4, 3, 4);

        // Split left into booking (top) and jobs (bottom)
        splitLeftVert = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 310,
            SplitterWidth = 3,
            BackColor = bgDark,
            BorderStyle = BorderStyle.None
        };

        // ── BOOKING FORM ──
        pnlBooking = new Panel { Dock = DockStyle.Fill, BackColor = bgPanel, Padding = new Padding(10, 8, 10, 8) };

        var lblBookingTitle = new Label
        {
            Text = "📋 NEW BOOKING",
            Dock = DockStyle.Top, Height = 24,
            ForeColor = accent, Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var pnlBookingFields = new Panel { Dock = DockStyle.Fill, BackColor = bgPanel };

        int y = 4;
        pnlBookingFields.Controls.Add(MakeLabel("Name:", 0, y + 3));
        txtCallerName = MakeTextBox(65, y, 150, bgInput, fgLight);
        txtCallerName.PlaceholderText = "Customer name";
        pnlBookingFields.Controls.Add(MakeLabel("Phone:", 225, y + 3));
        txtPhone = MakeTextBox(280, y, 140, bgInput, fgLight);
        txtPhone.PlaceholderText = "+44…";
        btnRepeatLast = MakeButton("🔁", 425, y, 50, 24, Color.FromArgb(55, 55, 65));
        btnRepeatLast.Visible = false;
        btnRepeatLast.Click += btnRepeatLast_Click;
        pnlBookingFields.Controls.AddRange(new Control[] { txtCallerName, txtPhone, btnRepeatLast });
        y += 30;

        pnlBookingFields.Controls.Add(MakeLabel("Pickup:", 0, y + 3));
        cmbPickup = MakeComboBox(65, y, 355, bgInput, fgLight);
        lblPickupStatus = new Label { Text = "", Location = new Point(425, y + 1), Size = new Size(24, 22), Font = new Font("Segoe UI", 12F) };
        pnlBookingFields.Controls.AddRange(new Control[] { cmbPickup, lblPickupStatus });
        y += 28;
        lblPickupResolved = new Label { Text = "", Location = new Point(65, y), Size = new Size(380, 14), ForeColor = Color.FromArgb(110, 170, 110), Font = new Font("Segoe UI", 7.5F, FontStyle.Italic) };
        pnlBookingFields.Controls.Add(lblPickupResolved);
        y += 16;

        pnlBookingFields.Controls.Add(MakeLabel("Dropoff:", 0, y + 3));
        cmbDropoff = MakeComboBox(65, y, 355, bgInput, fgLight);
        lblDropoffStatus = new Label { Text = "", Location = new Point(425, y + 1), Size = new Size(24, 22), Font = new Font("Segoe UI", 12F) };
        pnlBookingFields.Controls.AddRange(new Control[] { cmbDropoff, lblDropoffStatus });
        y += 28;
        lblDropoffResolved = new Label { Text = "", Location = new Point(65, y), Size = new Size(380, 14), ForeColor = Color.FromArgb(110, 170, 110), Font = new Font("Segoe UI", 7.5F, FontStyle.Italic) };
        pnlBookingFields.Controls.Add(lblDropoffResolved);
        y += 18;

        pnlBookingFields.Controls.Add(MakeLabel("Pax:", 0, y + 3));
        nudPassengers = new NumericUpDown { Location = new Point(65, y), Size = new Size(55, 23), Minimum = 1, Maximum = 16, Value = 1, BackColor = bgInput, ForeColor = fgLight, BorderStyle = BorderStyle.FixedSingle };
        pnlBookingFields.Controls.Add(MakeLabel("Vehicle:", 130, y + 3));
        cmbVehicle = MakeComboBox(190, y, 120, bgInput, fgLight);
        cmbVehicle.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbVehicle.Items.AddRange(new object[] { "Saloon", "Estate", "MPV", "Minibus" });
        cmbVehicle.SelectedIndex = 0;
        pnlBookingFields.Controls.Add(MakeLabel("Time:", 320, y + 3));
        cmbPickupTime = MakeComboBox(365, y, 85, bgInput, fgLight);
        cmbPickupTime.Items.AddRange(new object[] { "ASAP", "15 min", "30 min", "1 hour" });
        cmbPickupTime.SelectedIndex = 0;
        pnlBookingFields.Controls.AddRange(new Control[] { nudPassengers, cmbVehicle, cmbPickupTime });
        y += 30;

        // Quote row
        btnVerify = MakeButton("🔍 Get Quote", 0, y, 120, 28, accent);
        btnVerify.Click += async (s, e) => await VerifyAndQuoteAsync();
        lblFare = new Label { Text = "Fare: —", Location = new Point(130, y + 2), Size = new Size(140, 22), ForeColor = Color.LimeGreen, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
        lblEta = new Label { Text = "ETA: —", Location = new Point(275, y + 4), Size = new Size(100, 20), ForeColor = Color.FromArgb(160, 200, 160), Font = new Font("Segoe UI", 8.5F) };
        pnlBookingFields.Controls.AddRange(new Control[] { btnVerify, lblFare, lblEta });
        y += 34;

        // Action row
        btnDispatch = MakeButton("✅ Dispatch", 0, y, 120, 30, green);
        btnDispatch.Enabled = false;
        btnDispatch.Click += async (s, e) => await ConfirmBookingAsync();
        btnClearBooking = MakeButton("🗑 Clear", 130, y, 80, 30, Color.FromArgb(70, 70, 75));
        btnClearBooking.Click += (s, e) => ClearBookingForm();
        lblBookingStatus = new Label { Text = "", Location = new Point(220, y + 6), Size = new Size(240, 18), ForeColor = Color.Gray, Font = new Font("Segoe UI", 8F, FontStyle.Italic) };
        pnlBookingFields.Controls.AddRange(new Control[] { btnDispatch, btnClearBooking, lblBookingStatus });

        pnlBooking.Controls.Add(pnlBookingFields);
        pnlBooking.Controls.Add(lblBookingTitle);

        // ── JOB GRID ──
        pnlJobs = new Panel { Dock = DockStyle.Fill, BackColor = bgPanel, Padding = new Padding(6, 4, 6, 4) };

        var lblJobsTitle = new Label
        {
            Text = "📊 JOBS",
            Dock = DockStyle.Top, Height = 22,
            ForeColor = accent, Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };

        dgvJobs = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Color.FromArgb(28, 28, 32),
            ForeColor = fgLight,
            GridColor = borderColor,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            RowHeadersVisible = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            Font = new Font("Segoe UI", 8.5F),
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(28, 28, 32),
                ForeColor = fgLight,
                SelectionBackColor = Color.FromArgb(0, 80, 160),
                SelectionForeColor = Color.White,
                Padding = new Padding(2)
            },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(42, 42, 48),
                ForeColor = Color.FromArgb(180, 200, 220),
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleLeft
            },
            EnableHeadersVisualStyles = false
        };

        dgvJobs.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { Name = "ColRef", HeaderText = "Ref", Width = 70 },
            new DataGridViewTextBoxColumn { Name = "ColName", HeaderText = "Name", Width = 90 },
            new DataGridViewTextBoxColumn { Name = "ColPhone", HeaderText = "Phone", Width = 100 },
            new DataGridViewTextBoxColumn { Name = "ColPickup", HeaderText = "Pickup", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 100 },
            new DataGridViewTextBoxColumn { Name = "ColDropoff", HeaderText = "Dropoff", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 100 },
            new DataGridViewTextBoxColumn { Name = "ColPax", HeaderText = "Pax", Width = 36 },
            new DataGridViewTextBoxColumn { Name = "ColFare", HeaderText = "Fare", Width = 65 },
            new DataGridViewTextBoxColumn { Name = "ColStatus", HeaderText = "Status", Width = 70 },
            new DataGridViewTextBoxColumn { Name = "ColTime", HeaderText = "Time", Width = 55 }
        });

        pnlJobs.Controls.Add(dgvJobs);
        pnlJobs.Controls.Add(lblJobsTitle);

        splitLeftVert.Panel1.Controls.Add(pnlBooking);
        splitLeftVert.Panel2.Controls.Add(pnlJobs);
        pnlLeft.Controls.Add(splitLeftVert);

        // ══════════════════════════════════════════════════
        //  RIGHT PANEL — ADA/MAP + SIP + CALL CONTROLS
        // ══════════════════════════════════════════════════
        var pnlRight = splitMain.Panel2;
        pnlRight.BackColor = bgDark;
        pnlRight.Padding = new Padding(3, 4, 6, 4);

        var pnlRightInner = new Panel { Dock = DockStyle.Fill, BackColor = bgDark };

        // ── ADA / MAP VIEW ──
        pnlAdaMap = new Panel
        {
            Dock = DockStyle.Top, Height = 290,
            BackColor = bgPanel, Padding = new Padding(6, 4, 6, 4)
        };

        pnlAdaMapHeader = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = bgPanel };
        lblAdaMapTitle = new Label { Text = "🤖 ADA", Location = new Point(2, 4), AutoSize = true, ForeColor = Color.LimeGreen, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        lblAdaMapStatus = new Label { Text = "Waiting…", Location = new Point(200, 6), Size = new Size(140, 16), ForeColor = Color.Gray, Font = new Font("Segoe UI", 7.5F), TextAlign = ContentAlignment.MiddleRight, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        pnlAdaMapHeader.Controls.AddRange(new Control[] { lblAdaMapTitle, lblAdaMapStatus });

        pnlAdaMapHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };

        pnlAdaMap.Controls.Add(pnlAdaMapHost);
        pnlAdaMap.Controls.Add(pnlAdaMapHeader);

        // ── SIP REGISTRATION (compact) ──
        pnlSip = new Panel
        {
            Dock = DockStyle.Top, Height = 108,
            BackColor = bgPanel, Padding = new Padding(6, 2, 6, 4)
        };

        var lblSipTitle = new Label { Text = "📞 SIP", Location = new Point(2, 2), AutoSize = true, ForeColor = accent, Font = new Font("Segoe UI", 8.5F, FontStyle.Bold) };
        pnlSip.Controls.Add(lblSipTitle);

        int sy = 20;
        pnlSip.Controls.Add(MakeLabel("Acct:", 0, sy + 3, 8F));
        cmbSipAccount = new ComboBox { Location = new Point(42, sy), Size = new Size(180, 22), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = bgInput, ForeColor = fgLight, Font = new Font("Segoe UI", 8F) };
        cmbSipAccount.SelectedIndexChanged += cmbSipAccount_SelectedIndexChanged;
        btnSaveSip = MakeButton("💾", 226, sy, 28, 22, Color.FromArgb(55, 55, 65));
        btnSaveSip.Font = new Font("Segoe UI", 7F);
        btnSaveSip.Click += btnSaveAccount_Click;
        pnlSip.Controls.AddRange(new Control[] { cmbSipAccount, btnSaveSip });
        sy += 26;

        pnlSip.Controls.Add(MakeLabel("Svr:", 0, sy + 3, 8F));
        txtSipServer = MakeTextBox(42, sy, 120, bgInput, fgLight, 8F);
        txtSipServer.PlaceholderText = "sip.example.com";
        pnlSip.Controls.Add(MakeLabel("Ext:", 168, sy + 3, 8F));
        txtSipUser = MakeTextBox(196, sy, 58, bgInput, fgLight, 8F);
        pnlSip.Controls.AddRange(new Control[] { txtSipServer, txtSipUser });
        sy += 24;

        pnlSip.Controls.Add(MakeLabel("Pass:", 0, sy + 3, 8F));
        txtSipPassword = MakeTextBox(42, sy, 88, bgInput, fgLight, 8F);
        txtSipPassword.UseSystemPasswordChar = true;
        pnlSip.Controls.Add(MakeLabel("Port:", 136, sy + 3, 8F));
        txtSipPort = MakeTextBox(170, sy, 42, bgInput, fgLight, 8F);
        txtSipPort.Text = "5060";
        chkAutoAnswer = new CheckBox { Text = "Auto", Location = new Point(218, sy + 1), Size = new Size(50, 20), ForeColor = fgLight, Font = new Font("Segoe UI", 7.5F), Checked = true };
        pnlSip.Controls.AddRange(new Control[] { txtSipPassword, txtSipPort, chkAutoAnswer });
        sy += 24;

        btnConnect = MakeButton("▶ Connect", 0, sy, 80, 24, green);
        btnConnect.Font = new Font("Segoe UI", 8F, FontStyle.Bold);
        btnConnect.Click += btnConnect_Click;
        btnDisconnect = MakeButton("■ Stop", 84, sy, 65, 24, red);
        btnDisconnect.Font = new Font("Segoe UI", 8F, FontStyle.Bold);
        btnDisconnect.Enabled = false;
        btnDisconnect.Click += btnDisconnect_Click;
        lblSipStatus = new Label { Text = "● Offline", Location = new Point(154, sy + 4), Size = new Size(110, 16), ForeColor = Color.Gray, Font = new Font("Segoe UI", 8F, FontStyle.Bold) };
        pnlSip.Controls.AddRange(new Control[] { btnConnect, btnDisconnect, lblSipStatus });

        // ── CALL CONTROLS (compact) ──
        pnlCall = new Panel
        {
            Dock = DockStyle.Top, Height = 78,
            BackColor = bgPanel, Padding = new Padding(6, 2, 6, 4)
        };

        var lblCallTitle = new Label { Text = "🎧 CALL", Location = new Point(2, 2), AutoSize = true, ForeColor = accent, Font = new Font("Segoe UI", 8.5F, FontStyle.Bold) };
        pnlCall.Controls.Add(lblCallTitle);

        int cy = 20;
        btnAnswer = MakeButton("✅", 0, cy, 38, 24, green);
        btnAnswer.Enabled = false; btnAnswer.Click += btnAnswer_Click;
        btnReject = MakeButton("❌", 42, cy, 38, 24, red);
        btnReject.Enabled = false; btnReject.Click += btnReject_Click;
        btnHangUp = MakeButton("📴", 84, cy, 38, 24, Color.FromArgb(160, 45, 45));
        btnHangUp.Enabled = false; btnHangUp.Click += btnHangUp_Click;
        btnMute = MakeButton("🔊", 126, cy, 38, 24, Color.FromArgb(65, 65, 70));
        btnMute.Enabled = false; btnMute.Click += btnMute_Click;
        lblCallInfo = new Label { Text = "No call", Location = new Point(170, cy + 4), Size = new Size(100, 16), ForeColor = Color.Gray, Font = new Font("Segoe UI", 7.5F) };
        pnlCall.Controls.AddRange(new Control[] { btnAnswer, btnReject, btnHangUp, btnMute, lblCallInfo });
        cy += 28;

        chkManualMode = new CheckBox { Text = "🎤 Operator", Location = new Point(0, cy), Size = new Size(100, 20), ForeColor = orange, Font = new Font("Segoe UI", 8F, FontStyle.Bold) };
        chkManualMode.CheckedChanged += chkManualMode_CheckedChanged;
        trkOpVolume = new TrackBar
        {
            Location = new Point(105, cy - 2), Size = new Size(100, 25),
            Minimum = 10, Maximum = 60, Value = 20,
            TickFrequency = 10, SmallChange = 5, BackColor = bgPanel
        };
        trkOpVolume.ValueChanged += (s, e) => { _operatorMicGain = trkOpVolume.Value / 10f; };
        lblOpVolumeVal = new Label { Text = "2.0x", Location = new Point(210, cy + 2), Size = new Size(36, 16), ForeColor = Color.LimeGreen, Font = new Font("Segoe UI", 7.5F, FontStyle.Bold) };
        pnlCall.Controls.AddRange(new Control[] { chkManualMode, trkOpVolume, lblOpVolumeVal });

        // ── SPACERS between right panels ──
        var spacer1 = new Panel { Dock = DockStyle.Top, Height = 4, BackColor = bgDark };
        var spacer2 = new Panel { Dock = DockStyle.Top, Height = 4, BackColor = bgDark };

        // Add right panels in reverse dock order (bottom to top)
        pnlRightInner.Controls.Add(pnlCall);
        pnlRightInner.Controls.Add(spacer2);
        pnlRightInner.Controls.Add(pnlSip);
        pnlRightInner.Controls.Add(spacer1);
        pnlRightInner.Controls.Add(pnlAdaMap);

        pnlRight.Controls.Add(pnlRightInner);

        // ══════════════════════════════════════
        //  LOG PANEL (toggleable, docked bottom)
        // ══════════════════════════════════════
        pnlLog = new Panel { Dock = DockStyle.Bottom, Height = 150, BackColor = bgPanel, Visible = false, Padding = new Padding(6, 2, 6, 4) };
        txtLog = new RichTextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Cascadia Mono", 8.5F),
            BackColor = Color.FromArgb(18, 18, 20),
            ForeColor = Color.LightGreen,
            ReadOnly = true,
            BorderStyle = BorderStyle.None
        };
        var lblLogTitle = new Label { Text = "📋 LOG", Dock = DockStyle.Top, Height = 18, ForeColor = accent, Font = new Font("Segoe UI", 8F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        pnlLog.Controls.Add(txtLog);
        pnlLog.Controls.Add(lblLogTitle);

        // ══════════════════════════════════════
        //  STATUS BAR
        // ══════════════════════════════════════
        statusStrip = new StatusStrip { BackColor = Color.FromArgb(30, 30, 34), ForeColor = fgLight };
        statusLabel = new ToolStripStatusLabel("Ready") { ForeColor = fgLight };
        statusCallId = new ToolStripStatusLabel("") { Spring = true, TextAlign = ContentAlignment.MiddleRight, ForeColor = fgLight };
        statusStrip.Items.AddRange(new ToolStripItem[] { statusLabel, statusCallId });

        // ══════════════════════════════════════
        //  FORM
        // ══════════════════════════════════════
        this.AutoScaleDimensions = new SizeF(7F, 15F);
        this.AutoScaleMode = AutoScaleMode.Font;
        this.ClientSize = new Size(1080, 680);
        this.MinimumSize = new Size(960, 600);
        this.Text = "🚕 AdaSdkBooker — AI Taxi Booking System v1.0";
        this.StartPosition = FormStartPosition.CenterScreen;
        this.Font = new Font("Segoe UI", 9F);
        this.BackColor = bgDark;
        this.ForeColor = fgLight;

        this.Controls.Add(splitMain);
        this.Controls.Add(pnlLog);
        this.Controls.Add(toolStrip);
        this.Controls.Add(statusStrip);

        this.ResumeLayout(false);
        this.PerformLayout();
    }

    #endregion

    // ── Helpers ──
    private static Label MakeLabel(string text, int x, int y, float fontSize = 9F)
        => new Label { Text = text, Location = new Point(x, y), AutoSize = true, Font = new Font("Segoe UI", fontSize) };

    private static TextBox MakeTextBox(int x, int y, int w, Color bg, Color fg, float fontSize = 8.5F)
        => new TextBox { Location = new Point(x, y), Size = new Size(w, 22), BackColor = bg, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", fontSize) };

    private static ComboBox MakeComboBox(int x, int y, int w, Color bg, Color fg)
        => new ComboBox { Location = new Point(x, y), Size = new Size(w, 22), BackColor = bg, ForeColor = fg, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8.5F) };

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
    private ToolStrip toolStrip;
    private ToolStripMenuItem tsiOpenAi, tsiViewConfig;
    private ToolStripButton tsiAdaToggle, tsiLogToggle;

    private SplitContainer splitMain, splitLeftVert;

    // Booking form
    private Panel pnlBooking;
    private TextBox txtCallerName, txtPhone;
    private ComboBox cmbPickup, cmbDropoff, cmbVehicle, cmbPickupTime;
    private NumericUpDown nudPassengers;
    private Label lblPickupStatus, lblDropoffStatus, lblPickupResolved, lblDropoffResolved;
    private Label lblFare, lblEta, lblBookingStatus;
    private Button btnVerify, btnDispatch, btnClearBooking, btnRepeatLast;

    // Job grid
    private Panel pnlJobs;
    private DataGridView dgvJobs;

    // Ada / Map view
    private Panel pnlAdaMap, pnlAdaMapHeader, pnlAdaMapHost;
    private Label lblAdaMapTitle, lblAdaMapStatus;

    // SIP
    private Panel pnlSip;
    private ComboBox cmbSipAccount;
    private Button btnSaveSip;
    private TextBox txtSipServer, txtSipPort, txtSipUser, txtSipPassword;
    private CheckBox chkAutoAnswer;
    private Button btnConnect, btnDisconnect;
    private Label lblSipStatus;

    // Call controls
    private Panel pnlCall;
    private Button btnAnswer, btnReject, btnHangUp, btnMute;
    private CheckBox chkManualMode;
    private TrackBar trkOpVolume;
    private Label lblOpVolumeVal, lblCallInfo;

    // Log
    private Panel pnlLog;
    private RichTextBox txtLog;

    // Status bar
    private StatusStrip statusStrip;
    private ToolStripStatusLabel statusLabel, statusCallId;
}
