using AdaVaxVoIP.Config;

namespace AdaVaxVoIP;

/// <summary>
/// Tabbed settings dialog with multi-account SIP support.
/// </summary>
public class ConfigForm : Form
{
    public AppSettings Settings { get; private set; }

    // SIP account management
    private ComboBox cmbAccount = null!;
    private Button btnAddAccount = null!, btnRemoveAccount = null!;

    // SIP
    private TextBox txtSipServer = null!, txtSipUser = null!, txtSipPassword = null!;
    private TextBox txtSipAuthId = null!, txtSipDomain = null!;
    private NumericUpDown nudSipPort = null!;
    private ComboBox cmbTransport = null!;
    private CheckBox chkAutoAnswer = null!, chkStun = null!;
    private TextBox txtStunServer = null!;
    private NumericUpDown nudStunPort = null!;

    // OpenAI
    private TextBox txtOpenAiKey = null!, txtOpenAiModel = null!, txtOpenAiVoice = null!;

    // Audio
    private ComboBox cmbCodec = null!;
    private NumericUpDown nudVolumeBoost = null!, nudIngressBoost = null!, nudEchoGuard = null!;
    private CheckBox chkDiagnostics = null!;

    // Dispatch
    private TextBox txtBsqdWebhook = null!, txtBsqdApiKey = null!, txtWhatsAppWebhook = null!;

    // Maps / Supabase
    private TextBox txtGoogleMapsKey = null!;
    private TextBox txtSupabaseUrl = null!, txtSupabaseAnonKey = null!;

    // Simli
    private TextBox txtSimliApiKey = null!, txtSimliFaceId = null!;
    private CheckBox chkSimliEnabled = null!;

    // STT
    private TextBox txtDeepgramKey = null!;

    // VaxVoIP
    private TextBox txtVaxLicense = null!, txtVaxDomain = null!, txtVaxRecPath = null!;
    private NumericUpDown nudRtpMin = null!, nudRtpMax = null!;
    private CheckBox chkRecording = null!;

    private bool _suppressAccountSwitch;

    public ConfigForm(AppSettings settings)
    {
        Settings = SettingsStore.Clone(settings);
        BuildUi();
        LoadFromSettings();
        RefreshAccountDropdown();
    }

