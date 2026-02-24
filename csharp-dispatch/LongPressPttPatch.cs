// ============================================================
// LONG-PRESS PTT ON DRIVER ROW — Desktop Dispatcher Patch
// Hold a driver row in the DataGridView for 2+ seconds to
// activate targeted PTT to that specific driver.
// ============================================================

// ============================================================
// 1. ADD THESE FIELDS TO YOUR MainForm (or DriverListPanel)
// ============================================================

private System.Windows.Forms.Timer _longPressTimer;
private string _longPressTargetDriverId;
private bool _longPressPttActive;

// ============================================================
// 2. INITIALISE IN YOUR CONSTRUCTOR (after InitializeComponent)
// ============================================================

_longPressTimer = new System.Windows.Forms.Timer();
_longPressTimer.Interval = 2000; // 2 seconds
_longPressTimer.Tick += LongPressTimer_Tick;

// Wire up events on your driver DataGridView
driverGrid.CellMouseDown += DriverGrid_CellMouseDown;
driverGrid.CellMouseUp += DriverGrid_CellMouseUp;
driverGrid.CellMouseLeave += DriverGrid_CellMouseLeave;
driverGrid.MouseLeave += (s, e) => CancelLongPress();

// ============================================================
// 3. EVENT HANDLERS — paste into your form class
// ============================================================

private void DriverGrid_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
{
    if (e.RowIndex < 0) return;

    // Extract driver ID from the row (adjust column index/name to match yours)
    var row = driverGrid.Rows[e.RowIndex];
    var driverId = row.Cells["DriverId"]?.Value?.ToString()
                ?? row.Cells[0]?.Value?.ToString();

    if (string.IsNullOrEmpty(driverId)) return;

    _longPressTargetDriverId = driverId;
    _longPressTimer.Start();

    // Visual feedback — highlight the row
    row.DefaultCellStyle.BackColor = Color.FromArgb(40, 40, 60);
}

private void DriverGrid_CellMouseUp(object sender, DataGridViewCellMouseEventArgs e)
{
    CancelLongPress();
    if (e.RowIndex >= 0)
        driverGrid.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.Empty;
}

private void DriverGrid_CellMouseLeave(object sender, DataGridViewCellEventArgs e)
{
    CancelLongPress();
    if (e.RowIndex >= 0)
        driverGrid.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.Empty;
}

private void CancelLongPress()
{
    _longPressTimer.Stop();

    if (_longPressPttActive)
    {
        // Stop PTT
        _radioPanel.StopPtt();          // calls your existing RadioPanel.StopPtt()
        _longPressPttActive = false;
        UpdateStatusBar("PTT ended");
    }

    _longPressTargetDriverId = null;
}

private void LongPressTimer_Tick(object sender, EventArgs e)
{
    _longPressTimer.Stop();

    if (string.IsNullOrEmpty(_longPressTargetDriverId)) return;

    // Activate targeted PTT to this specific driver
    _longPressPttActive = true;
    _radioPanel.StartTargetedPtt(_longPressTargetDriverId);

    UpdateStatusBar($"🔴 PTT → Driver {_longPressTargetDriverId}");
}

// ============================================================
// 4. ADD THIS METHOD TO RadioPanel.cs
//    (alongside your existing StartPtt / StopPtt)
// ============================================================

/*
    /// <summary>
    /// Start PTT targeted at a single driver (publishes to radio/driver/{id})
    /// </summary>
    public void StartTargetedPtt(string driverId)
    {
        _targetDriverId = driverId;        // new field: private string _targetDriverId;
        _isTargetedPtt = true;             // new field: private bool _isTargetedPtt;
        StartPtt();                        // reuse existing capture logic
    }

    // Then in your TransmitAudio method, check:
    private void TransmitAudio(string base64Audio, string mimeType)
    {
        string topic = _isTargetedPtt && !string.IsNullOrEmpty(_targetDriverId)
            ? $"radio/driver/{_targetDriverId}"    // targeted to one driver
            : "radio/broadcast";                    // broadcast to all

        var payload = new
        {
            dispatcher = true,
            name = "Dispatch",
            audio = base64Audio,
            mime = mimeType,
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            targets = _isTargetedPtt ? new[] { _targetDriverId } : null
        };

        _mqttClient.Publish(topic, JsonSerializer.Serialize(payload));
    }

    public override void StopPtt()
    {
        base.StopPtt();                    // existing stop logic
        _isTargetedPtt = false;
        _targetDriverId = null;
    }
*/

// ============================================================
// 5. OPTIONAL: TOUCH SUPPORT (for touch-screen dispatch PCs)
// ============================================================

/*
    // If using a touch-enabled monitor, WinForms maps touch to mouse events
    // automatically, so the above code works as-is.
    //
    // For more precise control, you can handle WM_POINTERDOWN / WM_POINTERUP
    // via WndProc override — but the mouse events should suffice.
*/

// ============================================================
// 6. OPTIONAL: VISUAL FEEDBACK DURING PTT
// ============================================================

/*
    // In LongPressTimer_Tick, after activating PTT:
    var row = driverGrid.Rows.Cast<DataGridViewRow>()
        .FirstOrDefault(r => r.Cells["DriverId"]?.Value?.ToString() == _longPressTargetDriverId);

    if (row != null)
    {
        row.DefaultCellStyle.BackColor = Color.FromArgb(80, 20, 20);   // dark red
        row.DefaultCellStyle.ForeColor = Color.FromArgb(255, 120, 120); // light red text
    }

    // Reset in CancelLongPress:
    foreach (DataGridViewRow row in driverGrid.Rows)
    {
        row.DefaultCellStyle.BackColor = Color.Empty;
        row.DefaultCellStyle.ForeColor = Color.Empty;
    }
*/
