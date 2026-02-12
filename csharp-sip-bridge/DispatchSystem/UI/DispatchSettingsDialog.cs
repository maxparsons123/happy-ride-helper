namespace DispatchSystem.UI;

/// <summary>
/// Settings dialog for webhook port, auto-dispatch interval, bidding config, iCabbi keys, and sound.
/// </summary>
public sealed class DispatchSettingsDialog : Form
{
    public int WebhookPort { get; private set; }
    public int AutoDispatchIntervalSec { get; private set; }
    public bool SoundEnabled { get; private set; }
    public int BiddingWindowSec { get; private set; }
    public double BidRadiusKm { get; private set; }

    // iCabbi
    public string IcabbiAppKey { get; private set; }
    public string IcabbiSecretKey { get; private set; }
    public string IcabbiTenantBase { get; private set; }

    public DispatchSettingsDialog(int currentPort, int currentIntervalSec, bool soundEnabled,
        int biddingWindowSec = 20, double bidRadiusKm = 10.0,
        string icabbiAppKey = "", string icabbiSecretKey = "", string icabbiTenantBase = "")
    {
        Text = "⚙ Dispatch Settings";
        Size = new Size(420, 620);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.FromArgb(35, 35, 40);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.5F);

        WebhookPort = currentPort;
        AutoDispatchIntervalSec = currentIntervalSec;
        SoundEnabled = soundEnabled;
        BiddingWindowSec = biddingWindowSec;
        BidRadiusKm = bidRadiusKm;
        IcabbiAppKey = icabbiAppKey;
        IcabbiSecretKey = icabbiSecretKey;
        IcabbiTenantBase = icabbiTenantBase;

        var y = 20;

        AddLabel("Webhook Port:", 20, y);
        var txtPort = new TextBox
        {
            Text = currentPort.ToString(),
            Location = new Point(180, y),
            Width = 180,
            BackColor = Color.FromArgb(50, 50, 55),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(txtPort);
        y += 40;

        AddLabel("Auto-Dispatch (sec):", 20, y);
        var txtInterval = new TextBox
        {
            Text = currentIntervalSec.ToString(),
            Location = new Point(180, y),
            Width = 180,
            BackColor = Color.FromArgb(50, 50, 55),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(txtInterval);
        y += 40;

        // ── Bidding Settings ──
        var lblBidHeader = new Label
        {
            Text = "── Bidding Mode ──",
            Location = new Point(20, y),
            AutoSize = true,
            ForeColor = Color.Gold,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
        };
        Controls.Add(lblBidHeader);
        y += 30;

        AddLabel("Bid Window (sec):", 20, y);
        var txtBidWindow = new TextBox
        {
            Text = biddingWindowSec.ToString(),
            Location = new Point(180, y),
            Width = 180,
            BackColor = Color.FromArgb(50, 50, 55),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(txtBidWindow);
        y += 40;

        AddLabel("Bid Radius (km):", 20, y);
        var txtBidRadius = new TextBox
        {
            Text = bidRadiusKm.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
            Location = new Point(180, y),
            Width = 180,
            BackColor = Color.FromArgb(50, 50, 55),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(txtBidRadius);
        y += 40;

        var chkSound = new CheckBox
        {
            Text = "🔔 Play sound on new job",
            Checked = soundEnabled,
            Location = new Point(20, y),
            AutoSize = true,
            ForeColor = Color.White
        };
        Controls.Add(chkSound);
        y += 40;

        // ── iCabbi Settings ──
        var lblIcabbiHeader = new Label
        {
            Text = "── iCabbi Integration ──",
            Location = new Point(20, y),
            AutoSize = true,
            ForeColor = Color.MediumPurple,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
        };
        Controls.Add(lblIcabbiHeader);
        y += 30;

        AddLabel("App Key:", 20, y);
        var txtAppKey = new TextBox
        {
            Text = icabbiAppKey,
            Location = new Point(180, y),
            Width = 180,
            BackColor = Color.FromArgb(50, 50, 55),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            UseSystemPasswordChar = true
        };
        Controls.Add(txtAppKey);
        y += 35;

        AddLabel("Secret Key:", 20, y);
        var txtSecretKey = new TextBox
        {
            Text = icabbiSecretKey,
            Location = new Point(180, y),
            Width = 180,
            BackColor = Color.FromArgb(50, 50, 55),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            UseSystemPasswordChar = true
        };
        Controls.Add(txtSecretKey);
        y += 35;

        AddLabel("Tenant URL:", 20, y);
        var txtTenant = new TextBox
        {
            Text = icabbiTenantBase,
            Location = new Point(180, y),
            Width = 180,
            BackColor = Color.FromArgb(50, 50, 55),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(txtTenant);
        y += 50;

        var btnOk = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            BackColor = Color.FromArgb(0, 120, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(90, 34),
            Location = new Point(110, y)
        };
        btnOk.Click += (_, _) =>
        {
            if (int.TryParse(txtPort.Text, out var p) && p > 0) WebhookPort = p;
            if (int.TryParse(txtInterval.Text, out var i) && i >= 5) AutoDispatchIntervalSec = i;
            if (int.TryParse(txtBidWindow.Text, out var bw) && bw >= 5) BiddingWindowSec = bw;
            if (double.TryParse(txtBidRadius.Text, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var br) && br > 0) BidRadiusKm = br;
            SoundEnabled = chkSound.Checked;
            IcabbiAppKey = txtAppKey.Text.Trim();
            IcabbiSecretKey = txtSecretKey.Text.Trim();
            IcabbiTenantBase = txtTenant.Text.Trim();
        };

        var btnCancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            BackColor = Color.FromArgb(80, 40, 40),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(90, 34),
            Location = new Point(215, y)
        };

        Controls.AddRange(new Control[] { btnOk, btnCancel });
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private void AddLabel(string text, int x, int y)
    {
        Controls.Add(new Label
        {
            Text = text,
            Location = new Point(x, y + 2),
            AutoSize = true,
            ForeColor = Color.White
        });
    }
}
