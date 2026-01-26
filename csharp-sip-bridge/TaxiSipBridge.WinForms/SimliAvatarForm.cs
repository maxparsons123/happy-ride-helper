using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TaxiSipBridge;

/// <summary>
/// A borderless, always-on-top window that displays the Simli AI avatar.
/// </summary>
public class SimliAvatarForm : Form
{
    private readonly PictureBox _videoPicture;
    private readonly Label _statusLabel;
    private readonly Panel _headerPanel;
    private Point _dragOffset;
    private bool _isDragging;

    public SimliAvatarForm()
    {
        // Form setup - borderless floating window
        Text = "Ada Avatar";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(320, 400);
        BackColor = Color.FromArgb(30, 30, 35);
        TopMost = true;
        ShowInTaskbar = false;

        // Position in bottom-right corner
        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(screen.Right - Width - 20, screen.Bottom - Height - 20);

        // Header for dragging
        _headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 30,
            BackColor = Color.FromArgb(45, 45, 50),
            Cursor = Cursors.SizeAll
        };
        
        var titleLabel = new Label
        {
            Text = "🎭 Ada",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 0, 0, 0)
        };
        
        var closeButton = new Button
        {
            Text = "×",
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(30, 30),
            Dock = DockStyle.Right,
            Cursor = Cursors.Hand
        };
        closeButton.FlatAppearance.BorderSize = 0;
        closeButton.Click += (s, e) => Hide();

        _headerPanel.Controls.Add(titleLabel);
        _headerPanel.Controls.Add(closeButton);

        // Enable dragging
        _headerPanel.MouseDown += (s, e) => { _isDragging = true; _dragOffset = e.Location; };
        _headerPanel.MouseUp += (s, e) => _isDragging = false;
        _headerPanel.MouseMove += (s, e) =>
        {
            if (_isDragging)
            {
                Location = new Point(
                    Location.X + e.X - _dragOffset.X,
                    Location.Y + e.Y - _dragOffset.Y);
            }
        };
        titleLabel.MouseDown += (s, e) => { _isDragging = true; _dragOffset = e.Location; };
        titleLabel.MouseUp += (s, e) => _isDragging = false;
        titleLabel.MouseMove += (s, e) =>
        {
            if (_isDragging)
            {
                Location = new Point(
                    Location.X + e.X - _dragOffset.X,
                    Location.Y + e.Y - _dragOffset.Y);
            }
        };

        // Video display area
        _videoPicture = new PictureBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            SizeMode = PictureBoxSizeMode.Zoom
        };

        // Status label
        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            BackColor = Color.FromArgb(40, 40, 45),
            ForeColor = Color.Gray,
            Text = "Waiting for audio...",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 9)
        };

        Controls.Add(_videoPicture);
        Controls.Add(_statusLabel);
        Controls.Add(_headerPanel);

        // Round corners
        Region = CreateRoundedRegion(ClientRectangle, 12);
    }

    private static Region CreateRoundedRegion(Rectangle rect, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(rect.X, rect.Y, radius * 2, radius * 2, 180, 90);
        path.AddArc(rect.Right - radius * 2, rect.Y, radius * 2, radius * 2, 270, 90);
        path.AddArc(rect.Right - radius * 2, rect.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(rect.X, rect.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();
        return new Region(path);
    }

    /// <summary>
    /// Update the avatar video frame.
    /// </summary>
    public void UpdateVideoFrame(byte[] frameData)
    {
        if (InvokeRequired)
        {
            try { Invoke(new Action(() => UpdateVideoFrame(frameData))); }
            catch { }
            return;
        }

        try
        {
            using var ms = new MemoryStream(frameData);
            var image = Image.FromStream(ms);
            var oldImage = _videoPicture.Image;
            _videoPicture.Image = image;
            oldImage?.Dispose();
        }
        catch
        {
            // Failed to decode frame - might be encoded video that needs decoding
        }
    }

    /// <summary>
    /// Update the status text.
    /// </summary>
    public void SetStatus(string status)
    {
        if (InvokeRequired)
        {
            try { Invoke(new Action(() => SetStatus(status))); }
            catch { }
            return;
        }

        _statusLabel.Text = status;
        _statusLabel.ForeColor = status.Contains("Speaking") ? Color.LightGreen : Color.Gray;
    }

    /// <summary>
    /// Set the avatar to "speaking" visual state.
    /// </summary>
    public void SetSpeaking(bool isSpeaking)
    {
        SetStatus(isSpeaking ? "🔊 Speaking..." : "👂 Listening...");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }
}
