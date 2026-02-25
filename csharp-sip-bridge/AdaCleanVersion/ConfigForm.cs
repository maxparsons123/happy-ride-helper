using AdaCleanVersion.Config;

namespace AdaCleanVersion;

/// <summary>
/// Settings dialog for API keys, dispatch config, and audio options.
/// Mirrors AdaSdkModel's ConfigForm layout.
/// </summary>
public sealed class ConfigForm : Form
{
    public CleanAppSettings Settings { get; private set; }

    private TabControl tabs = null!;
    private TextBox txtOpenAiKey = null!, txtOpenAiModel = null!, txtOpenAiVoice = null!;
    private TextBox txtBsqdWebhook = null!, txtBsqdApiKey = null!, txtWhatsAppWebhook = null!;
    private ComboBox cmbCodec = null!;
    private NumericUpDown nudVolumeBoost = null!, nudEchoGuard = null!, nudCircuitBreaker = null!;
    private CheckBox chkDiagnostics = null!;
    private TextBox txtSupabaseUrl = null!, txtSupabaseKey = null!, txtSupabaseServiceKey = null!;
    private TextBox txtSimliApiKey = null!, txtSimliFaceId = null!;
    private CheckBox chkSimliEnabled = null!;
    private NumericUpDown nudMaxSimliReconnect = null!;
    private TextBox txtCompanyName = null!;

    public ConfigForm(CleanAppSettings settings)
    {
        Settings = Clone(settings);
        BuildUi();
        LoadFromSettings();
    }

