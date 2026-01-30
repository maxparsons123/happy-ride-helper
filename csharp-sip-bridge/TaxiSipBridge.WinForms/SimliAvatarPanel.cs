using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TaxiSipBridge;

/// <summary>
/// Reusable Simli Avatar panel that can be dropped into any WinForms application.
/// Handles connection, audio streaming, and video display.
/// </summary>
public class SimliAvatarPanel : UserControl
{
    private readonly PictureBox _videoPicture;
    private readonly Label _statusLabel;
    private readonly Label _titleLabel;
    
    private SimliAvatarClient? _client;
    private string? _apiKey;
    private string? _faceId;

    /// <summary>Fired for log messages.</summary>
    public event Action<string>? OnLog;

    /// <summary>Whether the avatar is currently connected.</summary>
    public bool IsConnected => _client?.IsConnected ?? false;

    public SimliAvatarPanel()
    {
        // Panel setup
        Size = new Size(200, 220);
        BackColor = Color.FromArgb(30, 30, 35);

        // Title
        _titleLabel = new Label
        {
            Text = "🎭 Avatar",
            Dock = DockStyle.Top,
            Height = 28,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(45, 45, 50)
        };

        // Video display
        _videoPicture = new PictureBox
        {
            Location = new Point(10, 35),
            Size = new Size(180, 150),
            BackColor = Color.Black,
            SizeMode = PictureBoxSizeMode.Zoom
        };

        // Status label
        _statusLabel = new Label
        {
            Text = "Not connected",
            Location = new Point(10, 190),
            Size = new Size(180, 20),
            ForeColor = Color.Gray,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 9)
        };

        Controls.Add(_videoPicture);
        Controls.Add(_statusLabel);
        Controls.Add(_titleLabel);
    }

    /// <summary>
    /// Configure the avatar with API credentials.
    /// </summary>
    public void Configure(string apiKey, string faceId)
    {
        _apiKey = apiKey;
        _faceId = faceId;
        SetStatus("Configured", Color.Orange);
        OnLog?.Invoke($"🎭 SimliAvatarPanel configured (Face: {faceId[..Math.Min(8, faceId.Length)]}...)");
    }

    /// <summary>
    /// Connect to Simli service.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey) || string.IsNullOrEmpty(_faceId))
        {
            OnLog?.Invoke("🎭 Cannot connect: API key or Face ID not configured");
            return;
        }

        if (_client?.IsConnected == true)
        {
            OnLog?.Invoke("🎭 Already connected");
            return;
        }

        // Cleanup any existing client
        await DisconnectAsync();

        _client = new SimliAvatarClient(_apiKey, _faceId);
        _client.OnLog += msg => OnLog?.Invoke(msg);
        _client.OnConnected += () => SafeInvoke(() => SetStatus("🟢 Connected", Color.LightGreen));
        _client.OnDisconnected += () => SafeInvoke(() => SetStatus("Disconnected", Color.Gray));
        _client.OnVideoFrame += UpdateVideoFrame;

        SetStatus("Connecting...", Color.Yellow);

        try
        {
            await _client.ConnectAsync(ct);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"🎭 Connection failed: {ex.Message}");
            SetStatus("Connection failed", Color.Red);
        }
    }

    /// <summary>
    /// Send PCM24 audio (24kHz 16-bit mono) to drive lip-sync.
    /// </summary>
    public async Task SendAudioAsync(byte[] pcm24Audio)
    {
        if (_client?.IsConnected != true) return;

        try
        {
            SafeInvoke(() => SetStatus("🔊 Speaking...", Color.LightGreen));
            await _client.SendPcm24AudioAsync(pcm24Audio);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"🎭 Audio send error: {ex.Message}");
        }
    }

    /// <summary>
    /// Notify that speaking has stopped.
    /// </summary>
    public void SetSpeaking(bool isSpeaking)
    {
        SafeInvoke(() =>
        {
            if (_client?.IsConnected == true)
            {
                SetStatus(isSpeaking ? "🔊 Speaking..." : "👂 Listening...", 
                    isSpeaking ? Color.LightGreen : Color.LightBlue);
            }
        });
    }

    /// <summary>
    /// Disconnect from Simli service.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_client != null)
        {
            try { await _client.DisconnectAsync(); } catch { }
            _client.Dispose();
            _client = null;
        }

        SafeInvoke(() =>
        {
            _videoPicture.Image?.Dispose();
            _videoPicture.Image = null;
            SetStatus("Not connected", Color.Gray);
        });
    }

    /// <summary>
    /// Get a callback action that can be passed to call handlers.
    /// </summary>
    public Action<bool> GetSpeakingCallback() => SetSpeaking;

    /// <summary>
    /// Get a Func that sends audio - can be used as a delegate.
    /// </summary>
    public Func<byte[], Task> GetAudioSender() => SendAudioAsync;

    private void UpdateVideoFrame(byte[] frameData)
    {
        SafeInvoke(() =>
        {
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
                // Failed to decode frame
            }
        });
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    private void SafeInvoke(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { Invoke(action); } catch { }
        }
        else
        {
            action();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _client?.Dispose();
            _videoPicture.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
