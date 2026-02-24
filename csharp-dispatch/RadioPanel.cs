using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using NAudio.Wave;

namespace DispatchSystem.UI;

/// <summary>
/// Push-To-Talk radio panel for the desktop dispatcher.
/// Audio format: Opus/WebM base64 chunks over MQTT.
/// 
/// MQTT Topics:
///   TX (dispatch → drivers):
///     radio/broadcast       — broadcast to all (optionally with "targets" array)
///     radio/driver/{id}     — targeted to a specific driver
///   RX (drivers → dispatch):
///     radio/channel          — driver-to-all transmission
/// 
/// Payload JSON:
///   { "driver": "DISPATCH", "name": "Dispatch", "audio": "<base64>", "mime": "audio/wav", "ts": 1234567890 }
///   For targeted: add "targets": ["driver_abc","driver_def"]
/// 
/// Dependencies: NAudio (NuGet)
/// </summary>
public class RadioPanel : Panel
{
    // ── UI ──
    private readonly Button _btnPtt;
    private readonly Label _lblStatus;
    private readonly CheckedListBox _driverSelector;
    private readonly Button _btnSelectAll;
    private readonly TrackBar _volumeSlider;
    private readonly ListBox _radioLog;
    private readonly Label _lblVolume;

    // ── Audio ──
    private WaveInEvent? _waveIn;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _playBuffer;
    private bool _pttActive;
    private float _volume = 0.8f;
    private readonly List<byte[]> _captureBuffer = new();

    // ── State ──
    private readonly HashSet<string> _selectedDriverIds = new();
    private readonly List<(string id, string name)> _knownDrivers = new();

    // ── Events ──
    /// <summary>Fires when PTT audio should be published. Args: (topic, jsonPayload)</summary>
    public event Action<string, string>? OnRadioTransmit;

    /// <summary>Fires log entries for the main log panel.</summary>
    public event Action<string, Color>? OnLog;

    public RadioPanel()
    {
        BackColor = Color.FromArgb(20, 20, 25);
        Dock = DockStyle.Fill;
        Padding = new Padding(8);

        // ── Title ──
        var title = new Label
        {
            Text = "📻 Radio PTT",
            ForeColor = Color.FromArgb(0, 188, 212),
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft
        };

        // ── Driver Selector ──
        _btnSelectAll = new Button
        {
            Text = "📢 All Drivers",
            Dock = DockStyle.Top,
            Height = 28,
            BackColor = Color.FromArgb(0, 120, 180),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnSelectAll.Click += (_, _) =>
        {
            _selectedDriverIds.Clear();
            for (int i = 0; i < _driverSelector.Items.Count; i++)
                _driverSelector.SetItemChecked(i, false);
            UpdatePttLabel();
        };

        _driverSelector = new CheckedListBox
        {
            Dock = DockStyle.Top,
            Height = 100,
            BackColor = Color.FromArgb(30, 30, 35),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9F),
            BorderStyle = BorderStyle.None,
            CheckOnClick = true
        };
        _driverSelector.ItemCheck += (_, args) =>
        {
            // Delay to get the new checked state
            BeginInvoke(() =>
            {
                _selectedDriverIds.Clear();
                for (int i = 0; i < _driverSelector.Items.Count; i++)
                {
                    if (_driverSelector.GetItemChecked(i) && i < _knownDrivers.Count)
                        _selectedDriverIds.Add(_knownDrivers[i].id);
                }
                UpdatePttLabel();
            });
        };

        // ── Status ──
        _lblStatus = new Label
        {
            Text = "Hold SPACE or click to talk",
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = 24,
            TextAlign = ContentAlignment.MiddleCenter
        };

        // ── PTT Button ──
        _btnPtt = new Button
        {
            Text = "🎙 PUSH TO TALK",
            Dock = DockStyle.Top,
            Height = 60,
            BackColor = Color.FromArgb(50, 50, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnPtt.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 90);
        _btnPtt.FlatAppearance.BorderSize = 2;
        _btnPtt.MouseDown += (_, _) => StartPtt();
        _btnPtt.MouseUp += (_, _) => StopPtt();

        // ── Volume ──
        _lblVolume = new Label
        {
            Text = "🔊 Volume: 80%",
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8F),
            Dock = DockStyle.Top,
            Height = 20,
            TextAlign = ContentAlignment.MiddleLeft
        };

        _volumeSlider = new TrackBar
        {
            Dock = DockStyle.Top,
            Minimum = 0,
            Maximum = 100,
            Value = 80,
            TickFrequency = 10,
            Height = 30,
            BackColor = Color.FromArgb(20, 20, 25)
        };
        _volumeSlider.ValueChanged += (_, _) =>
        {
            _volume = _volumeSlider.Value / 100f;
            _lblVolume.Text = $"🔊 Volume: {_volumeSlider.Value}%";
            if (_playBuffer != null)
            {
                // Volume is applied during playback
            }
        };

        // ── Radio Log ──
        _radioLog = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(15, 15, 20),
            ForeColor = Color.FromArgb(0, 188, 212),
            Font = new Font("Consolas", 8F),
            BorderStyle = BorderStyle.None
        };

