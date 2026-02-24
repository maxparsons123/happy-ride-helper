// ============================================================
// LONG-PRESS PTT ON DRIVER MAP ICON — Desktop Dispatcher Patch
// Hold a driver marker on the GMap.NET map for 2+ seconds to
// activate targeted PTT to that specific driver.
// ============================================================

// ============================================================
// 1. ADD THESE FIELDS TO YOUR MapPanel / MainForm
// ============================================================

private System.Windows.Forms.Timer _mapLongPressTimer;
private string _mapLongPressDriverId;
private bool _mapLongPressPttActive;
private Point _mapLongPressStartPoint;

// ============================================================
// 2. INITIALISE IN CONSTRUCTOR (after InitializeComponent)
// ============================================================

_mapLongPressTimer = new System.Windows.Forms.Timer();
_mapLongPressTimer.Interval = 2000; // 2 seconds
_mapLongPressTimer.Tick += MapLongPressTimer_Tick;

// Wire GMap marker events
gMapControl.OnMarkerEnter += GMap_OnMarkerEnter;
gMapControl.OnMarkerLeave += GMap_OnMarkerLeave;
gMapControl.MouseDown += GMap_MouseDown;
gMapControl.MouseUp += GMap_MouseUp;
gMapControl.MouseMove += GMap_MouseMove;

// ============================================================
// 3. TRACK WHICH DRIVER MARKER THE MOUSE IS OVER
// ============================================================

private string _hoveredDriverId = null;

private void GMap_OnMarkerEnter(GMapMarker marker)
{
    // Your driver markers should store the driver ID in the Tag property
    // e.g. marker.Tag = driverId;
    if (marker.Tag is string driverId)
    {
        _hoveredDriverId = driverId;
        // Optional: change cursor
        gMapControl.Cursor = Cursors.Hand;
    }
}

private void GMap_OnMarkerLeave(GMapMarker marker)
{
    _hoveredDriverId = null;
    gMapControl.Cursor = Cursors.Default;
    CancelMapLongPress();
}

// ============================================================
// 4. MOUSE DOWN / UP / MOVE HANDLERS
// ============================================================

private void GMap_MouseDown(object sender, MouseEventArgs e)
{
    if (e.Button != MouseButtons.Left) return;
    if (string.IsNullOrEmpty(_hoveredDriverId)) return;

    _mapLongPressDriverId = _hoveredDriverId;
    _mapLongPressStartPoint = e.Location;
    _mapLongPressTimer.Start();
}

private void GMap_MouseUp(object sender, MouseEventArgs e)
{
    CancelMapLongPress();
}

private void GMap_MouseMove(object sender, MouseEventArgs e)
{
    // Cancel if mouse moves more than 10px (user is dragging the map)
    if (_mapLongPressTimer.Enabled)
    {
        var dx = Math.Abs(e.X - _mapLongPressStartPoint.X);
        var dy = Math.Abs(e.Y - _mapLongPressStartPoint.Y);
        if (dx > 10 || dy > 10)
        {
            CancelMapLongPress();
        }
    }
}

// ============================================================
// 5. LONG-PRESS ACTIVATION & CANCEL
// ============================================================

private void MapLongPressTimer_Tick(object sender, EventArgs e)
{
    _mapLongPressTimer.Stop();

    if (string.IsNullOrEmpty(_mapLongPressDriverId)) return;

    _mapLongPressPttActive = true;
    _radioPanel.StartTargetedPtt(_mapLongPressDriverId);

    UpdateStatusBar($"🔴 PTT → Driver {_mapLongPressDriverId} (map)");

    // Optional: flash the marker
    // HighlightDriverMarker(_mapLongPressDriverId, true);
}

private void CancelMapLongPress()
{
    _mapLongPressTimer.Stop();

    if (_mapLongPressPttActive)
    {
        _radioPanel.StopPtt();
        _mapLongPressPttActive = false;
        UpdateStatusBar("PTT ended");
        // HighlightDriverMarker(_mapLongPressDriverId, false);
    }

    _mapLongPressDriverId = null;
}

// ============================================================
// 6. IMPORTANT: SET marker.Tag WHEN CREATING DRIVER MARKERS
// ============================================================

/*
    When you create/update driver markers on the map, store the ID:

    var marker = new GMarkerGoogle(
        new PointLatLng(lat, lng),
        driverBitmap
    );
    marker.Tag = driverId;                // <-- THIS IS KEY
    marker.ToolTipText = $"Driver {driverId}";
    marker.ToolTipMode = MarkerTooltipMode.OnMouseOver;

    driversOverlay.Markers.Add(marker);
*/

// ============================================================
// 7. OPTIONAL: VISUAL FEEDBACK ON MARKER DURING PTT
// ============================================================

/*
    private void HighlightDriverMarker(string driverId, bool active)
    {
        var overlay = gMapControl.Overlays.FirstOrDefault(o => o.Id == "drivers");
        if (overlay == null) return;

        var marker = overlay.Markers
            .FirstOrDefault(m => m.Tag?.ToString() == driverId);

        if (marker is GMarkerGoogle gMarker)
        {
            if (active)
            {
                // Swap to a red-tinted icon during PTT
                gMarker.Bitmap = Properties.Resources.driver_icon_ptt;
            }
            else
            {
                gMarker.Bitmap = Properties.Resources.driver_icon_normal;
            }
            gMapControl.Refresh();
        }
    }
*/
