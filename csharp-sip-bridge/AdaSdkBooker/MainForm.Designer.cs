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

        // ── Color palette ──
        var bgDark = Color.FromArgb(18, 18, 22);
        var bgPanel = Color.FromArgb(28, 28, 34);
        var bgSection = Color.FromArgb(34, 36, 42);
        var bgInput = Color.FromArgb(46, 48, 56);
        var fgLight = Color.FromArgb(230, 232, 238);
        var fgDim = Color.FromArgb(140, 145, 158);
        var accent = Color.FromArgb(65, 135, 220);
        var accentDim = Color.FromArgb(50, 100, 170);
        var green = Color.FromArgb(38, 162, 68);
        var red = Color.FromArgb(190, 48, 48);
        var orange = Color.FromArgb(240, 155, 20);
        var sectionBorder = Color.FromArgb(52, 56, 68);
        var headerBg = Color.FromArgb(28, 30, 38);

        // ══════════════════════════════════════
        //  TOOLSTRIP (top bar)
        // ══════════════════════════════════════
        toolStrip = new ToolStrip
        {
            BackColor = Color.FromArgb(22, 22, 26),
            ForeColor = fgLight,
            GripStyle = ToolStripGripStyle.Hidden,
            Padding = new Padding(10, 3, 10, 3),
            RenderMode = ToolStripRenderMode.Professional,
            Font = new Font("Segoe UI", 9F)
        };

        var tsiSettings = new ToolStripDropDownButton("⚙ Settings") { ForeColor = fgLight };
        tsiOpenAi = new ToolStripMenuItem("🤖 OpenAI / Audio / Dispatch…");
        tsiOpenAi.Click += tsiSettings_Click;
        tsiViewConfig = new ToolStripMenuItem("📄 View Config File");
        tsiViewConfig.Click += tsiViewConfig_Click;
        tsiSettings.DropDownItems.AddRange(new ToolStripItem[] { tsiOpenAi, tsiViewConfig });

        tsiAdaToggle = new ToolStripButton("🤖 Ada: ON") { ForeColor = Color.LimeGreen, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        tsiAdaToggle.Click += tsiAdaToggle_Click;

        tsiLogToggle = new ToolStripButton("📋 Log") { ForeColor = fgLight };
        tsiLogToggle.Click += tsiLogToggle_Click;

        var tsiAbout = new ToolStripButton("ℹ About") { ForeColor = fgLight };
        tsiAbout.Click += (s, e) => MessageBox.Show("AdaSdkBooker v1.0\nAI-Powered Taxi Booking System\n\nBuilt on AdaSdkModel engine.", "About", MessageBoxButtons.OK, MessageBoxIcon.Information);

        toolStrip.Items.AddRange(new ToolStripItem[]
        {
            tsiSettings, new ToolStripSeparator(),
            tsiAdaToggle, new ToolStripSeparator(),
            tsiLogToggle, new ToolStripSeparator(),
            tsiAbout
        });

        // ══════════════════════════════════════
        //  MAIN LAYOUT — LEFT (booking+jobs) | RIGHT (ada+sip+call+audio)
        // ══════════════════════════════════════
        splitMain = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 5,
            BackColor = bgDark,
            BorderStyle = BorderStyle.None,
            FixedPanel = FixedPanel.Panel2,
            Panel2MinSize = 340
        };

        // ══════════════════════════════════════════════════
        //  LEFT PANEL — BOOKING FORM + JOB GRID
        // ══════════════════════════════════════════════════
        var pnlLeft = splitMain.Panel1;
        pnlLeft.BackColor = bgDark;
        pnlLeft.Padding = new Padding(10, 8, 5, 8);

        splitLeftVert = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 310,
            SplitterWidth = 6,
            BackColor = bgDark,
            BorderStyle = BorderStyle.None
        };

        // ┌─────────────────────────────────────────┐
        // │  📋 NEW BOOKING                         │
        // └─────────────────────────────────────────┘
        pnlBooking = MakeSectionPanel(DockStyle.Fill, bgSection, sectionBorder);

        var lblBookingTitle = MakeSectionTitle("📋  NEW BOOKING", accent, headerBg);

        // Use a TableLayoutPanel for proper stretching
        var tblBooking = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = bgSection,
            Padding = new Padding(14, 10, 14, 8),
            ColumnCount = 6,
            RowCount = 7,
            AutoSize = false
        };
        // Columns: Label(auto) | Field(fill) | Label(auto) | Field(fill) | Label(auto) | Field(auto)
        tblBooking.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));   // Label
        tblBooking.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));    // Name
        tblBooking.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));   // Label
        tblBooking.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));    // Phone
        tblBooking.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 10));   // gap
        tblBooking.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));    // Repeat/extra

        tblBooking.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));  // Name/Phone
        tblBooking.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));  // Pickup
        tblBooking.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));  // Dropoff
        tblBooking.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));  // Pax/Vehicle/Time
        tblBooking.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));  // Resolved labels
        tblBooking.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));  // Quote row
        tblBooking.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));  // Action row

        // Row 0: Name + Phone
        tblBooking.Controls.Add(MakeLabel("Name:", 0, 0, 9.5F), 0, 0);
        txtCallerName = new TextBox { Dock = DockStyle.Fill, BackColor = bgInput, ForeColor = fgLight, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10F), PlaceholderText = "Customer name" };
        tblBooking.Controls.Add(txtCallerName, 1, 0);
        tblBooking.Controls.Add(MakeLabel("Phone:", 0, 0, 9.5F), 2, 0);
        txtPhone = new TextBox { Dock = DockStyle.Fill, BackColor = bgInput, ForeColor = fgLight, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10F), PlaceholderText = "+44…" };
        tblBooking.Controls.Add(txtPhone, 3, 0);
        btnRepeatLast = new Button { Text = "🔁 Last", Dock = DockStyle.Fill, BackColor = Color.FromArgb(52, 52, 62), ForeColor = fgLight, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8.5F), Visible = false, Cursor = Cursors.Hand };
        btnRepeatLast.FlatAppearance.BorderSize = 1;
        btnRepeatLast.FlatAppearance.BorderColor = sectionBorder;
        btnRepeatLast.Click += btnRepeatLast_Click;
        tblBooking.Controls.Add(btnRepeatLast, 5, 0);

        // Row 1: Pickup (span 4 cols)
        tblBooking.Controls.Add(MakeLabel("Pickup:", 0, 0, 9.5F), 0, 1);
        cmbPickup = new ComboBox { Dock = DockStyle.Fill, BackColor = bgInput, ForeColor = fgLight, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10F) };
        tblBooking.Controls.Add(cmbPickup, 1, 1);
        tblBooking.SetColumnSpan(cmbPickup, 3);
        lblPickupStatus = new Label { Text = "", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 14F), TextAlign = ContentAlignment.MiddleCenter };
        tblBooking.Controls.Add(lblPickupStatus, 5, 1);

        // Row 2: Dropoff (span 4 cols)
        tblBooking.Controls.Add(MakeLabel("Dropoff:", 0, 0, 9.5F), 0, 2);
        cmbDropoff = new ComboBox { Dock = DockStyle.Fill, BackColor = bgInput, ForeColor = fgLight, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10F) };
        tblBooking.Controls.Add(cmbDropoff, 1, 2);
        tblBooking.SetColumnSpan(cmbDropoff, 3);
        lblDropoffStatus = new Label { Text = "", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 14F), TextAlign = ContentAlignment.MiddleCenter };
        tblBooking.Controls.Add(lblDropoffStatus, 5, 2);

        // Row 3: Pax, Vehicle, Time
        tblBooking.Controls.Add(MakeLabel("Pax:", 0, 0, 9.5F), 0, 3);
        var pnlPaxVehicleTime = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = bgSection, WrapContents = false, AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty };
        nudPassengers = new NumericUpDown { Size = new Size(56, 30), Minimum = 1, Maximum = 16, Value = 1, BackColor = bgInput, ForeColor = fgLight, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10F) };
        var lblVeh = new Label { Text = "Vehicle:", Size = new Size(56, 28), ForeColor = fgLight, Font = new Font("Segoe UI", 9.5F), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(12, 0, 0, 0) };
        cmbVehicle = new ComboBox { Size = new Size(100, 28), BackColor = bgInput, ForeColor = fgLight, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10F), DropDownStyle = ComboBoxStyle.DropDownList };
        cmbVehicle.Items.AddRange(new object[] { "Saloon", "Estate", "MPV", "Minibus" });
        cmbVehicle.SelectedIndex = 0;
        var lblTm = new Label { Text = "Time:", Size = new Size(46, 28), ForeColor = fgLight, Font = new Font("Segoe UI", 9.5F), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(12, 0, 0, 0) };
        cmbPickupTime = new ComboBox { Size = new Size(90, 28), BackColor = bgInput, ForeColor = fgLight, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10F) };
        cmbPickupTime.Items.AddRange(new object[] { "ASAP", "15 min", "30 min", "1 hour" });
        cmbPickupTime.SelectedIndex = 0;
        pnlPaxVehicleTime.Controls.AddRange(new Control[] { nudPassengers, lblVeh, cmbVehicle, lblTm, cmbPickupTime });
        tblBooking.Controls.Add(pnlPaxVehicleTime, 1, 3);
        tblBooking.SetColumnSpan(pnlPaxVehicleTime, 5);

        // Row 4: Resolved labels (small)
        lblPickupResolved = new Label { Text = "", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(105, 170, 105), Font = new Font("Segoe UI", 7.5F, FontStyle.Italic), Padding = Padding.Empty };
        tblBooking.Controls.Add(lblPickupResolved, 1, 4);
        tblBooking.SetColumnSpan(lblPickupResolved, 3);
        lblDropoffResolved = new Label { Text = "", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(105, 170, 105), Font = new Font("Segoe UI", 7.5F, FontStyle.Italic) };
        tblBooking.Controls.Add(lblDropoffResolved, 5, 4);

        // Row 5: Quote row
        var pnlQuoteRow = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = bgSection, WrapContents = false, Padding = Padding.Empty };
        btnVerify = new Button { Text = "🔍 Get Quote", Size = new Size(120, 34), BackColor = accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold) };
        btnVerify.FlatAppearance.BorderSize = 0;
        btnVerify.Click += async (s, e) => await VerifyAndQuoteAsync();
        lblFare = new Label { Text = "Fare: —", Size = new Size(150, 34), ForeColor = Color.LimeGreen, Font = new Font("Segoe UI", 12F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(12, 0, 0, 0) };
        lblEta = new Label { Text = "ETA: —", Size = new Size(120, 34), ForeColor = Color.FromArgb(150, 195, 150), Font = new Font("Segoe UI", 9.5F), TextAlign = ContentAlignment.MiddleLeft };
        pnlQuoteRow.Controls.AddRange(new Control[] { btnVerify, lblFare, lblEta });
        tblBooking.Controls.Add(pnlQuoteRow, 0, 5);
        tblBooking.SetColumnSpan(pnlQuoteRow, 6);

        // Row 6: Action row
        var pnlActionRow = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = bgSection, WrapContents = false, Padding = Padding.Empty };
        btnDispatch = new Button { Text = "✅ Dispatch", Size = new Size(130, 36), BackColor = green, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 10.5F, FontStyle.Bold), Enabled = false };
        btnDispatch.FlatAppearance.BorderSize = 0;
        btnDispatch.Click += async (s, e) => await ConfirmBookingAsync();
        btnClearBooking = new Button { Text = "🗑 Clear", Size = new Size(90, 36), BackColor = Color.FromArgb(58, 58, 66), ForeColor = fgLight, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), Margin = new Padding(8, 0, 0, 0) };
        btnClearBooking.FlatAppearance.BorderSize = 1;
        btnClearBooking.FlatAppearance.BorderColor = sectionBorder;
        btnClearBooking.Click += (s, e) => ClearBookingForm();
        lblBookingStatus = new Label { Text = "", Size = new Size(260, 36), ForeColor = fgDim, Font = new Font("Segoe UI", 8.5F, FontStyle.Italic), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(10, 0, 0, 0) };
        pnlActionRow.Controls.AddRange(new Control[] { btnDispatch, btnClearBooking, lblBookingStatus });
        tblBooking.Controls.Add(pnlActionRow, 0, 6);
        tblBooking.SetColumnSpan(pnlActionRow, 6);

        pnlBooking.Controls.Add(tblBooking);
        pnlBooking.Controls.Add(lblBookingTitle);

        // ┌─────────────────────────────────────────┐
        // │  📊 JOBS                                │
        // └─────────────────────────────────────────┘
        pnlJobs = MakeSectionPanel(DockStyle.Fill, bgSection, sectionBorder);

        var lblJobsTitle = MakeSectionTitle("📊  JOBS", accent, headerBg);

        dgvJobs = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Color.FromArgb(22, 22, 26),
            ForeColor = fgLight,
            GridColor = Color.FromArgb(44, 46, 54),
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            RowHeadersVisible = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            Font = new Font("Segoe UI", 9F),
            RowTemplate = { Height = 30 },
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(22, 22, 26),
                ForeColor = fgLight,
                SelectionBackColor = Color.FromArgb(0, 75, 155),
                SelectionForeColor = Color.White,
                Padding = new Padding(4, 2, 4, 2)
            },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(36, 38, 46),
                ForeColor = Color.FromArgb(175, 195, 218),
                Font = new Font("Segoe UI Semibold", 9F),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                Padding = new Padding(4, 0, 0, 0)
            },
            ColumnHeadersHeight = 34,
            EnableHeadersVisualStyles = false
        };

        dgvJobs.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { Name = "ColRef", HeaderText = "Ref", Width = 70 },
            new DataGridViewTextBoxColumn { Name = "ColName", HeaderText = "Name", Width = 100 },
            new DataGridViewTextBoxColumn { Name = "ColPhone", HeaderText = "Phone", Width = 110 },
            new DataGridViewTextBoxColumn { Name = "ColPickup", HeaderText = "Pickup", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 120 },
            new DataGridViewTextBoxColumn { Name = "ColDropoff", HeaderText = "Dropoff", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 120 },
            new DataGridViewTextBoxColumn { Name = "ColPax", HeaderText = "P", Width = 32 },
            new DataGridViewTextBoxColumn { Name = "ColFare", HeaderText = "Fare", Width = 65 },
            new DataGridViewTextBoxColumn { Name = "ColStatus", HeaderText = "Status", Width = 75 },
            new DataGridViewTextBoxColumn { Name = "ColTime", HeaderText = "Time", Width = 60 }
        });

        pnlJobs.Controls.Add(dgvJobs);
        pnlJobs.Controls.Add(lblJobsTitle);

        splitLeftVert.Panel1.Controls.Add(pnlBooking);
        splitLeftVert.Panel2.Controls.Add(pnlJobs);
        pnlLeft.Controls.Add(splitLeftVert);

        // ══════════════════════════════════════════════════
        //  RIGHT PANEL — ADA/MAP + SIP + CALL + AUDIO
        //  Fixed width 380px, scrollable
        // ══════════════════════════════════════════════════
        var pnlRight = splitMain.Panel2;
        pnlRight.BackColor = bgDark;
        pnlRight.Padding = new Padding(5, 8, 10, 8);

        var pnlRightInner = new Panel { Dock = DockStyle.Fill, BackColor = bgDark, AutoScroll = true };

        // ┌─────────────────────────────┐
        // │  🤖 ADA                     │
        // └─────────────────────────────┘
        pnlAdaMap = MakeSectionPanel(DockStyle.Top, bgSection, sectionBorder);
        pnlAdaMap.Height = 260;
        pnlAdaMap.Margin = new Padding(0, 0, 0, 8);

        pnlAdaMapHeader = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = headerBg, Padding = new Padding(10, 0, 10, 0) };
        lblAdaMapTitle = new Label { Text = "🤖  ADA", Location = new Point(6, 5), AutoSize = true, ForeColor = Color.LimeGreen, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
        lblAdaMapStatus = new Label { Text = "Ready", Dock = DockStyle.Right, Width = 100, ForeColor = fgDim, Font = new Font("Segoe UI", 8.5F), TextAlign = ContentAlignment.MiddleRight };
        pnlAdaMapHeader.Controls.AddRange(new Control[] { lblAdaMapTitle, lblAdaMapStatus });

        pnlAdaMapHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black, Padding = new Padding(1) };

        pnlAdaMap.Controls.Add(pnlAdaMapHost);
        pnlAdaMap.Controls.Add(pnlAdaMapHeader);

        // ┌─────────────────────────────┐
        // │  📞 SIP CONNECTION          │
        // └─────────────────────────────┘
        pnlSip = MakeSectionPanel(DockStyle.Top, bgSection, sectionBorder);
        pnlSip.Height = 150;

        var lblSipTitle = MakeSectionTitle("📞  SIP CONNECTION", accent, headerBg);

        var pnlSipFields = new Panel { Dock = DockStyle.Fill, BackColor = bgSection, Padding = new Padding(12, 6, 12, 6) };

        int sy = 2;
        pnlSipFields.Controls.Add(MakeLabel("Account:", 0, sy + 3, 9F));
        cmbSipAccount = new ComboBox { Location = new Point(65, sy), Size = new Size(210, 26), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = bgInput, ForeColor = fgLight, Font = new Font("Segoe UI", 9F) };
        cmbSipAccount.SelectedIndexChanged += cmbSipAccount_SelectedIndexChanged;
        btnSaveSip = MakeButton("💾", 282, sy, 30, 26, Color.FromArgb(52, 52, 62));
        btnSaveSip.Font = new Font("Segoe UI", 9F);
        btnSaveSip.Click += btnSaveAccount_Click;
        pnlSipFields.Controls.AddRange(new Control[] { cmbSipAccount, btnSaveSip });
        sy += 32;

        pnlSipFields.Controls.Add(MakeLabel("Server:", 0, sy + 3, 9F));
        txtSipServer = MakeTextBox(65, sy, 155, bgInput, fgLight, 9F);
        txtSipServer.PlaceholderText = "sip.example.com";
        pnlSipFields.Controls.Add(MakeLabel("Ext:", 228, sy + 3, 9F));
        txtSipUser = MakeTextBox(258, sy, 60, bgInput, fgLight, 9F);
        pnlSipFields.Controls.AddRange(new Control[] { txtSipServer, txtSipUser });
        sy += 30;

        pnlSipFields.Controls.Add(MakeLabel("Pass:", 0, sy + 3, 9F));
        txtSipPassword = MakeTextBox(65, sy, 105, bgInput, fgLight, 9F);
        txtSipPassword.UseSystemPasswordChar = true;
        pnlSipFields.Controls.Add(MakeLabel("Port:", 180, sy + 3, 9F));
        txtSipPort = MakeTextBox(215, sy, 50, bgInput, fgLight, 9F);
        txtSipPort.Text = "5060";
        chkAutoAnswer = new CheckBox { Text = "Auto", Location = new Point(272, sy + 2), Size = new Size(50, 22), ForeColor = fgLight, Font = new Font("Segoe UI", 8.5F), Checked = true };
        pnlSipFields.Controls.AddRange(new Control[] { txtSipPassword, txtSipPort, chkAutoAnswer });
        sy += 30;

        btnConnect = MakeButton("▶ Connect", 0, sy, 100, 28, green);
        btnConnect.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        btnConnect.Click += btnConnect_Click;
        btnDisconnect = MakeButton("■ Stop", 106, sy, 72, 28, red);
        btnDisconnect.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        btnDisconnect.Enabled = false;
        btnDisconnect.Click += btnDisconnect_Click;
        lblSipStatus = new Label { Text = "● Offline", Location = new Point(184, sy + 5), Size = new Size(130, 20), ForeColor = fgDim, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        pnlSipFields.Controls.AddRange(new Control[] { btnConnect, btnDisconnect, lblSipStatus });

        pnlSip.Controls.Add(pnlSipFields);
        pnlSip.Controls.Add(lblSipTitle);

        // ┌─────────────────────────────┐
        // │  🎧 CALL CONTROLS           │
        // └─────────────────────────────┘
        pnlCall = MakeSectionPanel(DockStyle.Top, bgSection, sectionBorder);
        pnlCall.Height = 120;

        var lblCallTitle = MakeSectionTitle("🎧  CALL CONTROLS", accent, headerBg);

        var pnlCallFields = new Panel { Dock = DockStyle.Fill, BackColor = bgSection, Padding = new Padding(12, 6, 12, 6) };

        int cy = 0;
        btnAnswer = MakeButton("✅ Answer", 0, cy, 88, 30, green);
        btnAnswer.Enabled = false; btnAnswer.Click += btnAnswer_Click;
        btnReject = MakeButton("❌ Reject", 94, cy, 88, 30, red);
        btnReject.Enabled = false; btnReject.Click += btnReject_Click;
        btnHangUp = MakeButton("📴 Hang Up", 188, cy, 95, 30, Color.FromArgb(155, 42, 42));
        btnHangUp.Enabled = false; btnHangUp.Click += btnHangUp_Click;
        pnlCallFields.Controls.AddRange(new Control[] { btnAnswer, btnReject, btnHangUp });
        cy += 36;

        btnCallOut = MakeButton("📞 Call Out", 0, cy, 100, 30, accentDim);
        btnCallOut.Click += btnCallOut_Click;
        btnMute = MakeButton("🔊 Mute", 106, cy, 80, 30, Color.FromArgb(56, 56, 64));
        btnMute.Enabled = false; btnMute.Click += btnMute_Click;
        lblCallInfo = new Label { Text = "No call", Location = new Point(194, cy + 7), Size = new Size(120, 20), ForeColor = fgDim, Font = new Font("Segoe UI", 9F) };
        pnlCallFields.Controls.AddRange(new Control[] { btnCallOut, btnMute, lblCallInfo });
        cy += 36;

        chkManualMode = new CheckBox { Text = "🎤 Operator Mode", Location = new Point(0, cy), Size = new Size(150, 22), ForeColor = orange, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        chkManualMode.CheckedChanged += chkManualMode_CheckedChanged;
        pnlCallFields.Controls.Add(chkManualMode);

        pnlCall.Controls.Add(pnlCallFields);
        pnlCall.Controls.Add(lblCallTitle);

        // ┌─────────────────────────────┐
        // │  🔊 AUDIO / VOLUME          │
        // └─────────────────────────────┘
        pnlAudio = MakeSectionPanel(DockStyle.Top, bgSection, sectionBorder);
        pnlAudio.Height = 115;

        var lblAudioTitle = MakeSectionTitle("🔊  AUDIO / VOLUME", accent, headerBg);

        var pnlAudioFields = new Panel { Dock = DockStyle.Fill, BackColor = bgSection, Padding = new Padding(12, 8, 12, 8) };

        int ay = 0;
        pnlAudioFields.Controls.Add(MakeLabel("📱 Customer Vol:", 0, ay + 2, 9F));
        trkCustomerVolume = new TrackBar
        {
            Location = new Point(126, ay), Size = new Size(160, 30),
            Minimum = 0, Maximum = 100, Value = 80,
            TickFrequency = 20, SmallChange = 5, BackColor = bgSection
        };
        lblCustomerVolVal = new Label { Text = "80%", Location = new Point(292, ay + 2), Size = new Size(42, 20), ForeColor = Color.LimeGreen, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        trkCustomerVolume.ValueChanged += (s, e) => { lblCustomerVolVal.Text = $"{trkCustomerVolume.Value}%"; };
        pnlAudioFields.Controls.AddRange(new Control[] { trkCustomerVolume, lblCustomerVolVal });
        ay += 38;

        pnlAudioFields.Controls.Add(MakeLabel("🎧 Listener Vol:", 0, ay + 2, 9F));
        trkOpVolume = new TrackBar
        {
            Location = new Point(126, ay), Size = new Size(160, 30),
            Minimum = 10, Maximum = 60, Value = 20,
            TickFrequency = 10, SmallChange = 5, BackColor = bgSection
        };
        lblOpVolumeVal = new Label { Text = "2.0x", Location = new Point(292, ay + 2), Size = new Size(42, 20), ForeColor = Color.LimeGreen, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        trkOpVolume.ValueChanged += (s, e) =>
        {
            _operatorMicGain = trkOpVolume.Value / 10f;
            lblOpVolumeVal.Text = $"{_operatorMicGain:F1}x";
        };
        pnlAudioFields.Controls.AddRange(new Control[] { trkOpVolume, lblOpVolumeVal });

        pnlAudio.Controls.Add(pnlAudioFields);
        pnlAudio.Controls.Add(lblAudioTitle);

        // ── Assemble right panels with spacers ──
        var sp1 = new Panel { Dock = DockStyle.Top, Height = 8, BackColor = bgDark };
        var sp2 = new Panel { Dock = DockStyle.Top, Height = 8, BackColor = bgDark };
        var sp3 = new Panel { Dock = DockStyle.Top, Height = 8, BackColor = bgDark };

        pnlRightInner.Controls.Add(pnlAudio);
        pnlRightInner.Controls.Add(sp3);
        pnlRightInner.Controls.Add(pnlCall);
        pnlRightInner.Controls.Add(sp2);
        pnlRightInner.Controls.Add(pnlSip);
        pnlRightInner.Controls.Add(sp1);
        pnlRightInner.Controls.Add(pnlAdaMap);

        pnlRight.Controls.Add(pnlRightInner);

        // ══════════════════════════════════════
        //  LOG PANEL (toggleable, bottom)
        // ══════════════════════════════════════
        pnlLog = new Panel { Dock = DockStyle.Bottom, Height = 160, BackColor = bgPanel, Visible = false, Padding = new Padding(10, 4, 10, 4) };
        txtLog = new RichTextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Cascadia Mono", 9F),
            BackColor = Color.FromArgb(14, 14, 16),
            ForeColor = Color.LightGreen,
            ReadOnly = true,
            BorderStyle = BorderStyle.None
        };
        var lblLogTitle = MakeSectionTitle("📋  LOG", accent, headerBg);
        pnlLog.Controls.Add(txtLog);
        pnlLog.Controls.Add(lblLogTitle);

        // ══════════════════════════════════════
        //  STATUS BAR
        // ══════════════════════════════════════
        statusStrip = new StatusStrip { BackColor = Color.FromArgb(22, 22, 26), ForeColor = fgLight, SizingGrip = true };
        statusLabel = new ToolStripStatusLabel("Ready") { ForeColor = fgLight };
        statusCallId = new ToolStripStatusLabel("") { Spring = true, TextAlign = ContentAlignment.MiddleRight, ForeColor = fgLight };
        statusStrip.Items.AddRange(new ToolStripItem[] { statusLabel, statusCallId });

        // ══════════════════════════════════════
        //  FORM
        // ══════════════════════════════════════
        this.AutoScaleDimensions = new SizeF(7F, 15F);
        this.AutoScaleMode = AutoScaleMode.Font;
        this.ClientSize = new Size(1280, 800);
        this.MinimumSize = new Size(1100, 720);
        this.Text = "🚕 AdaSdkBooker — AI Taxi Booking System v1.0";
        this.StartPosition = FormStartPosition.CenterScreen;
        this.Font = new Font("Segoe UI", 9F);
        this.BackColor = bgDark;
        this.ForeColor = fgLight;

        // Set splitter after form size is set
        splitMain.SplitterDistance = this.ClientSize.Width - 380;

        this.Controls.Add(splitMain);
        this.Controls.Add(pnlLog);
        this.Controls.Add(toolStrip);
        this.Controls.Add(statusStrip);

        this.ResumeLayout(false);
        this.PerformLayout();
    }

    #endregion

    // ── Helpers ──
    private static Panel MakeSectionPanel(DockStyle dock, Color bg, Color border)
    {
        return new BorderedPanel
        {
            Dock = dock,
            BackColor = bg,
            Padding = new Padding(2),
            Margin = new Padding(0, 0, 0, 2),
            BorderColor = border
        };
    }

    private class BorderedPanel : Panel
    {
        public Color BorderColor { get; set; } = Color.FromArgb(52, 56, 68);
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(BorderColor, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    private static Label MakeSectionTitle(string text, Color fg, Color bg)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Top,
            Height = 30,
            ForeColor = fg,
            Font = new Font("Segoe UI Semibold", 10F),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
            BackColor = bg
        };
    }

    private static Label MakeLabel(string text, int x, int y, float fontSize = 9F)
        => new Label { Text = text, Location = new Point(x, y), AutoSize = true, Font = new Font("Segoe UI", fontSize) };

    private static TextBox MakeTextBox(int x, int y, int w, Color bg, Color fg, float fontSize = 9F)
        => new TextBox { Location = new Point(x, y), Size = new Size(w, 28), BackColor = bg, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", fontSize) };

    private static ComboBox MakeComboBox(int x, int y, int w, Color bg, Color fg)
        => new ComboBox { Location = new Point(x, y), Size = new Size(w, 28), BackColor = bg, ForeColor = fg, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9.5F) };

    private static Button MakeButton(string text, int x, int y, int w, int h, Color bg)
    {
        var btn = new Button
        {
            Text = text, Location = new Point(x, y), Size = new Size(w, h),
            BackColor = bg, ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };
        btn.FlatAppearance.BorderSize = 1;
        btn.FlatAppearance.BorderColor = Color.FromArgb(65, 70, 82);
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
    private Button btnAnswer, btnReject, btnHangUp, btnMute, btnCallOut;
    private CheckBox chkManualMode;
    private Label lblCallInfo;

    // Audio / Volume
    private Panel pnlAudio;
    private TrackBar trkCustomerVolume, trkOpVolume;
    private Label lblCustomerVolVal, lblOpVolumeVal;

    // Log
    private Panel pnlLog;
    private RichTextBox txtLog;

    // Status bar
    private StatusStrip statusStrip;
    private ToolStripStatusLabel statusLabel, statusCallId;
}
