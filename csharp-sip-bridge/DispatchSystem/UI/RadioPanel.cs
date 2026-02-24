using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DispatchSystem.Radio;

namespace DispatchSystem.UI;

/// <summary>
/// Push-To-Talk radio panel for the desktop dispatcher.
/// Uses WebRTC peer connections for audio streaming with MQTT signaling.
/// 
/// MQTT Signaling Topics:
///   radio/webrtc/signal/{peerId}  — SDP offers/answers + ICE candidates
///   radio/webrtc/presence         — peer discovery
/// 
/// Dependencies: SIPSorcery, NAudio (NuGet)
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
    private readonly Label _lblPeers;

    // ── WebRTC Engine ──
    private readonly WebRtcRadioEngine _engine;
    private bool _pttActive;

    // ── State ──
    private readonly HashSet<string> _selectedDriverIds = new();
    private readonly List<(string id, string name)> _knownDrivers = new();
    private string? _targetDriverId;
    private bool _isTargetedPtt;

    // ── Events ──
    /// <summary>Fires when a signaling message should be published via MQTT. Args: (topic, jsonPayload)</summary>
    public event Action<string, string>? OnRadioTransmit;

    /// <summary>Fires log entries for the main log panel.</summary>
    public event Action<string, Color>? OnLog;

    public RadioPanel()
    {
        BackColor = Color.FromArgb(20, 20, 25);
        Dock = DockStyle.Fill;
        Padding = new Padding(8);

        // ── WebRTC Engine ──
        _engine = new WebRtcRadioEngine();
        _engine.OnSignalingSend += (topic, json) => OnRadioTransmit?.Invoke(topic, json);
        _engine.OnLog += (msg, isError) =>
        {
            var color = isError ? Color.Orange : Color.FromArgb(0, 188, 212);
            AddLogEntry(msg, color);
            OnLog?.Invoke($"📻 {msg}", color);
        };
        _engine.OnPeerCountChanged += count =>
        {
            if (InvokeRequired) { BeginInvoke(() => UpdatePeerCount(count)); return; }
            UpdatePeerCount(count);
        };

        // ── Title ──
        var title = new Label
        {
            Text = "📻 Radio PTT (WebRTC)",
            ForeColor = Color.FromArgb(0, 188, 212),
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft
        };

        // ── Peer count ──
        _lblPeers = new Label
        {
            Text = "🔗 0 peers connected",
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8F),
            Dock = DockStyle.Top,
            Height = 20,
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
            var vol = _volumeSlider.Value / 100f;
            _engine.SetVolume(vol);
            _lblVolume.Text = $"🔊 Volume: {_volumeSlider.Value}%";
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
        Controls.Add(_lblPeers);
        Controls.Add(title);

        // Announce presence on load
        _engine.AnnouncePresence();
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

    /// <summary>Handle incoming WebRTC presence message.</summary>
    public void HandlePresence(string jsonPayload) => _engine.HandlePresence(jsonPayload);

    /// <summary>Handle incoming WebRTC signaling message (SDP/ICE).</summary>
    public void HandleSignaling(string jsonPayload) => _ = _engine.HandleSignaling(jsonPayload);

    /// <summary>Handle keyboard shortcut — call from MainForm.ProcessCmdKey.</summary>
    public void HandleKeyDown(Keys key)
    {
        if (key == Keys.Space && !_pttActive) StartPtt();
    }

    public void HandleKeyUp(Keys key)
    {
        if (key == Keys.Space && _pttActive) StopPtt();
    }

    /// <summary>
    /// Start PTT targeted at a single driver (only enables that peer's track).
    /// Used by long-press on driver grid row or map marker.
    /// </summary>
    public void StartTargetedPtt(string driverId)
    {
        _targetDriverId = driverId;
        _isTargetedPtt = true;
        StartPtt();
    }

    // ═══════════════════════════════════════════
    //  PTT CONTROL
    // ═══════════════════════════════════════════

    public void StartPtt()
    {
        if (_pttActive) return;
        _pttActive = true;

        _btnPtt.BackColor = Color.FromArgb(180, 30, 30);
        _btnPtt.Text = _isTargetedPtt
            ? $"🔴 PTT → {_targetDriverId}"
            : "🔴 TRANSMITTING...";
        _btnPtt.FlatAppearance.BorderColor = Color.Red;
        _lblStatus.Text = _isTargetedPtt
            ? $"🔴 TARGETED → {_targetDriverId}"
            : _selectedDriverIds.Count == 0
                ? "🔴 BROADCASTING TO ALL..."
                : $"🔴 TO {_selectedDriverIds.Count} DRIVER(S)...";
        _lblStatus.ForeColor = Color.Red;

        try
        {
            // Determine targets
            IEnumerable<string>? targets = null;
            if (_isTargetedPtt && !string.IsNullOrEmpty(_targetDriverId))
                targets = new[] { _targetDriverId };
            else if (_selectedDriverIds.Count > 0)
                targets = _selectedDriverIds;

            _engine.StartPtt(targets);

            var targetLabel = _isTargetedPtt
                ? $"Driver {_targetDriverId}"
                : _selectedDriverIds.Count == 0
                    ? "All Drivers"
                    : _selectedDriverIds.Count == 1
                        ? _knownDrivers.FirstOrDefault(d => _selectedDriverIds.Contains(d.id)).name ?? "Driver"
                        : $"{_selectedDriverIds.Count} drivers";
            AddLogEntry($"→ {targetLabel} (WebRTC)", Color.FromArgb(0, 188, 212));
        }
        catch (Exception ex)
        {
            _pttActive = false;
            _isTargetedPtt = false;
            _targetDriverId = null;
            ResetPttButton();
            OnLog?.Invoke($"❌ Mic error: {ex.Message}", Color.Red);
        }
    }

    public void StopPtt()
    {
        if (!_pttActive) return;
        _pttActive = false;
        _isTargetedPtt = false;
        _targetDriverId = null;

        _engine.StopPtt();
        ResetPttButton();
    }

    // ═══════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════

    private void ResetPttButton()
    {
        if (InvokeRequired) { BeginInvoke(ResetPttButton); return; }
        _btnPtt.BackColor = Color.FromArgb(50, 50, 60);
        _btnPtt.Text = "🎙 PUSH TO TALK";
        _btnPtt.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 90);
        UpdatePttLabel();
    }

    private void UpdatePttLabel()
    {
        if (InvokeRequired) { BeginInvoke(UpdatePttLabel); return; }
        _lblStatus.Text = _selectedDriverIds.Count == 0
            ? "Hold SPACE or click — broadcast to all"
            : $"Hold SPACE or click — {_selectedDriverIds.Count} driver(s) selected";
        _lblStatus.ForeColor = Color.Gray;
    }

    private void UpdatePeerCount(int count)
    {
        _lblPeers.Text = $"🔗 {count} peer{(count != 1 ? "s" : "")} connected";
        _lblPeers.ForeColor = count > 0 ? Color.LimeGreen : Color.Gray;
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

    protected override void Dispose(bool disposing)
    {
        if (disposing) _engine.Dispose();
        base.Dispose(disposing);
    }
}