    private void BuildUi()
    {
        Text = "⚙ Settings";
        Size = new Size(520, 530);
        MinimumSize = new Size(480, 470);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        BackColor = Color.FromArgb(40, 40, 43);
        ForeColor = Color.FromArgb(220, 220, 220);
        Font = new Font("Segoe UI", 9F);

        var bg = Color.FromArgb(60, 60, 65);
        var fg = Color.FromArgb(220, 220, 220);

        var tabs = new TabControl { Dock = DockStyle.Fill };

        // ── SIP Tab ──
        var tabSip = new TabPage("📞 SIP") { BackColor = BackColor };

        // Account selector row
        tabSip.Controls.Add(new Label { Text = "Account:", Location = new Point(15, 23), AutoSize = true });
        cmbAccount = new ComboBox
        {
            Location = new Point(120, 20), Size = new Size(200, 23),
            DropDownStyle = ComboBoxStyle.DropDownList, BackColor = bg, ForeColor = fg
        };
        cmbAccount.SelectedIndexChanged += CmbAccount_SelectedIndexChanged;
        tabSip.Controls.Add(cmbAccount);

        btnAddAccount = new Button
        {
            Text = "+", Size = new Size(30, 23), Location = new Point(325, 20),
            BackColor = Color.FromArgb(0, 120, 60), ForeColor = Color.White, FlatStyle = FlatStyle.Flat
        };
        btnAddAccount.FlatAppearance.BorderSize = 0;
        btnAddAccount.Click += BtnAddAccount_Click;
        tabSip.Controls.Add(btnAddAccount);

        btnRemoveAccount = new Button
        {
            Text = "−", Size = new Size(30, 23), Location = new Point(358, 20),
            BackColor = Color.FromArgb(180, 40, 40), ForeColor = Color.White, FlatStyle = FlatStyle.Flat
        };
        btnRemoveAccount.FlatAppearance.BorderSize = 0;
        btnRemoveAccount.Click += BtnRemoveAccount_Click;
        tabSip.Controls.Add(btnRemoveAccount);

        // Offset all SIP fields down by 35px to make room for account row
        const int yOff = 35;
        txtSipServer = AddField(tabSip, "Server:", 20 + yOff, bg, fg);
        nudSipPort = AddNumeric(tabSip, "Port:", 55 + yOff, bg, fg, 1, 65535, 5060);
        cmbTransport = new ComboBox { Location = new Point(120, 88 + yOff), Size = new Size(100, 23), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = bg, ForeColor = fg };
        cmbTransport.Items.AddRange(new object[] { "UDP", "TCP", "TLS" });
        tabSip.Controls.Add(new Label { Text = "Transport:", Location = new Point(15, 91 + yOff), AutoSize = true });
        tabSip.Controls.Add(cmbTransport);
        txtSipUser = AddField(tabSip, "Username:", 120 + yOff, bg, fg);
        txtSipPassword = AddField(tabSip, "Password:", 155 + yOff, bg, fg, masked: true);
        txtSipAuthId = AddField(tabSip, "Auth ID:", 190 + yOff, bg, fg);
        txtSipDomain = AddField(tabSip, "Domain:", 225 + yOff, bg, fg);
        chkAutoAnswer = new CheckBox { Text = "Auto-Answer incoming calls", Location = new Point(15, 258 + yOff), AutoSize = true, ForeColor = fg };
        chkStun = new CheckBox { Text = "Enable STUN", Location = new Point(15, 283 + yOff), AutoSize = true, ForeColor = fg };
        txtStunServer = AddField(tabSip, "STUN Server:", 308 + yOff, bg, fg);
        nudStunPort = AddNumeric(tabSip, "STUN Port:", 343 + yOff, bg, fg, 1, 65535, 19302);
        tabSip.Controls.AddRange(new Control[] { chkAutoAnswer, chkStun });

        // ── OpenAI Tab ──
        var tabAi = new TabPage("🤖 OpenAI") { BackColor = BackColor };
        txtOpenAiKey = AddField(tabAi, "API Key:", 20, bg, fg, masked: true);
        txtOpenAiModel = AddField(tabAi, "Model:", 55, bg, fg);
        txtOpenAiVoice = AddField(tabAi, "Voice:", 90, bg, fg);

        // ── Audio Tab ──
        var tabAudio = new TabPage("🔊 Audio") { BackColor = BackColor };
        tabAudio.Controls.Add(new Label { Text = "Codec:", Location = new Point(15, 23), AutoSize = true });
        cmbCodec = new ComboBox { Location = new Point(140, 20), Size = new Size(100, 23), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = bg, ForeColor = fg };
        cmbCodec.Items.AddRange(new object[] { "PCMA", "PCMU", "G722" });
        tabAudio.Controls.Add(cmbCodec);
        nudVolumeBoost = AddNumeric(tabAudio, "Volume Boost:", 55, bg, fg, 0, 10, 1, 1, 0.5m);
        nudIngressBoost = AddNumeric(tabAudio, "Ingress Boost:", 90, bg, fg, 0, 10, 2.5m, 1, 0.5m);
        nudEchoGuard = AddNumeric(tabAudio, "Echo Guard (ms):", 125, bg, fg, 0, 1000, 200, 0, 50);
        chkDiagnostics = new CheckBox { Text = "Enable audio diagnostics", Location = new Point(15, 160), AutoSize = true, ForeColor = fg };
        tabAudio.Controls.Add(chkDiagnostics);

        // ── Dispatch Tab ──
        var tabDispatch = new TabPage("📡 Dispatch") { BackColor = BackColor };
        txtBsqdWebhook = AddField(tabDispatch, "Webhook URL:", 20, bg, fg);
        txtBsqdApiKey = AddField(tabDispatch, "API Key:", 55, bg, fg, masked: true);
        txtWhatsAppWebhook = AddField(tabDispatch, "WhatsApp URL:", 90, bg, fg);

        // ── Maps Tab ──
        var tabMaps = new TabPage("🗺 Maps") { BackColor = BackColor };
        txtGoogleMapsKey = AddField(tabMaps, "API Key:", 20, bg, fg, masked: true);
        txtSupabaseUrl = AddField(tabMaps, "Supabase URL:", 55, bg, fg);
        txtSupabaseAnonKey = AddField(tabMaps, "Supabase Key:", 90, bg, fg, masked: true);

        // ── Simli Tab ──
        var tabSimli = new TabPage("🎭 Simli") { BackColor = BackColor };
        txtSimliApiKey = AddField(tabSimli, "API Key:", 20, bg, fg, masked: true);
        txtSimliFaceId = AddField(tabSimli, "Face ID:", 55, bg, fg);
        chkSimliEnabled = new CheckBox { Text = "Enable Simli avatar", Location = new Point(15, 90), AutoSize = true, ForeColor = fg };
        tabSimli.Controls.Add(chkSimliEnabled);

        // ── STT Tab ──
        var tabStt = new TabPage("🎤 STT") { BackColor = BackColor };
        txtDeepgramKey = AddField(tabStt, "Deepgram Key:", 20, bg, fg, masked: true);

        // ── VaxVoIP Tab ──
        var tabVax = new TabPage("🔧 VaxVoIP") { BackColor = BackColor };
        txtVaxLicense = AddField(tabVax, "License Key:", 20, bg, fg);
        txtVaxDomain = AddField(tabVax, "Domain Realm:", 55, bg, fg);
        nudRtpMin = AddNumeric(tabVax, "RTP Port Min:", 90, bg, fg, 1024, 65535, 10000);
        nudRtpMax = AddNumeric(tabVax, "RTP Port Max:", 125, bg, fg, 1024, 65535, 20000);
        chkRecording = new CheckBox { Text = "Enable call recording", Location = new Point(15, 160), AutoSize = true, ForeColor = fg };
        tabVax.Controls.Add(chkRecording);
        txtVaxRecPath = AddField(tabVax, "Recordings Path:", 190, bg, fg);

        tabs.TabPages.AddRange(new TabPage[] { tabSip, tabAi, tabAudio, tabDispatch, tabMaps, tabSimli, tabStt, tabVax });

        // ── OK / Cancel ──
        var pnlButtons = new Panel { Dock = DockStyle.Bottom, Height = 45, BackColor = BackColor };
        var btnOk = new Button
        {
            Text = "Save", Size = new Size(80, 30), Location = new Point(320, 8),
            BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.OK
        };
        btnOk.FlatAppearance.BorderSize = 0;
        btnOk.Click += (_, _) => WriteToSettings();

        var btnCancel = new Button
        {
            Text = "Cancel", Size = new Size(80, 30), Location = new Point(410, 8),
            FlatStyle = FlatStyle.Flat, ForeColor = Color.LightGray, DialogResult = DialogResult.Cancel
        };
        btnCancel.FlatAppearance.BorderSize = 0;

        pnlButtons.Controls.AddRange(new Control[] { btnOk, btnCancel });

        Controls.Add(tabs);
        Controls.Add(pnlButtons);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    #region Account Management

    private void RefreshAccountDropdown()
    {
        _suppressAccountSwitch = true;
        cmbAccount.Items.Clear();

        foreach (var acct in Settings.SipAccounts)
            cmbAccount.Items.Add(acct.ToString());

        if (Settings.SipAccounts.Count > 0)
        {
            var idx = Math.Clamp(Settings.SelectedSipAccountIndex, 0, Settings.SipAccounts.Count - 1);
            cmbAccount.SelectedIndex = idx;
        }

        btnRemoveAccount.Enabled = Settings.SipAccounts.Count > 1;
        _suppressAccountSwitch = false;
    }

    private void CmbAccount_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_suppressAccountSwitch || cmbAccount.SelectedIndex < 0) return;

