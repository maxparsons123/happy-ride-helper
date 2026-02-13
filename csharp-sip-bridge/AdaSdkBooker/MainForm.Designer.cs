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
        var bgDark = Color.FromArgb(20, 20, 24);
        var bgPanel = Color.FromArgb(30, 30, 36);
        var bgSection = Color.FromArgb(38, 38, 44);
        var bgInput = Color.FromArgb(50, 50, 58);
        var fgLight = Color.FromArgb(225, 225, 230);
        var accent = Color.FromArgb(70, 140, 220);
        var accentDim = Color.FromArgb(50, 100, 170);
        var green = Color.FromArgb(40, 170, 70);
        var red = Color.FromArgb(195, 50, 50);
        var orange = Color.FromArgb(245, 160, 20);
        var borderColor = Color.FromArgb(55, 58, 68);
        var sectionBorder = Color.FromArgb(60, 65, 78);

        // ══════════════════════════════════════
        //  TOOLSTRIP (top bar)
        // ══════════════════════════════════════
        toolStrip = new ToolStrip
        {
            BackColor = Color.FromArgb(26, 26, 30),
            ForeColor = fgLight,
            GripStyle = ToolStripGripStyle.Hidden,
            Padding = new Padding(8, 2, 8, 2),
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
        //  MAIN SPLIT — LEFT (booking+jobs) | RIGHT (ada+sip+call+audio)
        // ══════════════════════════════════════
        splitMain = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 560,
            SplitterWidth = 4,
            BackColor = bgDark,
            BorderStyle = BorderStyle.None,
            FixedPanel = FixedPanel.None
        };

        // ══════════════════════════════════════════════════
        //  LEFT PANEL — BOOKING FORM + JOB GRID
        // ══════════════════════════════════════════════════
        var pnlLeft = splitMain.Panel1;
        pnlLeft.BackColor = bgDark;
        pnlLeft.Padding = new Padding(8, 6, 4, 6);

        splitLeftVert = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 360,
            SplitterWidth = 6,
            BackColor = bgDark,
            BorderStyle = BorderStyle.None
        };

        // ┌───────────────────────────────────┐
        // │  📋 NEW BOOKING                   │
        // └───────────────────────────────────┘
        pnlBooking = MakeSectionPanel(DockStyle.Fill, bgSection, sectionBorder);

        var lblBookingTitle = MakeSectionTitle("📋  NEW BOOKING", accent);

        var pnlBookingFields = new Panel { Dock = DockStyle.Fill, BackColor = bgSection, Padding = new Padding(12, 8, 12, 8) };

        int y = 4;
        // Row 1: Name + Phone + Repeat
        pnlBookingFields.Controls.Add(MakeLabel("Name:", 0, y + 4, 9F));
        txtCallerName = MakeTextBox(70, y, 195, bgInput, fgLight, 9.5F);
        txtCallerName.PlaceholderText = "Customer name";
        txtCallerName.Height = 28;
        pnlBookingFields.Controls.Add(MakeLabel("Phone:", 280, y + 4, 9F));
        txtPhone = MakeTextBox(340, y, 160, bgInput, fgLight, 9.5F);
        txtPhone.PlaceholderText = "+44…";
        txtPhone.Height = 28;
        btnRepeatLast = MakeButton("🔁 Last", 510, y, 65, 28, Color.FromArgb(55, 55, 68));
        btnRepeatLast.Font = new Font("Segoe UI", 8F);
        btnRepeatLast.Visible = false;
        btnRepeatLast.Click += btnRepeatLast_Click;
        pnlBookingFields.Controls.AddRange(new Control[] { txtCallerName, txtPhone, btnRepeatLast });
        y += 38;

        // Row 2: Pickup
        pnlBookingFields.Controls.Add(MakeLabel("Pickup:", 0, y + 4, 9F));
        cmbPickup = MakeComboBox(70, y, 430, bgInput, fgLight);
        cmbPickup.Font = new Font("Segoe UI", 9.5F);
        cmbPickup.Height = 28;
        lblPickupStatus = new Label { Text = "", Location = new Point(510, y + 2), Size = new Size(26, 26), Font = new Font("Segoe UI", 13F) };
        pnlBookingFields.Controls.AddRange(new Control[] { cmbPickup, lblPickupStatus });
        y += 30;
        lblPickupResolved = new Label { Text = "", Location = new Point(70, y), Size = new Size(430, 16), ForeColor = Color.FromArgb(110, 175, 110), Font = new Font("Segoe UI", 7.8F, FontStyle.Italic) };
        pnlBookingFields.Controls.Add(lblPickupResolved);
        y += 20;

        // Row 3: Dropoff
        pnlBookingFields.Controls.Add(MakeLabel("Dropoff:", 0, y + 4, 9F));
        cmbDropoff = MakeComboBox(70, y, 430, bgInput, fgLight);
        cmbDropoff.Font = new Font("Segoe UI", 9.5F);
        cmbDropoff.Height = 28;
        lblDropoffStatus = new Label { Text = "", Location = new Point(510, y + 2), Size = new Size(26, 26), Font = new Font("Segoe UI", 13F) };
        pnlBookingFields.Controls.AddRange(new Control[] { cmbDropoff, lblDropoffStatus });
        y += 30;
        lblDropoffResolved = new Label { Text = "", Location = new Point(70, y), Size = new Size(430, 16), ForeColor = Color.FromArgb(110, 175, 110), Font = new Font("Segoe UI", 7.8F, FontStyle.Italic) };
        pnlBookingFields.Controls.Add(lblDropoffResolved);
        y += 22;

        // Row 4: Pax, Vehicle, Time
        pnlBookingFields.Controls.Add(MakeLabel("Pax:", 0, y + 4, 9F));
        nudPassengers = new NumericUpDown { Location = new Point(70, y), Size = new Size(60, 28), Minimum = 1, Maximum = 16, Value = 1, BackColor = bgInput, ForeColor = fgLight, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9.5F) };
        pnlBookingFields.Controls.Add(MakeLabel("Vehicle:", 145, y + 4, 9F));
        cmbVehicle = MakeComboBox(210, y, 130, bgInput, fgLight);
        cmbVehicle.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbVehicle.Items.AddRange(new object[] { "Saloon", "Estate", "MPV", "Minibus" });
        cmbVehicle.SelectedIndex = 0;
        pnlBookingFields.Controls.Add(MakeLabel("Time:", 355, y + 4, 9F));
        cmbPickupTime = MakeComboBox(400, y, 100, bgInput, fgLight);
        cmbPickupTime.Items.AddRange(new object[] { "ASAP", "15 min", "30 min", "1 hour" });
        cmbPickupTime.SelectedIndex = 0;
        pnlBookingFields.Controls.AddRange(new Control[] { nudPassengers, cmbVehicle, cmbPickupTime });
        y += 38;

        // Row 5: Quote
        btnVerify = MakeButton("🔍 Get Quote", 0, y, 130, 32, accent);
        btnVerify.Click += async (s, e) => await VerifyAndQuoteAsync();
        lblFare = new Label { Text = "Fare: —", Location = new Point(145, y + 4), Size = new Size(160, 24), ForeColor = Color.LimeGreen, Font = new Font("Segoe UI", 11F, FontStyle.Bold) };
        lblEta = new Label { Text = "ETA: —", Location = new Point(310, y + 6), Size = new Size(120, 20), ForeColor = Color.FromArgb(160, 200, 160), Font = new Font("Segoe UI", 9F) };
        pnlBookingFields.Controls.AddRange(new Control[] { btnVerify, lblFare, lblEta });
        y += 42;

        // Row 6: Actions
        btnDispatch = MakeButton("✅ Dispatch", 0, y, 130, 34, green);
        btnDispatch.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        btnDispatch.Enabled = false;
        btnDispatch.Click += async (s, e) => await ConfirmBookingAsync();
        btnClearBooking = MakeButton("🗑 Clear", 140, y, 90, 34, Color.FromArgb(65, 65, 72));
        btnClearBooking.Click += (s, e) => ClearBookingForm();
        lblBookingStatus = new Label { Text = "", Location = new Point(242, y + 8), Size = new Size(280, 20), ForeColor = Color.Gray, Font = new Font("Segoe UI", 8.5F, FontStyle.Italic) };
        pnlBookingFields.Controls.AddRange(new Control[] { btnDispatch, btnClearBooking, lblBookingStatus });

        pnlBooking.Controls.Add(pnlBookingFields);
        pnlBooking.Controls.Add(lblBookingTitle);

        // ┌───────────────────────────────────┐
        // │  📊 JOBS                          │
        // └───────────────────────────────────┘
        pnlJobs = MakeSectionPanel(DockStyle.Fill, bgSection, sectionBorder);

        var lblJobsTitle = MakeSectionTitle("📊  JOBS", accent);

        dgvJobs = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Color.FromArgb(26, 26, 30),
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
                BackColor = Color.FromArgb(26, 26, 30),
                ForeColor = fgLight,
                SelectionBackColor = Color.FromArgb(0, 80, 160),
                SelectionForeColor = Color.White,
                Padding = new Padding(3)
            },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(40, 42, 50),
                ForeColor = Color.FromArgb(180, 200, 220),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
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
        //  RIGHT PANEL — ADA/MAP + SIP + CALL + AUDIO
        // ══════════════════════════════════════════════════
        var pnlRight = splitMain.Panel2;
        pnlRight.BackColor = bgDark;
        pnlRight.Padding = new Padding(4, 6, 8, 6);

        var pnlRightInner = new Panel { Dock = DockStyle.Fill, BackColor = bgDark, AutoScroll = true };

        // ┌───────────────────────────────────┐
        // │  🤖 ADA / 🗺️ MAP                 │
        // └───────────────────────────────────┘
        pnlAdaMap = MakeSectionPanel(DockStyle.Top, bgSection, sectionBorder);
        pnlAdaMap.Height = 320;

        pnlAdaMapHeader = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = bgSection, Padding = new Padding(8, 0, 8, 0) };
        lblAdaMapTitle = new Label { Text = "🤖  ADA", Location = new Point(4, 5), AutoSize = true, ForeColor = Color.LimeGreen, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
        lblAdaMapStatus = new Label { Text = "Waiting…", Dock = DockStyle.Right, Width = 120, ForeColor = Color.Gray, Font = new Font("Segoe UI", 8F), TextAlign = ContentAlignment.MiddleRight };
        pnlAdaMapHeader.Controls.AddRange(new Control[] { lblAdaMapTitle, lblAdaMapStatus });

        pnlAdaMapHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black, Padding = new Padding(1) };

        pnlAdaMap.Controls.Add(pnlAdaMapHost);
        pnlAdaMap.Controls.Add(pnlAdaMapHeader);

        // ┌───────────────────────────────────┐
        // │  📞 SIP CONNECTION                │
        // └───────────────────────────────────┘
        pnlSip = MakeSectionPanel(DockStyle.Top, bgSection, sectionBorder);
        pnlSip.Height = 130;

        var lblSipTitle = MakeSectionTitle("📞  SIP CONNECTION", accent);

        var pnlSipFields = new Panel { Dock = DockStyle.Fill, BackColor = bgSection, Padding = new Padding(12, 4, 12, 4) };

        int sy = 2;
        pnlSipFields.Controls.Add(MakeLabel("Account:", 0, sy + 4, 8.5F));
        cmbSipAccount = new ComboBox { Location = new Point(65, sy), Size = new Size(200, 24), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = bgInput, ForeColor = fgLight, Font = new Font("Segoe UI", 8.5F) };
        cmbSipAccount.SelectedIndexChanged += cmbSipAccount_SelectedIndexChanged;
        btnSaveSip = MakeButton("💾", 272, sy, 30, 24, Color.FromArgb(55, 55, 68));
        btnSaveSip.Font = new Font("Segoe UI", 8F);
        btnSaveSip.Click += btnSaveAccount_Click;
        pnlSipFields.Controls.AddRange(new Control[] { cmbSipAccount, btnSaveSip });
        sy += 30;

        pnlSipFields.Controls.Add(MakeLabel("Server:", 0, sy + 4, 8.5F));
        txtSipServer = MakeTextBox(65, sy, 145, bgInput, fgLight, 8.5F);
        txtSipServer.PlaceholderText = "sip.example.com";
        pnlSipFields.Controls.Add(MakeLabel("Ext:", 218, sy + 4, 8.5F));
        txtSipUser = MakeTextBox(248, sy, 60, bgInput, fgLight, 8.5F);
        pnlSipFields.Controls.AddRange(new Control[] { txtSipServer, txtSipUser });
        sy += 28;

        pnlSipFields.Controls.Add(MakeLabel("Pass:", 0, sy + 4, 8.5F));
        txtSipPassword = MakeTextBox(65, sy, 100, bgInput, fgLight, 8.5F);
        txtSipPassword.UseSystemPasswordChar = true;
        pnlSipFields.Controls.Add(MakeLabel("Port:", 174, sy + 4, 8.5F));
        txtSipPort = MakeTextBox(210, sy, 48, bgInput, fgLight, 8.5F);
        txtSipPort.Text = "5060";
        chkAutoAnswer = new CheckBox { Text = "Auto", Location = new Point(266, sy + 2), Size = new Size(52, 22), ForeColor = fgLight, Font = new Font("Segoe UI", 8F), Checked = true };
        pnlSipFields.Controls.AddRange(new Control[] { txtSipPassword, txtSipPort, chkAutoAnswer });
        sy += 28;

        btnConnect = MakeButton("▶ Connect", 0, sy, 95, 26, green);
        btnConnect.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        btnConnect.Click += btnConnect_Click;
        btnDisconnect = MakeButton("■ Stop", 100, sy, 70, 26, red);
        btnDisconnect.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        btnDisconnect.Enabled = false;
        btnDisconnect.Click += btnDisconnect_Click;
        lblSipStatus = new Label { Text = "● Offline", Location = new Point(178, sy + 5), Size = new Size(130, 18), ForeColor = Color.Gray, Font = new Font("Segoe UI", 8.5F, FontStyle.Bold) };
        pnlSipFields.Controls.AddRange(new Control[] { btnConnect, btnDisconnect, lblSipStatus });

        pnlSip.Controls.Add(pnlSipFields);
        pnlSip.Controls.Add(lblSipTitle);

        // ┌───────────────────────────────────┐
        // │  🎧 CALL CONTROLS                 │
        // └───────────────────────────────────┘
        pnlCall = MakeSectionPanel(DockStyle.Top, bgSection, sectionBorder);
        pnlCall.Height = 110;

        var lblCallTitle = MakeSectionTitle("🎧  CALL CONTROLS", accent);

        var pnlCallFields = new Panel { Dock = DockStyle.Fill, BackColor = bgSection, Padding = new Padding(12, 4, 12, 4) };

        int cy = 2;
        // Call action buttons
        btnAnswer = MakeButton("✅ Answer", 0, cy, 80, 28, green);
        btnAnswer.Enabled = false; btnAnswer.Click += btnAnswer_Click;
        btnReject = MakeButton("❌ Reject", 86, cy, 80, 28, red);
        btnReject.Enabled = false; btnReject.Click += btnReject_Click;
        btnHangUp = MakeButton("📴 Hang Up", 172, cy, 90, 28, Color.FromArgb(160, 45, 45));
        btnHangUp.Enabled = false; btnHangUp.Click += btnHangUp_Click;
        pnlCallFields.Controls.AddRange(new Control[] { btnAnswer, btnReject, btnHangUp });
        cy += 34;

        // Call Out + Mute + Status
        btnCallOut = MakeButton("📞 Call Out", 0, cy, 90, 28, accentDim);
        btnCallOut.Click += btnCallOut_Click;
        btnMute = MakeButton("🔊 Mute", 96, cy, 76, 28, Color.FromArgb(60, 60, 68));
        btnMute.Enabled = false; btnMute.Click += btnMute_Click;
        lblCallInfo = new Label { Text = "No call", Location = new Point(180, cy + 6), Size = new Size(140, 18), ForeColor = Color.Gray, Font = new Font("Segoe UI", 8.5F) };
        pnlCallFields.Controls.AddRange(new Control[] { btnCallOut, btnMute, lblCallInfo });
        cy += 34;

        // Operator mode
        chkManualMode = new CheckBox { Text = "🎤 Operator Mode", Location = new Point(0, cy), Size = new Size(140, 22), ForeColor = orange, Font = new Font("Segoe UI", 8.5F, FontStyle.Bold) };
        chkManualMode.CheckedChanged += chkManualMode_CheckedChanged;
        pnlCallFields.Controls.Add(chkManualMode);

        pnlCall.Controls.Add(pnlCallFields);
        pnlCall.Controls.Add(lblCallTitle);

        // ┌───────────────────────────────────┐
        // │  🔊 AUDIO / VOLUME                │
        // └───────────────────────────────────┘
        pnlAudio = MakeSectionPanel(DockStyle.Top, bgSection, sectionBorder);
        pnlAudio.Height = 110;

        var lblAudioTitle = MakeSectionTitle("🔊  AUDIO / VOLUME", accent);

        var pnlAudioFields = new Panel { Dock = DockStyle.Fill, BackColor = bgSection, Padding = new Padding(12, 6, 12, 6) };

        int ay = 0;
        // Customer Volume (what they hear)
        pnlAudioFields.Controls.Add(MakeLabel("📱 Customer Vol:", 0, ay + 4, 8.5F));
        trkCustomerVolume = new TrackBar
        {
            Location = new Point(120, ay), Size = new Size(150, 30),
            Minimum = 0, Maximum = 100, Value = 80,
            TickFrequency = 20, SmallChange = 5, BackColor = bgSection
        };
        lblCustomerVolVal = new Label { Text = "80%", Location = new Point(275, ay + 4), Size = new Size(40, 18), ForeColor = Color.LimeGreen, Font = new Font("Segoe UI", 8F, FontStyle.Bold) };
        trkCustomerVolume.ValueChanged += (s, e) => { lblCustomerVolVal.Text = $"{trkCustomerVolume.Value}%"; };
        pnlAudioFields.Controls.AddRange(new Control[] { trkCustomerVolume, lblCustomerVolVal });
        ay += 36;

        // Listener Volume (what operator hears)
        pnlAudioFields.Controls.Add(MakeLabel("🎧 Listener Vol:", 0, ay + 4, 8.5F));
        trkOpVolume = new TrackBar
        {
            Location = new Point(120, ay), Size = new Size(150, 30),
            Minimum = 10, Maximum = 60, Value = 20,
            TickFrequency = 10, SmallChange = 5, BackColor = bgSection
        };
        lblOpVolumeVal = new Label { Text = "2.0x", Location = new Point(275, ay + 4), Size = new Size(40, 18), ForeColor = Color.LimeGreen, Font = new Font("Segoe UI", 8F, FontStyle.Bold) };
        trkOpVolume.ValueChanged += (s, e) =>
        {
            _operatorMicGain = trkOpVolume.Value / 10f;
            lblOpVolumeVal.Text = $"{_operatorMicGain:F1}x";
        };
        pnlAudioFields.Controls.AddRange(new Control[] { trkOpVolume, lblOpVolumeVal });

        pnlAudio.Controls.Add(pnlAudioFields);
        pnlAudio.Controls.Add(lblAudioTitle);

        // ── Assemble right panels (reverse dock order) ──
        var gap1 = new Panel { Dock = DockStyle.Top, Height = 6, BackColor = bgDark };
        var gap2 = new Panel { Dock = DockStyle.Top, Height = 6, BackColor = bgDark };
        var gap3 = new Panel { Dock = DockStyle.Top, Height = 6, BackColor = bgDark };

        pnlRightInner.Controls.Add(pnlAudio);
        pnlRightInner.Controls.Add(gap3);
        pnlRightInner.Controls.Add(pnlCall);
        pnlRightInner.Controls.Add(gap2);
        pnlRightInner.Controls.Add(pnlSip);
        pnlRightInner.Controls.Add(gap1);
        pnlRightInner.Controls.Add(pnlAdaMap);

        pnlRight.Controls.Add(pnlRightInner);

        // ══════════════════════════════════════
        //  LOG PANEL (toggleable, docked bottom)
        // ══════════════════════════════════════
        pnlLog = new Panel { Dock = DockStyle.Bottom, Height = 160, BackColor = bgPanel, Visible = false, Padding = new Padding(8, 4, 8, 4) };
        txtLog = new RichTextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Cascadia Mono", 8.5F),
            BackColor = Color.FromArgb(16, 16, 18),
            ForeColor = Color.LightGreen,
            ReadOnly = true,
            BorderStyle = BorderStyle.None
        };
        var lblLogTitle = MakeSectionTitle("📋  LOG", accent);
        pnlLog.Controls.Add(txtLog);
        pnlLog.Controls.Add(lblLogTitle);

        // ══════════════════════════════════════
        //  STATUS BAR
        // ══════════════════════════════════════
        statusStrip = new StatusStrip { BackColor = Color.FromArgb(26, 26, 30), ForeColor = fgLight, SizingGrip = true };
        statusLabel = new ToolStripStatusLabel("Ready") { ForeColor = fgLight };
        statusCallId = new ToolStripStatusLabel("") { Spring = true, TextAlign = ContentAlignment.MiddleRight, ForeColor = fgLight };
        statusStrip.Items.AddRange(new ToolStripItem[] { statusLabel, statusCallId });

        // ══════════════════════════════════════
        //  FORM
        // ══════════════════════════════════════
        this.AutoScaleDimensions = new SizeF(7F, 15F);
        this.AutoScaleMode = AutoScaleMode.Font;
        this.ClientSize = new Size(1200, 780);
        this.MinimumSize = new Size(1080, 700);
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
    private static Panel MakeSectionPanel(DockStyle dock, Color bg, Color border)
    {
        var pnl = new BorderedPanel
        {
            Dock = dock,
            BackColor = bg,
            Padding = new Padding(2),
            Margin = new Padding(0, 0, 0, 2),
            BorderColor = border
        };
        return pnl;
    }

    private class BorderedPanel : Panel
    {
        public Color BorderColor { get; set; } = Color.FromArgb(60, 65, 78);
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(BorderColor, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    private static Label MakeSectionTitle(string text, Color fg)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Top,
            Height = 28,
            ForeColor = fg,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            BackColor = Color.FromArgb(32, 34, 42)
        };
    }

    private static Label MakeLabel(string text, int x, int y, float fontSize = 9F)
        => new Label { Text = text, Location = new Point(x, y), AutoSize = true, Font = new Font("Segoe UI", fontSize) };

    private static TextBox MakeTextBox(int x, int y, int w, Color bg, Color fg, float fontSize = 8.5F)
        => new TextBox { Location = new Point(x, y), Size = new Size(w, 26), BackColor = bg, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", fontSize) };

    private static ComboBox MakeComboBox(int x, int y, int w, Color bg, Color fg)
        => new ComboBox { Location = new Point(x, y), Size = new Size(w, 26), BackColor = bg, ForeColor = fg, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9F) };

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
        btn.FlatAppearance.BorderColor = Color.FromArgb(70, 75, 85);
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
