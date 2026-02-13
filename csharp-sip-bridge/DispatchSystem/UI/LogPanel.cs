namespace DispatchSystem.UI;

/// <summary>
/// Scrolling log panel with color-coded entries.
/// </summary>
public sealed class LogPanel : Panel
{
    private readonly RichTextBox _log;

    public LogPanel()
    {
        var lbl = new Label
        {
            Text = "📋 Log",
            Dock = DockStyle.Top,
            Height = 22,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(40, 40, 45)
        };

        _log = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(15, 15, 18),
            ForeColor = Color.LightGreen,
            Font = new Font("Cascadia Mono", 8.5F),
            BorderStyle = BorderStyle.None
        };

        Controls.Add(_log);
        Controls.Add(lbl);
    }

    public void AppendLog(string message, Color? color = null)
    {
        if (_log.IsDisposed) return;
        if (_log.InvokeRequired) { _log.BeginInvoke(() => AppendLog(message, color)); return; }

        _log.SelectionStart = _log.TextLength;
        _log.SelectionColor = color ?? Color.LightGreen;
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        _log.ScrollToCaret();

        if (_log.Lines.Length > 500)
        {
            _log.SelectionStart = 0;
            _log.SelectionLength = _log.GetFirstCharIndexFromLine(100);
            _log.SelectedText = "";
        }
    }
}