        // Save current fields back to the previously selected account
        SyncCurrentFieldsToSelectedAccount();

        // Switch
        Settings.SelectedSipAccountIndex = cmbAccount.SelectedIndex;
        var acct = Settings.SipAccounts[cmbAccount.SelectedIndex];
        Settings.Sip = acct.ToSipSettings();
        LoadSipFields();
    }

    private void SyncCurrentFieldsToSelectedAccount()
    {
        // Write UI fields into Settings.Sip
        WriteSipFields();

        // Sync back to the account entry
        var idx = Settings.SelectedSipAccountIndex;
        if (idx >= 0 && idx < Settings.SipAccounts.Count)
        {
            Settings.SipAccounts[idx].FromSipSettings(Settings.Sip, Settings.SipAccounts[idx].Label);
        }
    }

    private void BtnAddAccount_Click(object? sender, EventArgs e)
    {
        // Save current first
        SyncCurrentFieldsToSelectedAccount();

        var label = $"Account {Settings.SipAccounts.Count + 1}";
        var newAcct = new SipAccount { Label = label };
        Settings.SipAccounts.Add(newAcct);
        Settings.SelectedSipAccountIndex = Settings.SipAccounts.Count - 1;
        Settings.Sip = newAcct.ToSipSettings();

        RefreshAccountDropdown();
        LoadSipFields();
    }

    private void BtnRemoveAccount_Click(object? sender, EventArgs e)
    {
        if (Settings.SipAccounts.Count <= 1) return;

        var idx = cmbAccount.SelectedIndex;
        if (idx < 0) return;

        var acct = Settings.SipAccounts[idx];
        if (MessageBox.Show($"Remove account \"{acct.Label}\"?", "Confirm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        Settings.SipAccounts.RemoveAt(idx);
        Settings.SelectedSipAccountIndex = Math.Min(idx, Settings.SipAccounts.Count - 1);
        Settings.Sip = Settings.SipAccounts[Settings.SelectedSipAccountIndex].ToSipSettings();

        RefreshAccountDropdown();
        LoadSipFields();
    }

    #endregion

    private TextBox AddField(TabPage tab, string label, int y, Color bg, Color fg, bool masked = false)
    {
        tab.Controls.Add(new Label { Text = label, Location = new Point(15, y + 3), AutoSize = true });
        var tb = new TextBox { Location = new Point(140, y), Size = new Size(330, 23), BackColor = bg, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle };
        if (masked) tb.UseSystemPasswordChar = true;
        tab.Controls.Add(tb);
        return tb;
    }

    private NumericUpDown AddNumeric(TabPage tab, string label, int y, Color bg, Color fg,
        decimal min, decimal max, decimal value, int decimals = 0, decimal increment = 1)
    {
        tab.Controls.Add(new Label { Text = label, Location = new Point(15, y + 3), AutoSize = true });
        var nud = new NumericUpDown
        {
            Location = new Point(140, y), Size = new Size(100, 23),
            Minimum = min, Maximum = max, Value = value,
            DecimalPlaces = decimals, Increment = increment,
            BackColor = bg, ForeColor = fg
        };
        tab.Controls.Add(nud);
        return nud;
    }

    private void LoadSipFields()
    {
        txtSipServer.Text = Settings.Sip.Server;
        nudSipPort.Value = Settings.Sip.Port;
        cmbTransport.SelectedItem = Settings.Sip.Transport;
        if (cmbTransport.SelectedIndex < 0) cmbTransport.SelectedIndex = 0;
        txtSipUser.Text = Settings.Sip.Username;
        txtSipPassword.Text = Settings.Sip.Password;
        txtSipAuthId.Text = Settings.Sip.AuthId ?? "";
        txtSipDomain.Text = Settings.Sip.Domain ?? "";
        chkAutoAnswer.Checked = Settings.Sip.AutoAnswer;
        chkStun.Checked = Settings.Sip.EnableStun;
        txtStunServer.Text = Settings.Sip.StunServer;
        nudStunPort.Value = Settings.Sip.StunPort;
    }

    private void WriteSipFields()
    {
        Settings.Sip.Server = txtSipServer.Text.Trim();
        Settings.Sip.Port = (int)nudSipPort.Value;
        Settings.Sip.Transport = cmbTransport.SelectedItem?.ToString() ?? "UDP";
        Settings.Sip.Username = txtSipUser.Text.Trim();
        Settings.Sip.Password = txtSipPassword.Text.Trim();
        Settings.Sip.AuthId = string.IsNullOrWhiteSpace(txtSipAuthId.Text) ? null : txtSipAuthId.Text.Trim();
        Settings.Sip.Domain = string.IsNullOrWhiteSpace(txtSipDomain.Text) ? null : txtSipDomain.Text.Trim();
        Settings.Sip.AutoAnswer = chkAutoAnswer.Checked;
        Settings.Sip.EnableStun = chkStun.Checked;
        Settings.Sip.StunServer = txtStunServer.Text.Trim();
        Settings.Sip.StunPort = (int)nudStunPort.Value;
    }

    private void LoadFromSettings()
    {
        // SIP
        LoadSipFields();

        // OpenAI
        txtOpenAiKey.Text = Settings.OpenAi.ApiKey;
        txtOpenAiModel.Text = Settings.OpenAi.Model;
        txtOpenAiVoice.Text = Settings.OpenAi.Voice;

        // Audio
        cmbCodec.SelectedItem = Settings.Audio.PreferredCodec;
        if (cmbCodec.SelectedIndex < 0) cmbCodec.SelectedIndex = 0;
        nudVolumeBoost.Value = (decimal)Settings.Audio.VolumeBoost;
        nudIngressBoost.Value = (decimal)Settings.Audio.IngressVolumeBoost;
        nudEchoGuard.Value = Settings.Audio.EchoGuardMs;
        chkDiagnostics.Checked = Settings.Audio.EnableDiagnostics;

        // Dispatch
        txtBsqdWebhook.Text = Settings.Dispatch.BsqdWebhookUrl;
        txtBsqdApiKey.Text = Settings.Dispatch.BsqdApiKey;
        txtWhatsAppWebhook.Text = Settings.Dispatch.WhatsAppWebhookUrl;

        // Maps / Supabase
        txtGoogleMapsKey.Text = Settings.GoogleMaps.ApiKey;
        txtSupabaseUrl.Text = Settings.Supabase.Url;
        txtSupabaseAnonKey.Text = Settings.Supabase.AnonKey;

        // Simli
        txtSimliApiKey.Text = Settings.Simli.ApiKey;
        txtSimliFaceId.Text = Settings.Simli.FaceId;
        chkSimliEnabled.Checked = Settings.Simli.Enabled;

        // STT
        txtDeepgramKey.Text = Settings.Stt.DeepgramApiKey;

        // VaxVoIP
        txtVaxLicense.Text = Settings.VaxVoIP.LicenseKey;
        txtVaxDomain.Text = Settings.VaxVoIP.DomainRealm;
        nudRtpMin.Value = Settings.VaxVoIP.RtpPortMin;
        nudRtpMax.Value = Settings.VaxVoIP.RtpPortMax;
        chkRecording.Checked = Settings.VaxVoIP.EnableRecording;
        txtVaxRecPath.Text = Settings.VaxVoIP.RecordingsPath;
    }

    private void WriteToSettings()
    {
        // SIP — sync current fields to active account
        WriteSipFields();
        SyncCurrentFieldsToSelectedAccount();

        // OpenAI
        Settings.OpenAi.ApiKey = txtOpenAiKey.Text.Trim();
        Settings.OpenAi.Model = txtOpenAiModel.Text.Trim();
        Settings.OpenAi.Voice = txtOpenAiVoice.Text.Trim();

        // Audio
        Settings.Audio.PreferredCodec = cmbCodec.SelectedItem?.ToString() ?? "PCMA";
        Settings.Audio.VolumeBoost = (double)nudVolumeBoost.Value;
        Settings.Audio.IngressVolumeBoost = (double)nudIngressBoost.Value;
        Settings.Audio.EchoGuardMs = (int)nudEchoGuard.Value;
        Settings.Audio.EnableDiagnostics = chkDiagnostics.Checked;

        // Dispatch
        Settings.Dispatch.BsqdWebhookUrl = txtBsqdWebhook.Text.Trim();
        Settings.Dispatch.BsqdApiKey = txtBsqdApiKey.Text.Trim();
        Settings.Dispatch.WhatsAppWebhookUrl = txtWhatsAppWebhook.Text.Trim();

        // Maps / Supabase
        Settings.GoogleMaps.ApiKey = txtGoogleMapsKey.Text.Trim();
        Settings.Supabase.Url = txtSupabaseUrl.Text.Trim();
        Settings.Supabase.AnonKey = txtSupabaseAnonKey.Text.Trim();

        // Simli
        Settings.Simli.ApiKey = txtSimliApiKey.Text.Trim();
        Settings.Simli.FaceId = txtSimliFaceId.Text.Trim();
        Settings.Simli.Enabled = chkSimliEnabled.Checked;

        // STT
        Settings.Stt.DeepgramApiKey = txtDeepgramKey.Text.Trim();

        // VaxVoIP
        Settings.VaxVoIP.LicenseKey = txtVaxLicense.Text.Trim();
        Settings.VaxVoIP.DomainRealm = txtVaxDomain.Text.Trim();
        Settings.VaxVoIP.RtpPortMin = (int)nudRtpMin.Value;
        Settings.VaxVoIP.RtpPortMax = (int)nudRtpMax.Value;
        Settings.VaxVoIP.EnableRecording = chkRecording.Checked;
        Settings.VaxVoIP.RecordingsPath = txtVaxRecPath.Text.Trim();
    }
}
