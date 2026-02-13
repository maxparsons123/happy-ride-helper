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
        //  TOOLSTRIP
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
        //  MAIN SPLIT — LEFT (centre) | RIGHT (controls)
        //  Right panel fixed at 380px
        // ══════════════════════════════════════
        splitMain = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 5,
            BackColor = bgDark,
            BorderStyle = BorderStyle.None,
            FixedPanel = FixedPanel.Panel2,
            Panel1MinSize = 100,
            Panel2MinSize = 100
        };

        // ══════════════════════════════════════════════════
        //  LEFT PANEL — top: [Booking | Map]  bottom: [Jobs]
        // ══════════════════════════════════════════════════
        var pnlLeft = splitMain.Panel1;
        pnlLeft.BackColor = bgDark;
        pnlLeft.Padding = new Padding(8, 6, 4, 6);

        // Vertical split: top = booking+map row, bottom = jobs grid
        splitLeftVert = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 290,
            SplitterWidth = 6,
            BackColor = bgDark,
            BorderStyle = BorderStyle.None
        };

        // ── TOP ROW: Booking Form | Map (side by side) ──
        splitBookingMap = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 5,
            BackColor = bgDark,
            BorderStyle = BorderStyle.None,
            FixedPanel = FixedPanel.Panel2,
            Panel1MinSize = 100,
            Panel2MinSize = 100
        };

        // ┌─────────────────────────────────────────┐
        // │  📋 NEW BOOKING                         │
        // └─────────────────────────────────────────┘
        pnlBooking = MakeSectionPanel(DockStyle.Fill, bgSection, sectionBorder);
        var lblBookingTitle = MakeSectionTitle("📋  NEW BOOKING", accent, headerBg);

        var tblBooking = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = bgSection,
            Padding = new Padding(12, 8, 12, 6),
            ColumnCount = 6,
            RowCount = 7,
            AutoSize = false
        };
        tblBooking.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        tblBooking.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        tblBooking.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
        tblBooking.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        tblBooking.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
        tblBooking.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));

        tblBooking.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));  // Name/Phone
        tblBooking.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));  // Pickup
        tblBooking.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));  // Dropoff
        tblBooking.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));  // Pax/Vehicle/Time
        tblBooking.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));  // Resolved
        tblBooking.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));  // Quote
        tblBooking.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));  // Actions

        // Row 0: Name + Phone
        tblBooking.Controls.Add(MakeLabel("Name:", 0, 0, 9F), 0, 0);
        txtCallerName = new TextBox { Dock = DockStyle.Fill, BackColor = bgInput, ForeColor = fgLight, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9.5F), PlaceholderText = "Customer name" };
        tblBooking.Controls.Add(txtCallerName, 1, 0);
        tblBooking.Controls.Add(MakeLabel("Phone:", 0, 0, 9F), 2, 0);
        txtPhone = new TextBox { Dock = DockStyle.Fill, BackColor = bgInput, ForeColor = fgLight, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9.5F), PlaceholderText = "+44…" };
        tblBooking.Controls.Add(txtPhone, 3, 0);
        btnRepeatLast = new Button { Text = "🔁", Dock = DockStyle.Fill, BackColor = Color.FromArgb(52, 52, 62), ForeColor = fgLight, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9F), Visible = false, Cursor = Cursors.Hand };
        btnRepeatLast.FlatAppearance.BorderSize = 1;
        btnRepeatLast.FlatAppearance.BorderColor = sectionBorder;
        btnRepeatLast.Click += btnRepeatLast_Click;
        tblBooking.Controls.Add(btnRepeatLast, 5, 0);

        // Row 1: Pickup
        tblBooking.Controls.Add(MakeLabel("Pickup:", 0, 0, 9F), 0, 1);
        cmbPickup = new ComboBox { Dock = DockStyle.Fill, BackColor = bgInput, ForeColor = fgLight, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9.5F) };
        tblBooking.Controls.Add(cmbPickup, 1, 1);
        tblBooking.SetColumnSpan(cmbPickup, 3);
        lblPickupStatus = new Label { Text = "", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 13F), TextAlign = ContentAlignment.MiddleCenter };
        tblBooking.Controls.Add(lblPickupStatus, 5, 1);

        // Row 2: Dropoff
        tblBooking.Controls.Add(MakeLabel("Dropoff:", 0, 0, 9F), 0, 2);
        cmbDropoff = new ComboBox { Dock = DockStyle.Fill, BackColor = bgInput, ForeColor = fgLight, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9.5F) };
        tblBooking.Controls.Add(cmbDropoff, 1, 2);
        tblBooking.SetColumnSpan(cmbDropoff, 3);
        lblDropoffStatus = new Label { Text = "", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 13F), TextAlign = ContentAlignment.MiddleCenter };
        tblBooking.Controls.Add(lblDropoffStatus, 5, 2);

        // Row 3: Pax, Vehicle, Time
        tblBooking.Controls.Add(MakeLabel("Pax:", 0, 0, 9F), 0, 3);
        var pnlPaxVehicleTime = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = bgSection, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
        nudPassengers = new NumericUpDown { Size = new Size(52, 26), Minimum = 1, Maximum = 16, Value = 1, BackColor = bgInput, ForeColor = fgLight, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9.5F) };
        var lblVeh = new Label { Text = "Vehicle:", Size = new Size(52, 26), ForeColor = fgLight, Font = new Font("Segoe UI", 9F), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(10, 0, 0, 0) };
        cmbVehicle = new ComboBox { Size = new Size(90, 26), BackColor = bgInput, ForeColor = fgLight, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9.5F), DropDownStyle = ComboBoxStyle.DropDownList };
        cmbVehicle.Items.AddRange(new object[] { "Saloon", "Estate", "MPV", "Minibus" });
        cmbVehicle.SelectedIndex = 0;
        var lblTm = new Label { Text = "Time:", Size = new Size(42, 26), ForeColor = fgLight, Font = new Font("Segoe UI", 9F), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(10, 0, 0, 0) };
        cmbPickupTime = new ComboBox { Size = new Size(82, 26), BackColor = bgInput, ForeColor = fgLight, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9.5F) };
        cmbPickupTime.Items.AddRange(new object[] { "ASAP", "15 min", "30 min", "1 hour" });
        cmbPickupTime.SelectedIndex = 0;
        pnlPaxVehicleTime.Controls.AddRange(new Control[] { nudPassengers, lblVeh, cmbVehicle, lblTm, cmbPickupTime });
        tblBooking.Controls.Add(pnlPaxVehicleTime, 1, 3);
        tblBooking.SetColumnSpan(pnlPaxVehicleTime, 5);

        // Row 4: Resolved labels
        lblPickupResolved = new Label { Text = "", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(105, 170, 105), Font = new Font("Segoe UI", 7.5F, FontStyle.Italic) };
        tblBooking.Controls.Add(lblPickupResolved, 1, 4);
        tblBooking.SetColumnSpan(lblPickupResolved, 3);
        lblDropoffResolved = new Label { Text = "", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(105, 170, 105), Font = new Font("Segoe UI", 7.5F, FontStyle.Italic) };
        tblBooking.Controls.Add(lblDropoffResolved, 5, 4);

        // Row 5: Quote
        var pnlQuoteRow = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = bgSection, WrapContents = false, Padding = Padding.Empty };
        btnVerify = new Button { Text = "🔍 Quote", Size = new Size(90, 30), BackColor = accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        btnVerify.FlatAppearance.BorderSize = 0;
        btnVerify.Click += async (s, e) => await VerifyAndQuoteAsync();
        lblFare = new Label { Text = "Fare: —", Size = new Size(130, 30), ForeColor = Color.LimeGreen, Font = new Font("Segoe UI", 11F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(8, 0, 0, 0) };
        lblEta = new Label { Text = "ETA: —", Size = new Size(110, 30), ForeColor = Color.FromArgb(150, 195, 150), Font = new Font("Segoe UI", 9F), TextAlign = ContentAlignment.MiddleLeft };
        pnlQuoteRow.Controls.AddRange(new Control[] { btnVerify, lblFare, lblEta });
        tblBooking.Controls.Add(pnlQuoteRow, 0, 5);
        tblBooking.SetColumnSpan(pnlQuoteRow, 6);

        // Row 6: Actions
        var pnlActionRow = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = bgSection, WrapContents = false, Padding = Padding.Empty };
        btnDispatch = new Button { Text = "✅ Dispatch", Size = new Size(110, 34), BackColor = green, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 10F, FontStyle.Bold), Enabled = false };
        btnDispatch.FlatAppearance.BorderSize = 0;
        btnDispatch.Click += async (s, e) => await ConfirmBookingAsync();
        btnClearBooking = new Button { Text = "🗑 Clear", Size = new Size(80, 34), BackColor = Color.FromArgb(58, 58, 66), ForeColor = fgLight, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Margin = new Padding(6, 0, 0, 0) };
        btnClearBooking.FlatAppearance.BorderSize = 1;
        btnClearBooking.FlatAppearance.BorderColor = sectionBorder;
        btnClearBooking.Click += (s, e) => ClearBookingForm();
        lblBookingStatus = new Label { Text = "", Size = new Size(200, 34), ForeColor = fgDim, Font = new Font("Segoe UI", 8.5F, FontStyle.Italic), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(8, 0, 0, 0) };
        pnlActionRow.Controls.AddRange(new Control[] { btnDispatch, btnClearBooking, lblBookingStatus });
        tblBooking.Controls.Add(pnlActionRow, 0, 6);
        tblBooking.SetColumnSpan(pnlActionRow, 6);

        pnlBooking.Controls.Add(tblBooking);
        pnlBooking.Controls.Add(lblBookingTitle);

        // ┌─────────────────────────────────────────┐
        // │  🗺️ MAP                                 │
        // └─────────────────────────────────────────┘
        pnlMap = MakeSectionPanel(DockStyle.Fill, bgSection, sectionBorder);
        var lblMapTitle = MakeSectionTitle("🗺️  MAP", accent, headerBg);
        pnlMapHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(22, 24, 28), Padding = new Padding(1) };
        // Placeholder label until WebView2 is loaded
        var lblMapPlaceholder = new Label
        {
            Text = "Map will load here\nwhen addresses are entered",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(80, 85, 95),
            Font = new Font("Segoe UI", 10F, FontStyle.Italic),
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(22, 24, 28)
        };
        pnlMapHost.Controls.Add(lblMapPlaceholder);
        pnlMap.Controls.Add(pnlMapHost);
        pnlMap.Controls.Add(lblMapTitle);

        // Assemble top row: Booking (left) | Map (right)
        splitBookingMap.Panel1.Controls.Add(pnlBooking);
        splitBookingMap.Panel2.Controls.Add(pnlMap);

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
            RowTemplate = { Height = 28 },
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
            ColumnHeadersHeight = 32,
            EnableHeadersVisualStyles = false
        };

        dgvJobs.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { Name = "ColRef", HeaderText = "Ref", Width = 65 },
            new DataGridViewTextBoxColumn { Name = "ColName", HeaderText = "Name", Width = 95 },
            new DataGridViewTextBoxColumn { Name = "ColPhone", HeaderText = "Phone", Width = 105 },
            new DataGridViewTextBoxColumn { Name = "ColPickup", HeaderText = "Pickup", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 120 },
            new DataGridViewTextBoxColumn { Name = "ColDropoff", HeaderText = "Dropoff", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 120 },
            new DataGridViewTextBoxColumn { Name = "ColPax", HeaderText = "P", Width = 30 },
            new DataGridViewTextBoxColumn { Name = "ColFare", HeaderText = "Fare", Width = 60 },
            new DataGridViewTextBoxColumn { Name = "ColStatus", HeaderText = "Status", Width = 70 },
            new DataGridViewTextBoxColumn { Name = "ColTime", HeaderText = "Time", Width = 55 }
        });

        pnlJobs.Controls.Add(dgvJobs);
        pnlJobs.Controls.Add(lblJobsTitle);

        // Assemble left: top row + jobs
        splitLeftVert.Panel1.Controls.Add(splitBookingMap);
        splitLeftVert.Panel2.Controls.Add(pnlJobs);
        pnlLeft.Controls.Add(splitLeftVert);

        // ══════════════════════════════════════════════════
        //  RIGHT PANEL — ADA + SIP + CALL + AUDIO
        //  Fixed 380px, scrollable
        // ══════════════════════════════════════════════════
        var pnlRight = splitMain.Panel2;
        pnlRight.BackColor = bgDark;
        pnlRight.Padding = new Padding(4, 6, 8, 6);

        var pnlRightInner = new Panel { Dock = DockStyle.Fill, BackColor = bgDark, AutoScroll = true };

        // ┌─────────────────────────────┐
        // │  🤖 ADA                     │
        // └─────────────────────────────┘
        pnlAdaMap = MakeSectionPanel(DockStyle.Top, bgSection, sectionBorder);
        pnlAdaMap.Height = 220;

        pnlAdaMapHeader = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = headerBg, Padding = new Padding(10, 0, 10, 0) };
        lblAdaMapTitle = new Label { Text = "🤖  ADA", Location = new Point(6, 4), AutoSize = true, ForeColor = Color.LimeGreen, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
        lblAdaMapStatus = new Label { Text = "Ready", Dock = DockStyle.Right, Width = 90, ForeColor = fgDim, Font = new Font("Segoe UI", 8.5F), TextAlign = ContentAlignment.MiddleRight };
        pnlAdaMapHeader.Controls.AddRange(new Control[] { lblAdaMapTitle, lblAdaMapStatus });

        pnlAdaMapHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black, Padding = new Padding(1) };
        pnlAdaMap.Controls.Add(pnlAdaMapHost);
        pnlAdaMap.Controls.Add(pnlAdaMapHeader);

        // ┌─────────────────────────────┐
        // │  📞 SIP CONNECTION          │
        // └─────────────────────────────┘
        pnlSip = MakeSectionPanel(DockStyle.Top, bgSection, sectionBorder);
        pnlSip.Height = 148;

        var lblSipTitle = MakeSectionTitle("📞  SIP CONNECTION", accent, headerBg);
        var pnlSipFields = new Panel { Dock = DockStyle.Fill, BackColor = bgSection, Padding = new Padding(12, 6, 12, 4) };

        int sy = 0;
        pnlSipFields.Controls.Add(MakeLabel("Account:", 0, sy + 3, 8.5F));
        cmbSipAccount = new ComboBox { Location = new Point(62, sy), Size = new Size(210, 24), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = bgInput, ForeColor = fgLight, Font = new Font("Segoe UI", 8.5F) };
        cmbSipAccount.SelectedIndexChanged += cmbSipAccount_SelectedIndexChanged;
        btnSaveSip = MakeButton("💾", 278, sy, 28, 24, Color.FromArgb(52, 52, 62));
        btnSaveSip.Font = new Font("Segoe UI", 8.5F);
        btnSaveSip.Click += btnSaveAccount_Click;
        pnlSipFields.Controls.AddRange(new Control[] { cmbSipAccount, btnSaveSip });
        sy += 28;

        pnlSipFields.Controls.Add(MakeLabel("Server:", 0, sy + 3, 8.5F));
        txtSipServer = MakeTextBox(62, sy, 150, bgInput, fgLight, 8.5F);
        txtSipServer.PlaceholderText = "sip.example.com";
        pnlSipFields.Controls.Add(MakeLabel("Ext:", 220, sy + 3, 8.5F));
        txtSipUser = MakeTextBox(248, sy, 60, bgInput, fgLight, 8.5F);
        pnlSipFields.Controls.AddRange(new Control[] { txtSipServer, txtSipUser });
        sy += 28;

        pnlSipFields.Controls.Add(MakeLabel("Pass:", 0, sy + 3, 8.5F));
        txtSipPassword = MakeTextBox(62, sy, 100, bgInput, fgLight, 8.5F);
        txtSipPassword.UseSystemPasswordChar = true;
        pnlSipFields.Controls.Add(MakeLabel("Port:", 172, sy + 3, 8.5F));
        txtSipPort = MakeTextBox(205, sy, 48, bgInput, fgLight, 8.5F);
        txtSipPort.Text = "5060";
        chkAutoAnswer = new CheckBox { Text = "Auto", Location = new Point(260, sy + 2), Size = new Size(50, 22), ForeColor = fgLight, Font = new Font("Segoe UI", 8F), Checked = true };
        pnlSipFields.Controls.AddRange(new Control[] { txtSipPassword, txtSipPort, chkAutoAnswer });
        sy += 28;

        btnConnect = MakeButton("▶ Connect", 0, sy, 95, 26, green);
        btnConnect.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        btnConnect.Click += btnConnect_Click;
        btnDisconnect = MakeButton("■ Stop", 100, sy, 68, 26, red);
        btnDisconnect.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        btnDisconnect.Enabled = false;
        btnDisconnect.Click += btnDisconnect_Click;
        lblSipStatus = new Label { Text = "● Offline", Location = new Point(174, sy + 4), Size = new Size(130, 18), ForeColor = fgDim, Font = new Font("Segoe UI", 8.5F, FontStyle.Bold) };
        pnlSipFields.Controls.AddRange(new Control[] { btnConnect, btnDisconnect, lblSipStatus });

        pnlSip.Controls.Add(pnlSipFields);
        pnlSip.Controls.Add(lblSipTitle);

        // ┌─────────────────────────────┐
        // │  🎧 CALL CONTROLS           │
        // └─────────────────────────────┘
        pnlCall = MakeSectionPanel(DockStyle.Top, bgSection, sectionBorder);
        pnlCall.Height = 118;

        var lblCallTitle = MakeSectionTitle("🎧  CALL CONTROLS", accent, headerBg);
        var pnlCallFields = new Panel { Dock = DockStyle.Fill, BackColor = bgSection, Padding = new Padding(12, 5, 12, 4) };

        int cy = 0;
        btnAnswer = MakeButton("✅ Answer", 0, cy, 84, 28, green);
        btnAnswer.Enabled = false; btnAnswer.Click += btnAnswer_Click;
        btnReject = MakeButton("❌ Reject", 90, cy, 84, 28, red);
        btnReject.Enabled = false; btnReject.Click += btnReject_Click;
        btnHangUp = MakeButton("📴 Hang Up", 180, cy, 90, 28, Color.FromArgb(155, 42, 42));
        btnHangUp.Enabled = false; btnHangUp.Click += btnHangUp_Click;
        pnlCallFields.Controls.AddRange(new Control[] { btnAnswer, btnReject, btnHangUp });
        cy += 33;

        btnCallOut = MakeButton("📞 Call Out", 0, cy, 96, 28, accentDim);
        btnCallOut.Click += btnCallOut_Click;
        btnMute = MakeButton("🔊 Mute", 102, cy, 76, 28, Color.FromArgb(56, 56, 64));
        btnMute.Enabled = false; btnMute.Click += btnMute_Click;
        lblCallInfo = new Label { Text = "No call", Location = new Point(184, cy + 6), Size = new Size(120, 18), ForeColor = fgDim, Font = new Font("Segoe UI", 8.5F) };
        pnlCallFields.Controls.AddRange(new Control[] { btnCallOut, btnMute, lblCallInfo });
        cy += 33;

        chkManualMode = new CheckBox { Text = "🎤 Operator Mode", Location = new Point(0, cy), Size = new Size(148, 22), ForeColor = orange, Font = new Font("Segoe UI", 8.5F, FontStyle.Bold) };
        chkManualMode.CheckedChanged += chkManualMode_CheckedChanged;
        pnlCallFields.Controls.Add(chkManualMode);

        pnlCall.Controls.Add(pnlCallFields);
        pnlCall.Controls.Add(lblCallTitle);

        // ┌─────────────────────────────┐
        // │  🔊 AUDIO / VOLUME          │
        // └─────────────────────────────┘
        pnlAudio = MakeSectionPanel(DockStyle.Top, bgSection, sectionBorder);
        pnlAudio.Height = 110;

        var lblAudioTitle = MakeSectionTitle("🔊  AUDIO / VOLUME", accent, headerBg);
        var pnlAudioFields = new Panel { Dock = DockStyle.Fill, BackColor = bgSection, Padding = new Padding(12, 6, 12, 6) };

        int ay = 0;
        pnlAudioFields.Controls.Add(MakeLabel("📱 Customer:", 0, ay + 2, 8.5F));
        trkCustomerVolume = new TrackBar
        {
            Location = new Point(100, ay), Size = new Size(170, 28),
            Minimum = 0, Maximum = 100, Value = 80,
            TickFrequency = 20, SmallChange = 5, BackColor = bgSection
        };
        lblCustomerVolVal = new Label { Text = "80%", Location = new Point(276, ay + 2), Size = new Size(40, 18), ForeColor = Color.LimeGreen, Font = new Font("Segoe UI", 8.5F, FontStyle.Bold) };
        trkCustomerVolume.ValueChanged += (s, e) => { lblCustomerVolVal.Text = $"{trkCustomerVolume.Value}%"; };
        pnlAudioFields.Controls.AddRange(new Control[] { trkCustomerVolume, lblCustomerVolVal });
        ay += 34;

        pnlAudioFields.Controls.Add(MakeLabel("🎧 Listener:", 0, ay + 2, 8.5F));
        trkOpVolume = new TrackBar
        {
            Location = new Point(100, ay), Size = new Size(170, 28),
            Minimum = 10, Maximum = 60, Value = 20,
            TickFrequency = 10, SmallChange = 5, BackColor = bgSection
        };
        lblOpVolumeVal = new Label { Text = "2.0x", Location = new Point(276, ay + 2), Size = new Size(40, 18), ForeColor = Color.LimeGreen, Font = new Font("Segoe UI", 8.5F, FontStyle.Bold) };
        trkOpVolume.ValueChanged += (s, e) =>
        {
            _operatorMicGain = trkOpVolume.Value / 10f;
            lblOpVolumeVal.Text = $"{_operatorMicGain:F1}x";
        };
        pnlAudioFields.Controls.AddRange(new Control[] { trkOpVolume, lblOpVolumeVal });

        pnlAudio.Controls.Add(pnlAudioFields);
        pnlAudio.Controls.Add(lblAudioTitle);

        // ── Assemble right panels (reverse dock order) ──
        var sp1 = new Panel { Dock = DockStyle.Top, Height = 6, BackColor = bgDark };
        var sp2 = new Panel { Dock = DockStyle.Top, Height = 6, BackColor = bgDark };
        var sp3 = new Panel { Dock = DockStyle.Top, Height = 6, BackColor = bgDark };

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
            Font = new Font("Cascadia Mono", 8.5F),
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
        this.ClientSize = new Size(1360, 820);
        this.MinimumSize = new Size(1150, 720);
        this.Text = "🚕 AdaSdkBooker — AI Taxi Booking System v1.0";
        this.StartPosition = FormStartPosition.CenterScreen;
        this.Font = new Font("Segoe UI", 9F);
        this.BackColor = bgDark;
        this.ForeColor = fgLight;

        // Defer splitter distances to after layout is complete
        this.Load += (s, e) =>
        {
            splitMain.SplitterDistance = Math.Max(splitMain.Panel1MinSize, this.ClientSize.Width - 380);
            // Booking form: ~280px, Map: remainder of width
            splitBookingMap.SplitterDistance = Math.Max(200, 280);
        };

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
            Height = 28,
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
        => new TextBox { Location = new Point(x, y), Size = new Size(w, 26), BackColor = bg, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", fontSize) };

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

    private SplitContainer splitMain, splitLeftVert, splitBookingMap;

    // Booking form
    private Panel pnlBooking;
    private TextBox txtCallerName, txtPhone;
    private ComboBox cmbPickup, cmbDropoff, cmbVehicle, cmbPickupTime;
    private NumericUpDown nudPassengers;
    private Label lblPickupStatus, lblDropoffStatus, lblPickupResolved, lblDropoffResolved;
    private Label lblFare, lblEta, lblBookingStatus;
    private Button btnVerify, btnDispatch, btnClearBooking, btnRepeatLast;

    // Map panel
    private Panel pnlMap, pnlMapHost;

    // Job grid
    private Panel pnlJobs;
    private DataGridView dgvJobs;

    // Ada view
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
