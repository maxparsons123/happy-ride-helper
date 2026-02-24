# Radio PTT Integration Guide

## Architecture
The radio system uses **WebRTC peer-to-peer** audio streaming with **MQTT as the signaling channel**.
This replaces the previous base64-over-MQTT approach for dramatically lower latency and better audio quality.

## Files
- `Radio/WebRtcRadioEngine.cs` — WebRTC peer connection management, G.711 μ-law codec, MQTT signaling
- `UI/RadioPanel.cs` — WinForms PTT panel using the WebRTC engine

## NuGet Dependencies
```
Install-Package NAudio
Install-Package SIPSorcery
Install-Package SIPSorceryMedia.Abstractions
```

## Integration into MainForm.cs

### 1. Add field
```csharp
private readonly RadioPanel _radioPanel;
```

### 2. Create and dock (in constructor, after `_driverList`)
```csharp
_radioPanel = new RadioPanel { Dock = DockStyle.Fill };
_radioPanel.OnRadioTransmit += (topic, json) =>
{
    if (_mqtt != null)
        _ = _mqtt.PublishAsync(topic, json);
};
_radioPanel.OnLog += (msg, color) => _logPanel.AppendLog(msg, color);
```

### 3. Add to layout
Option A — Tab-based:
```csharp
var rightTabs = new TabControl { Dock = DockStyle.Fill };
var tabDrivers = new TabPage("🚕 Drivers") { BackColor = Color.FromArgb(28, 28, 32) };
tabDrivers.Controls.Add(_driverList);
var tabRadio = new TabPage("📻 Radio") { BackColor = Color.FromArgb(28, 28, 32) };
tabRadio.Controls.Add(_radioPanel);
rightTabs.TabPages.AddRange(new[] { tabDrivers, tabRadio });
splitTop.Panel2.Controls.Add(rightTabs);
```

### 4. MQTT subscriptions (in ConnectAsync)
```csharp
// Subscribe to WebRTC signaling topics
await _mqtt.SubscribeAsync("radio/webrtc/signal/DISPATCH");
await _mqtt.SubscribeAsync("radio/webrtc/presence");
```

### 5. MQTT message handler
```csharp
if (topic == "radio/webrtc/presence")
{
    _radioPanel.HandlePresence(message);
    return;
}
if (topic == "radio/webrtc/signal/DISPATCH")
{
    _radioPanel.HandleSignaling(message);
    return;
}
```

### 6. Update driver list
```csharp
_radioPanel.UpdateDriverList(
    drivers.Select(d => (d.Id, d.Name, d.Status.ToString()))
);
```

### 7. Keyboard shortcut (ProcessCmdKey in MainForm)
```csharp
protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
{
    if (keyData == Keys.Space)
    {
        _radioPanel.HandleKeyDown(Keys.Space);
        return true;
    }
    return base.ProcessCmdKey(ref msg, keyData);
}

// KeyUp handler in constructor:
KeyPreview = true;
KeyUp += (_, args) =>
{
    if (args.KeyCode == Keys.Space)
        _radioPanel.HandleKeyUp(Keys.Space);
};
```

## MQTT Topic Reference

| Topic | Direction | Purpose |
|-------|-----------|---------|
| `radio/webrtc/signal/{peerId}` | Bidirectional | SDP offers/answers + ICE candidates |
| `radio/webrtc/presence` | Bidirectional | Peer discovery/announcement |

## Audio Format
- **Codec**: G.711 μ-law (PCMU) over RTP via WebRTC
- **Sample Rate**: 8kHz mono
- **Latency**: ~20ms capture frames, real-time RTP delivery
