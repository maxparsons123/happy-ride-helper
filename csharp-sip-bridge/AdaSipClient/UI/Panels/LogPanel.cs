using AdaSipClient.Core;

namespace AdaSipClient.UI.Panels;

/// <summary>
/// Scrollable log viewer with copy and clear support.
/// Implements ILogSink for direct use by services.
/// </summary>
public sealed class LogPanel : UserControl, ILogSink
{
    private readonly ListBox _lstLogs;
    private const int MaxLines = 2000;

    public LogPanel()
    {
        BackColor = Theme.PanelBg;
        Padding = new Padding(8);

        var title = Theme.StyledLabel("📋 Logs");
        title.Font = Theme.Header;
        title.Dock = DockStyle.Top;

        _lstLogs = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = Theme.Mono,
            BackColor = Theme.LogBg,
            ForeColor = Theme.TextSuccess,
            BorderStyle = BorderStyle.None,
            SelectionMode = SelectionMode.MultiExtended
        };
        _lstLogs.KeyDown += OnKeyDown;

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Theme.PanelBg,
            Padding = new Padding(0, 4, 0, 0)
        };

        var btnClear = Theme.StyledButton("Clear", Color.FromArgb(75, 85, 99));
        btnClear.Width = 80;
        btnClear.Click += (_, _) => _lstLogs.Items.Clear();

        var btnCopy = Theme.StyledButton("📋 Copy All", Theme.AccentBlue);
        btnCopy.Width = 100;
        btnCopy.Click += (_, _) => CopyAll();

        toolbar.Controls.Add(btnClear);
        toolbar.Controls.Add(btnCopy);

        Controls.Add(_lstLogs);
        Controls.Add(toolbar);
        Controls.Add(title);
    }

    // ── ILogSink ──

    public void Log(string message) => AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    public void LogWarning(string message) => AppendLine($"[{DateTime.Now:HH:mm:ss}] ⚠ {message}");
    public void LogError(string message) => AppendLine($"[{DateTime.Now:HH:mm:ss}] ❌ {message}");

    private void AppendLine(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLine(line));
            return;
        }

        _lstLogs.Items.Add(line);
        if (_lstLogs.Items.Count > MaxLines)
            _lstLogs.Items.RemoveAt(0);

        _lstLogs.TopIndex = _lstLogs.Items.Count - 1;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C)
        {
            var selected = string.Join(Environment.NewLine,
                _lstLogs.SelectedItems.Cast<string>());
            if (!string.IsNullOrEmpty(selected))
                Clipboard.SetText(selected);
            e.Handled = true;
        }
    }

    private void CopyAll()
    {
        var all = string.Join(Environment.NewLine,
            _lstLogs.Items.Cast<string>());
        if (!string.IsNullOrEmpty(all))
            Clipboard.SetText(all);
    }
}
