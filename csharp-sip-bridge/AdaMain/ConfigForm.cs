using AdaMain.Config;

namespace AdaMain;

/// <summary>
/// Settings dialog for API keys, dispatch config, and audio options.
/// </summary>
public class ConfigForm : Form
{
    public AppSettings Settings { get; private set; }

    private TabControl tabs;
    private TextBox txtOpenAiKey, txtOpenAiModel, txtOpenAiVoice;
    private TextBox txtGoogleMapsKey;
    private TextBox txtSimliApiKey, txtSimliFaceId;
    private TextBox txtDeepgramKey;
    private TextBox txtBsqdWebhook, txtBsqdApiKey, txtWhatsAppWebhook;
    private ComboBox cmbCodec;
    private NumericUpDown nudVolumeBoost, nudEchoGuard;

    public ConfigForm(AppSettings settings)
    {
        Settings = Clone(settings);
        BuildUi();
        LoadFromSettings();
    }

    private void BuildUi()
    {
        Text = "⚙ Settings";
        Size = new Size(500, 420);
        MinimumSize = new Size(460, 380);
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

        // ── Simli Tab ──
        var tabSimli = new TabPage("🎭 Simli") { BackColor = BackColor };
        txtSimliApiKey = AddField(tabSimli, "API Key:", 20, bgInput, fgInput, masked: true);
        txtSimliFaceId = AddField(tabSimli, "Face ID:", 60, bgInput, fgInput);

        // ── STT / Deepgram Tab ──
        var tabStt = new TabPage("🎤 STT") { BackColor = BackColor };
        txtDeepgramKey = AddField(tabStt, "Deepgram Key:", 20, bgInput, fgInput, masked: true);

        // ── Google Maps Tab ──
        var tabMaps = new TabPage("🗺 Maps") { BackColor = BackColor };
        txtGoogleMapsKey = AddField(tabMaps, "API Key:", 20, bgInput, fgInput, masked: true);

        // ── Dispatch Tab ──
        var tabDispatch = new TabPage("📡 Dispatch") { BackColor = BackColor };
        txtBsqdWebhook = AddField(tabDispatch, "Webhook URL:", 20, bgInput, fgInput);
        txtBsqdApiKey = AddField(tabDispatch, "API Key:", 60, bgInput, fgInput, masked: true);
        txtWhatsAppWebhook = AddField(tabDispatch, "WhatsApp URL:", 100, bgInput, fgInput);

        // ── Audio Tab ──
        var tabAudio = new TabPage("🔊 Audio") { BackColor = BackColor };
        var lblCodec = new Label { Text = "Codec:", Location = new Point(15, 23), AutoSize = true };
        cmbCodec = new ComboBox { Location = new Point(120, 20), Size = new Size(100, 23), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = bgInput, ForeColor = fgInput };
        cmbCodec.Items.AddRange(new object[] { "PCMA", "PCMU", "G722" });

        var lblVol = new Label { Text = "Volume Boost:", Location = new Point(15, 56), AutoSize = true };
        nudVolumeBoost = new NumericUpDown { Location = new Point(120, 53), Size = new Size(80, 23), Minimum = 0, Maximum = 10, DecimalPlaces = 1, Increment = 0.5m, BackColor = bgInput, ForeColor = fgInput };

        var lblEcho = new Label { Text = "Echo Guard (ms):", Location = new Point(15, 89), AutoSize = true };
        nudEchoGuard = new NumericUpDown { Location = new Point(120, 86), Size = new Size(80, 23), Minimum = 0, Maximum = 1000, Increment = 50, BackColor = bgInput, ForeColor = fgInput };

        tabAudio.Controls.AddRange(new Control[] { lblCodec, cmbCodec, lblVol, nudVolumeBoost, lblEcho, nudEchoGuard });

        tabs.TabPages.AddRange(new TabPage[] { tabAi, tabSimli, tabStt, tabMaps, tabDispatch, tabAudio });

        // ── OK / Cancel ──
        var pnlButtons = new Panel { Dock = DockStyle.Bottom, Height = 45, BackColor = BackColor };
        var btnOk = new Button { Text = "Save", Size = new Size(80, 30), Location = new Point(300, 8), BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.OK };
        btnOk.FlatAppearance.BorderSize = 0;
        btnOk.Click += (s, e) => { WriteToSettings(); };

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
        txtSimliApiKey.Text = Settings.Simli.ApiKey;
        txtSimliFaceId.Text = Settings.Simli.FaceId;
        txtDeepgramKey.Text = Settings.Stt.DeepgramApiKey;
        txtGoogleMapsKey.Text = Settings.GoogleMaps.ApiKey;
        txtBsqdWebhook.Text = Settings.Dispatch.BsqdWebhookUrl;
        txtBsqdApiKey.Text = Settings.Dispatch.BsqdApiKey;
        txtWhatsAppWebhook.Text = Settings.Dispatch.WhatsAppWebhookUrl;

        cmbCodec.SelectedItem = Settings.Audio.PreferredCodec;
        if (cmbCodec.SelectedIndex < 0) cmbCodec.SelectedIndex = 0;
        nudVolumeBoost.Value = (decimal)Settings.Audio.VolumeBoost;
        nudEchoGuard.Value = Settings.Audio.EchoGuardMs;
    }

    private void WriteToSettings()
    {
        Settings.OpenAi.ApiKey = txtOpenAiKey.Text.Trim();
        Settings.OpenAi.Model = txtOpenAiModel.Text.Trim();
        Settings.OpenAi.Voice = txtOpenAiVoice.Text.Trim();
        Settings.Simli.ApiKey = txtSimliApiKey.Text.Trim();
        Settings.Simli.FaceId = txtSimliFaceId.Text.Trim();
        Settings.Stt.DeepgramApiKey = txtDeepgramKey.Text.Trim();
        Settings.GoogleMaps.ApiKey = txtGoogleMapsKey.Text.Trim();
        Settings.Dispatch.BsqdWebhookUrl = txtBsqdWebhook.Text.Trim();
        Settings.Dispatch.BsqdApiKey = txtBsqdApiKey.Text.Trim();
        Settings.Dispatch.WhatsAppWebhookUrl = txtWhatsAppWebhook.Text.Trim();

        Settings.Audio.PreferredCodec = cmbCodec.SelectedItem?.ToString() ?? "PCMA";
        Settings.Audio.VolumeBoost = (double)nudVolumeBoost.Value;
        Settings.Audio.EchoGuardMs = (int)nudEchoGuard.Value;
    }

    private static AppSettings Clone(AppSettings src)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(src);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }
}
