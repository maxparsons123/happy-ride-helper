# Radio PTT Integration Guide

## Files
- `RadioPanel.cs` — Drop-in WinForms panel with PTT, driver selection, audio capture/playback

## NuGet Dependency
```
Install-Package NAudio
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
Option A — Add a tab to the right panel:
```csharp
// In the right-side panel where _driverList lives:
var rightTabs = new TabControl { Dock = DockStyle.Fill };
var tabDrivers = new TabPage("🚕 Drivers") { BackColor = Color.FromArgb(28, 28, 32) };
tabDrivers.Controls.Add(_driverList);
var tabRadio = new TabPage("📻 Radio") { BackColor = Color.FromArgb(28, 28, 32) };
tabRadio.Controls.Add(_radioPanel);
rightTabs.TabPages.AddRange(new[] { tabDrivers, tabRadio });
// Replace _driverList with rightTabs in splitTop.Panel2
splitTop.Panel2.Controls.Add(rightTabs);
```

Option B — Split the right panel vertically:
```csharp
var rightSplit = new SplitContainer
{
    Dock = DockStyle.Fill,
    Orientation = Orientation.Horizontal,
    SplitterDistance = 400
};
rightSplit.Panel1.Controls.Add(_driverList);
rightSplit.Panel2.Controls.Add(_radioPanel);
splitTop.Panel2.Controls.Add(rightSplit);
```

### 4. MQTT subscriptions (in ConnectAsync, after existing subscriptions)
```csharp
// Subscribe to driver radio channel
await _mqtt.SubscribeAsync("radio/channel");
```

### 5. MQTT message handler (in your MQTT OnMessage handler)
```csharp
if (topic == "radio/channel")
{
    _radioPanel.HandleIncomingRadio(message);
    return;
}
```

### 6. Update driver list (in RefreshUI or wherever you update driver lists)
```csharp
_radioPanel.UpdateDriverList(
    drivers.Select(d => (d.Id, d.Name, d.Status.ToString()))
);
```

### 7. Keyboard shortcut (override ProcessCmdKey in MainForm)
```csharp
protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
{
    if (keyData == Keys.Space)
    {
        _radioPanel.HandleKeyDown(Keys.Space);
        return true; // suppress space from other controls
    }
    return base.ProcessCmdKey(ref msg, keyData);
}

// Also handle KeyUp for space release — add to MainForm constructor:
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
| `radio/broadcast` | Dispatch → All drivers | Broadcast audio (optional `targets` array) |
| `radio/driver/{id}` | Dispatch → One driver | Targeted audio |
| `radio/channel` | Driver → Dispatch | Driver transmission |

## Audio Format
- **Desktop TX**: PCM 16kHz mono wrapped in WAV header, base64 encoded
- **Web RX**: The web driver app can decode WAV via Web Audio API
- **Web TX**: Opus/WebM base64 — desktop uses MediaFoundationReader to decode
