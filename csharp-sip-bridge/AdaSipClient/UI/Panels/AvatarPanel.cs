namespace AdaSipClient.UI.Panels;

/// <summary>
/// Simli avatar viewport. Currently a PictureBox placeholder;
/// can be swapped to WebView2 when Simli integration is wired.
/// </summary>
public sealed class AvatarPanel : UserControl
{
    private readonly PictureBox _viewport;
    private readonly Label _lblStatus;

    public PictureBox Viewport => _viewport;

    public AvatarPanel()
    {
        BackColor = Theme.PanelBg;
        Padding = new Padding(8);

        var title = Theme.StyledLabel("🎭 Avatar");
        title.Font = Theme.Header;
        title.Dock = DockStyle.Top;

        _viewport = new PictureBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            SizeMode = PictureBoxSizeMode.Zoom
        };

        _lblStatus = new Label
        {
            Text = "Waiting for call...",
            Dock = DockStyle.Bottom,
            Height = 24,
            ForeColor = Theme.TextSecondary,
            Font = Theme.Small,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Theme.PanelBg
        };

        Controls.Add(_viewport);
        Controls.Add(_lblStatus);
        Controls.Add(title);
    }

    public void SetStatus(string text, Color? color = null)
    {
        if (InvokeRequired)
        {
            Invoke(() => SetStatus(text, color));
            return;
        }
        _lblStatus.Text = text;
        _lblStatus.ForeColor = color ?? Theme.TextSecondary;
    }
}