    private void BuildUi()
    {
        Text = "⚙ Settings";
        Size = new Size(500, 460);
        MinimumSize = new Size(460, 420);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        BackColor = Color.FromArgb(40, 40, 43);
        ForeColor = Color.FromArgb(220, 220, 220);
        Font = new Font("Segoe UI", 9F);

        var bgInput = Color.FromArgb(60, 60, 65);
        var fgInput = Color.FromArgb(220, 220, 220);

        tabs = new TabControl { Dock = DockStyle.Fill };

        // ── OpenAI Tab ──
        var tabAi = new TabPage("🤖 OpenAI") { BackColor = BackColor };
        txtOpenAiKey = AddField(tabAi, "API Key:", 20, bgInput, fgInput, masked: true);
        txtOpenAiModel = AddField(tabAi, "Model:", 60, bgInput, fgInput);
        txtOpenAiVoice = AddField(tabAi, "Voice:", 100, bgInput, fgInput);

        // ── Dispatch Tab ──
        var tabDispatch = new TabPage("📡 Dispatch") { BackColor = BackColor };
        txtBsqdWebhook = AddField(tabDispatch, "Webhook URL:", 20, bgInput, fgInput);
        txtBsqdApiKey = AddField(tabDispatch, "API Key:", 60, bgInput, fgInput, masked: true);
        txtWhatsAppWebhook = AddField(tabDispatch, "WhatsApp URL:", 100, bgInput, fgInput);

        // ── Audio Tab ──
        var tabAudio = new TabPage("🔊 Audio") { BackColor = BackColor };
        var lblCodec = new Label { Text = "Codec:", Location = new Point(15, 23), AutoSize = true };
        cmbCodec = new ComboBox { Location = new Point(120, 20), Size = new Size(100, 23), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = bgInput, ForeColor = fgInput };
        cmbCodec.Items.AddRange(new object[] { "PCMA", "PCMU" });

        var lblVol = new Label { Text = "Volume Boost:", Location = new Point(15, 56), AutoSize = true };
        nudVolumeBoost = new NumericUpDown { Location = new Point(120, 53), Size = new Size(80, 23), Minimum = 0, Maximum = 10, DecimalPlaces = 1, Increment = 0.5m, BackColor = bgInput, ForeColor = fgInput };

        var lblEcho = new Label { Text = "Echo Guard (ms):", Location = new Point(15, 89), AutoSize = true };
        nudEchoGuard = new NumericUpDown { Location = new Point(120, 86), Size = new Size(80, 23), Minimum = 0, Maximum = 1000, Increment = 50, BackColor = bgInput, ForeColor = fgInput };
        chkDiagnostics = new CheckBox { Text = "Enable audio diagnostics logging", Location = new Point(15, 122), AutoSize = true, ForeColor = fgInput };

        var lblCb = new Label { Text = "CB Threshold:", Location = new Point(15, 155), AutoSize = true };
        nudCircuitBreaker = new NumericUpDown { Location = new Point(120, 152), Size = new Size(80, 23), Minimum = 1, Maximum = 100, Increment = 1, BackColor = bgInput, ForeColor = fgInput };
        var lblCbHint = new Label { Text = "Consecutive RTP failures before call end", Location = new Point(210, 155), AutoSize = true, ForeColor = Color.FromArgb(150, 150, 150), Font = new Font("Segoe UI", 7.5F) };

        tabAudio.Controls.AddRange(new Control[] { lblCodec, cmbCodec, lblVol, nudVolumeBoost, lblEcho, nudEchoGuard, chkDiagnostics, lblCb, nudCircuitBreaker, lblCbHint });

        // ── Backend Tab ──
        var tabBackend = new TabPage("☁ Backend") { BackColor = BackColor };
        txtSupabaseUrl = AddField(tabBackend, "URL:", 20, bgInput, fgInput);
        txtSupabaseKey = AddField(tabBackend, "Anon Key:", 60, bgInput, fgInput, masked: true);
        txtSupabaseServiceKey = AddField(tabBackend, "Service Key:", 100, bgInput, fgInput, masked: true);

        // ── Simli Tab ──
        var tabSimli = new TabPage("🎭 Avatar") { BackColor = BackColor };
        txtSimliApiKey = AddField(tabSimli, "API Key:", 20, bgInput, fgInput, masked: true);
        txtSimliFaceId = AddField(tabSimli, "Face ID:", 60, bgInput, fgInput);
        chkSimliEnabled = new CheckBox { Text = "Enable Simli avatar during calls", Location = new Point(15, 100), AutoSize = true, ForeColor = fgInput };

        var lblMaxRecon = new Label { Text = "Max Reconnects:", Location = new Point(15, 133), AutoSize = true };
        nudMaxSimliReconnect = new NumericUpDown { Location = new Point(120, 130), Size = new Size(80, 23), Minimum = 0, Maximum = 50, Increment = 1, BackColor = bgInput, ForeColor = fgInput };
        var lblReconHint = new Label { Text = "0 = unlimited", Location = new Point(210, 133), AutoSize = true, ForeColor = Color.FromArgb(150, 150, 150), Font = new Font("Segoe UI", 7.5F) };

        tabSimli.Controls.AddRange(new Control[] { chkSimliEnabled, lblMaxRecon, nudMaxSimliReconnect, lblReconHint });

        // ── Company Tab ──
        var tabCompany = new TabPage("🚕 Company") { BackColor = BackColor };
        txtCompanyName = AddField(tabCompany, "Company Name:", 20, bgInput, fgInput);

        tabs.TabPages.AddRange(new TabPage[] { tabAi, tabDispatch, tabAudio, tabBackend, tabSimli, tabCompany });

        // ── OK / Cancel ──
        var pnlButtons = new Panel { Dock = DockStyle.Bottom, Height = 45, BackColor = BackColor };
        var btnOk = new Button { Text = "Save", Size = new Size(80, 30), Location = new Point(300, 8), BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.OK };
        btnOk.FlatAppearance.BorderSize = 0;
        btnOk.Click += (s, e) => WriteToSettings();

        var btnCancel = new Button { Text = "Cancel", Size = new Size(80, 30), Location = new Point(390, 8), FlatStyle = FlatStyle.Flat, ForeColor = Color.LightGray, DialogResult = DialogResult.Cancel };
        btnCancel.FlatAppearance.BorderSize = 0;

        pnlButtons.Controls.AddRange(new Control[] { btnOk, btnCancel });

        Controls.Add(tabs);
        Controls.Add(pnlButtons);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private TextBox AddField(TabPage tab, string label, int y, Color bg, Color fg, bool masked = false)
    {
        tab.Controls.Add(new Label { Text = label, Location = new Point(15, y + 3), AutoSize = true });
        var tb = new TextBox { Location = new Point(120, y), Size = new Size(330, 23), BackColor = bg, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle };
        if (masked) tb.UseSystemPasswordChar = true;
        tab.Controls.Add(tb);
        return tb;
    }

    private void LoadFromSettings()
    {
        txtOpenAiKey.Text = Settings.OpenAi.ApiKey;
        txtOpenAiModel.Text = Settings.OpenAi.Model;
        txtOpenAiVoice.Text = Settings.OpenAi.Voice;
        txtBsqdWebhook.Text = Settings.Dispatch.BsqdWebhookUrl;
        txtBsqdApiKey.Text = Settings.Dispatch.BsqdApiKey;
        txtWhatsAppWebhook.Text = Settings.Dispatch.WhatsAppWebhookUrl;
        cmbCodec.SelectedItem = Settings.Audio.PreferredCodec;
        if (cmbCodec.SelectedIndex < 0) cmbCodec.SelectedIndex = 0;
        nudVolumeBoost.Value = (decimal)Settings.Audio.VolumeBoost;
        nudEchoGuard.Value = Settings.Audio.EchoGuardMs;
        chkDiagnostics.Checked = Settings.Audio.EnableDiagnostics;
        nudCircuitBreaker.Value = Settings.Rtp.CircuitBreakerThreshold;
        txtSupabaseUrl.Text = Settings.Supabase.Url;
        txtSupabaseKey.Text = Settings.Supabase.AnonKey;
        txtSupabaseServiceKey.Text = Settings.Supabase.ServiceRoleKey;
        txtSimliApiKey.Text = Settings.Simli.ApiKey;
        txtSimliFaceId.Text = Settings.Simli.FaceId;
        chkSimliEnabled.Checked = Settings.Simli.Enabled;
        nudMaxSimliReconnect.Value = Settings.Rtp.MaxSimliReconnectAttempts;
        txtCompanyName.Text = Settings.Taxi.CompanyName;
    }

    private void WriteToSettings()
    {
        Settings.OpenAi.ApiKey = txtOpenAiKey.Text.Trim();
        Settings.OpenAi.Model = txtOpenAiModel.Text.Trim();
        Settings.OpenAi.Voice = txtOpenAiVoice.Text.Trim();
        Settings.Dispatch.BsqdWebhookUrl = txtBsqdWebhook.Text.Trim();
        Settings.Dispatch.BsqdApiKey = txtBsqdApiKey.Text.Trim();
        Settings.Dispatch.WhatsAppWebhookUrl = txtWhatsAppWebhook.Text.Trim();
        Settings.Audio.PreferredCodec = cmbCodec.SelectedItem?.ToString() ?? "PCMA";
        Settings.Audio.VolumeBoost = (double)nudVolumeBoost.Value;
        Settings.Audio.EchoGuardMs = (int)nudEchoGuard.Value;
        Settings.Audio.EnableDiagnostics = chkDiagnostics.Checked;
        Settings.Rtp.CircuitBreakerThreshold = (int)nudCircuitBreaker.Value;
        Settings.Supabase.Url = txtSupabaseUrl.Text.Trim();
        Settings.Supabase.AnonKey = txtSupabaseKey.Text.Trim();
        Settings.Supabase.ServiceRoleKey = txtSupabaseServiceKey.Text.Trim();
        Settings.Simli.ApiKey = txtSimliApiKey.Text.Trim();
        Settings.Simli.FaceId = txtSimliFaceId.Text.Trim();
        Settings.Simli.Enabled = chkSimliEnabled.Checked;
        Settings.Rtp.MaxSimliReconnectAttempts = (int)nudMaxSimliReconnect.Value;
        Settings.Taxi.CompanyName = txtCompanyName.Text.Trim();
    }

    private static CleanAppSettings Clone(CleanAppSettings src)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(src);
        return System.Text.Json.JsonSerializer.Deserialize<CleanAppSettings>(json) ?? new CleanAppSettings();
    }
}