        // ── Layout (add bottom-up for Dock.Top stacking) ──
        Controls.Add(_radioLog);
        Controls.Add(_volumeSlider);
        Controls.Add(_lblVolume);
        Controls.Add(_btnPtt);
        Controls.Add(_lblStatus);
        Controls.Add(_driverSelector);
        Controls.Add(_btnSelectAll);
        Controls.Add(title);

        // Init playback
        InitPlayback();
    }

    // ═══════════════════════════════════════════
    //  PUBLIC API
    // ═══════════════════════════════════════════

    /// <summary>Update the driver list from DispatchDb or MQTT tracking.</summary>
    public void UpdateDriverList(IEnumerable<(string id, string name, string status)> drivers)
    {
        if (InvokeRequired) { BeginInvoke(() => UpdateDriverList(drivers)); return; }

        _knownDrivers.Clear();
        _driverSelector.Items.Clear();

        foreach (var (id, name, status) in drivers)
        {
            var emoji = status.ToLower() switch
            {
                "online" or "available" or "free" => "🟢",
                "onjob" or "busy" or "on_job" => "🟡",
                "break" or "on_break" => "🔵",
                _ => "⚫"
            };
            _knownDrivers.Add((id, name));
            var idx = _driverSelector.Items.Add($"{emoji} {name} ({id})");
            if (_selectedDriverIds.Contains(id))
                _driverSelector.SetItemChecked(idx, true);
        }

        _btnSelectAll.Text = $"📢 All Drivers ({_knownDrivers.Count} online)";
        UpdatePttLabel();
    }

    /// <summary>Handle incoming radio audio from a driver (topic: radio/channel).</summary>
    public void HandleIncomingRadio(string jsonPayload)
    {
        if (InvokeRequired) { BeginInvoke(() => HandleIncomingRadio(jsonPayload)); return; }

        try
        {
            using var doc = JsonDocument.Parse(jsonPayload);
            var root = doc.RootElement;

            var driverName = root.TryGetProperty("name", out var n) ? n.GetString() ?? "Driver" : "Driver";
            var driverId = root.TryGetProperty("driver", out var d) ? d.GetString() ?? "" : "";
            var audioB64 = root.TryGetProperty("audio", out var a) ? a.GetString() : null;
            var mime = root.TryGetProperty("mime", out var m) ? m.GetString() ?? "" : "";

            if (string.IsNullOrEmpty(audioB64)) return;

            // Don't play our own transmissions
            if (driverId == "DISPATCH") return;

            AddLogEntry($"← {driverName}", Color.LimeGreen);
            OnLog?.Invoke($"📻 Radio RX from {driverName}", Color.LimeGreen);

            PlayBase64Audio(audioB64, mime);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"⚠ Radio parse error: {ex.Message}", Color.Orange);
        }
    }

    /// <summary>Handle keyboard shortcut — call from MainForm.ProcessCmdKey.</summary>
    public void HandleKeyDown(Keys key)
    {
        if (key == Keys.Space && !_pttActive) StartPtt();
    }

    public void HandleKeyUp(Keys key)
    {
        if (key == Keys.Space && _pttActive) StopPtt();
    }

    // ═══════════════════════════════════════════
    //  PTT CAPTURE
    // ═══════════════════════════════════════════

    private void StartPtt()
    {
        if (_pttActive) return;
        _pttActive = true;
        _captureBuffer.Clear();

        _btnPtt.BackColor = Color.FromArgb(180, 30, 30);
        _btnPtt.Text = "🔴 TRANSMITTING...";
        _btnPtt.FlatAppearance.BorderColor = Color.Red;
        _lblStatus.Text = _selectedDriverIds.Count == 0
            ? "🔴 BROADCASTING TO ALL..."
            : $"🔴 TO {_selectedDriverIds.Count} DRIVER(S)...";
        _lblStatus.ForeColor = Color.Red;

        try
        {
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1), // 16kHz mono PCM
                BufferMilliseconds = 500 // 500ms chunks to match web app
            };

            _waveIn.DataAvailable += (_, args) =>
            {
                if (!_pttActive || args.BytesRecorded == 0) return;

                // Accumulate audio data
                var chunk = new byte[args.BytesRecorded];
                Array.Copy(args.Buffer, chunk, args.BytesRecorded);
                _captureBuffer.Add(chunk);

                // Send every 500ms chunk as WAV base64
                var wavBytes = WrapInWavHeader(chunk, 16000, 16, 1);
                var base64 = Convert.ToBase64String(wavBytes);
                TransmitAudio(base64, "audio/wav");
            };

            _waveIn.StartRecording();

            var targetLabel = _selectedDriverIds.Count == 0
                ? "All Drivers"
                : _selectedDriverIds.Count == 1
                    ? _knownDrivers.FirstOrDefault(d => _selectedDriverIds.Contains(d.id)).name ?? "Driver"
                    : $"{_selectedDriverIds.Count} drivers";
            AddLogEntry($"→ {targetLabel}", Color.FromArgb(0, 188, 212));
        }
        catch (Exception ex)
        {
            _pttActive = false;
            ResetPttButton();
            OnLog?.Invoke($"❌ Mic error: {ex.Message}", Color.Red);
        }
    }

    private void StopPtt()
    {
        if (!_pttActive) return;
        _pttActive = false;

        try
        {
            _waveIn?.StopRecording();
            _waveIn?.Dispose();
            _waveIn = null;
        }
        catch { /* ignore */ }

        ResetPttButton();
    }

    private void ResetPttButton()
    {
        if (InvokeRequired) { BeginInvoke(ResetPttButton); return; }
        _btnPtt.BackColor = Color.FromArgb(50, 50, 60);
        _btnPtt.Text = "🎙 PUSH TO TALK";
        _btnPtt.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 90);
        UpdatePttLabel();
    }

    private void TransmitAudio(string base64, string mimeType)
    {
        var payload = new Dictionary<string, object>
        {
            ["driver"] = "DISPATCH",
            ["name"] = "Dispatch",
            ["audio"] = base64,
            ["mime"] = mimeType,
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        if (_selectedDriverIds.Count == 0)
        {
            // Broadcast to all
            var json = JsonSerializer.Serialize(payload);
            OnRadioTransmit?.Invoke("radio/broadcast", json);
        }
        else
        {
            // Send to each selected driver's private topic
            foreach (var dId in _selectedDriverIds)
            {
                var json = JsonSerializer.Serialize(payload);
                OnRadioTransmit?.Invoke($"radio/driver/{dId}", json);
            }

            // Also broadcast with targets filter
            payload["targets"] = _selectedDriverIds.ToArray();
            var broadcastJson = JsonSerializer.Serialize(payload);
            OnRadioTransmit?.Invoke("radio/broadcast", broadcastJson);
        }
    }

    // ═══════════════════════════════════════════
    //  AUDIO PLAYBACK
    // ═══════════════════════════════════════════

    private void InitPlayback()
    {
        _playBuffer = new BufferedWaveProvider(new WaveFormat(16000, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(10),
            DiscardOnBufferOverflow = true
        };

        _waveOut = new WaveOutEvent();
        _waveOut.Init(_playBuffer);
        _waveOut.Volume = _volume;
        _waveOut.Play();
    }

    private void PlayBase64Audio(string base64, string mime)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);

            if (_waveOut != null)
                _waveOut.Volume = _volume;

            // If it's WAV, strip header and feed PCM directly
            if (mime.Contains("wav") && bytes.Length > 44)
            {
                var pcm = new byte[bytes.Length - 44];
                Array.Copy(bytes, 44, pcm, 0, pcm.Length);
                _playBuffer?.AddSamples(pcm, 0, pcm.Length);
                return;
            }

            // For WebM/Opus from web browsers — decode via NAudio
            // NAudio can't natively decode Opus/WebM in all cases.
            // Fallback: save to temp file and use MediaFoundationReader
            var tempFile = Path.Combine(Path.GetTempPath(), $"radio_{Guid.NewGuid():N}.webm");
            File.WriteAllBytes(tempFile, bytes);

            try
            {
                using var reader = new MediaFoundationReader(tempFile);
                // Resample to 16kHz mono if needed
                using var resampler = new MediaFoundationResampler(reader, new WaveFormat(16000, 16, 1));
                var buffer = new byte[4096];
                int read;
                while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
                {
                    _playBuffer?.AddSamples(buffer, 0, read);
                }
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"⚠ Audio playback error: {ex.Message}", Color.Orange);
        }
    }

    // ═══════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════

    private void UpdatePttLabel()
    {
        if (InvokeRequired) { BeginInvoke(UpdatePttLabel); return; }
        _lblStatus.Text = _selectedDriverIds.Count == 0
            ? "Hold SPACE or click — broadcast to all"
            : $"Hold SPACE or click — {_selectedDriverIds.Count} driver(s) selected";
        _lblStatus.ForeColor = Color.Gray;
    }

    private void AddLogEntry(string text, Color color)
    {
        if (InvokeRequired) { BeginInvoke(() => AddLogEntry(text, color)); return; }
        var time = DateTime.Now.ToString("HH:mm:ss");
        _radioLog.Items.Add($"[{time}] {text}");
        if (_radioLog.Items.Count > 50)
            _radioLog.Items.RemoveAt(0);
        _radioLog.TopIndex = _radioLog.Items.Count - 1;
    }

    /// <summary>Wraps raw PCM bytes in a minimal WAV header.</summary>
    private static byte[] WrapInWavHeader(byte[] pcm, int sampleRate, int bitsPerSample, int channels)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;

        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + pcm.Length);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16); // chunk size
        bw.Write((short)1); // PCM
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)blockAlign);
        bw.Write((short)bitsPerSample);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(pcm.Length);
        bw.Write(pcm);

        return ms.ToArray();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _waveIn?.StopRecording();
            _waveIn?.Dispose();
            _waveOut?.Stop();
            _waveOut?.Dispose();
        }
        base.Dispose(disposing);
    }
}
