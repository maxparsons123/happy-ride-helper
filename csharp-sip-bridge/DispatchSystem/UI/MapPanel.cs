using Microsoft.Web.WebView2.WinForms;
using DispatchSystem.Data;

namespace DispatchSystem.UI;

/// <summary>
/// WebView2-based Leaflet map showing drivers and job pickups in real-time.
/// </summary>
public sealed class MapPanel : Panel
{
    private readonly WebView2 _webView;
    private bool _mapReady;

    public MapPanel()
    {
        _webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);
    }

    public async Task InitializeAsync()
    {
        await _webView.EnsureCoreWebView2Async();
        _webView.CoreWebView2.NavigateToString(GetMapHtml());
        _webView.NavigationCompleted += (_, _) => _mapReady = true;
    }

    public async Task UpdateDriverMarker(string driverId, double lat, double lng, string status, string name)
    {
        if (!_mapReady) return;
        var color = status switch
        {
            "Online" => "green",
            "OnJob" => "blue",
            "Break" => "orange",
            _ => "gray"
        };
        var latStr = lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var lngStr = lng.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await _webView.ExecuteScriptAsync(
            $"updateDriver('{Esc(driverId)}', {latStr}, {lngStr}, '{color}', '{Esc(name)} ({status})')");
    }

    public async Task AddJobMarker(string jobId, double lat, double lng, string pickup, DateTime createdAt)
    {
        if (!_mapReady) return;
        var epochMs = new DateTimeOffset(createdAt.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(createdAt, DateTimeKind.Utc) : createdAt).ToUnixTimeMilliseconds();
        var latStr = lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var lngStr = lng.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await _webView.ExecuteScriptAsync(
            $"addJob('{Esc(jobId)}', {latStr}, {lngStr}, '{Esc(pickup)}', {epochMs})");
    }

    public async Task RemoveJobMarker(string jobId)
    {
        if (!_mapReady) return;
        await _webView.ExecuteScriptAsync($"removeJob('{Esc(jobId)}')");
    }

    public async Task DrawAllocationLine(string jobId, double dLat, double dLng, double pLat, double pLng)
    {
        if (!_mapReady) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        await _webView.ExecuteScriptAsync(
            $"drawAllocation('{Esc(jobId)}', {dLat.ToString(inv)}, {dLng.ToString(inv)}, {pLat.ToString(inv)}, {pLng.ToString(inv)})");
    }

    private static string Esc(string s) => s.Replace("'", "\\'").Replace("\n", " ");

    private static string GetMapHtml() => """
    <!DOCTYPE html>
    <html><head>
    <meta charset="utf-8"/>
    <link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css"/>
    <script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
    <style>
        html,body,#map { margin:0; padding:0; width:100%; height:100%; }
    </style>
    </head><body>
    <div id="map"></div>
    <script>
        const map = L.map('map').setView([51.9, 4.48], 12);
        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            attribution: '© OpenStreetMap'
        }).addTo(map);

        const drivers = {};
        const jobs = {};
        const jobTimes = {};
        const lines = {};

        function driverIcon(color) {
            return L.divIcon({
                className: '',
                html: `<div style="background:${color};width:20px;height:20px;border-radius:50%;border:3px solid #fff;box-shadow:0 0 6px rgba(0,0,0,0.6)"></div>`,
                iconSize: [26, 26], iconAnchor: [13, 13]
            });
        }

        function passengerColor(createdMs) {
            const mins = (Date.now() - createdMs) / 60000;
            if (mins < 10) return '#4CAF50';   // green — fresh
            if (mins < 20) return '#FF9800';   // amber — 10-20 min
            return '#F44336';                   // red — 20+ min
        }

        function passengerIcon(color) {
            return L.divIcon({
                className: '',
                html: `<div style="position:relative">
                    <svg width="28" height="40" viewBox="0 0 28 40">
                        <path d="M14 0C6.3 0 0 6.3 0 14c0 10.5 14 26 14 26s14-15.5 14-26C28 6.3 21.7 0 14 0z" fill="${color}" stroke="#fff" stroke-width="1.5"/>
                        <circle cx="14" cy="13" r="6" fill="#fff"/>
                        <circle cx="14" cy="11" r="3" fill="${color}"/>
                        <path d="M9 16c0-2.8 2.2-3 5-3s5 .2 5 3" fill="${color}"/>
                    </svg>
                </div>`,
                iconSize: [28, 40], iconAnchor: [14, 40], popupAnchor: [0, -36]
            });
        }

        function updateDriver(id, lat, lng, color, label) {
            if (drivers[id]) {
                drivers[id].setLatLng([lat, lng]);
                drivers[id].setIcon(driverIcon(color));
                drivers[id].setTooltipContent(label);
            } else {
                drivers[id] = L.marker([lat, lng], { icon: driverIcon(color) })
                    .bindTooltip(label, { permanent: false })
                    .addTo(map);
            }
        }

        function addJob(id, lat, lng, label, createdMs) {
            if (jobs[id]) map.removeLayer(jobs[id]);
            jobTimes[id] = createdMs || Date.now();
            const color = passengerColor(jobTimes[id]);
            const mins = Math.floor((Date.now() - jobTimes[id]) / 60000);
            jobs[id] = L.marker([lat, lng], { icon: passengerIcon(color) })
                .bindPopup(`<b>Job ${id}</b><br>${label}<br>Waiting: ${mins} min`)
                .addTo(map);
        }

        function removeJob(id) {
            if (jobs[id]) { map.removeLayer(jobs[id]); delete jobs[id]; delete jobTimes[id]; }
            if (lines[id]) { map.removeLayer(lines[id]); delete lines[id]; }
        }

        function drawAllocation(id, dLat, dLng, pLat, pLng) {
            if (lines[id]) map.removeLayer(lines[id]);
            lines[id] = L.polyline([[dLat, dLng], [pLat, pLng]], {
                color: '#2196F3', weight: 3, dashArray: '8,8'
            }).addTo(map);
        }

        // Refresh passenger icon colors every 30 seconds
        setInterval(() => {
            for (const id in jobs) {
                if (!jobTimes[id]) continue;
                const color = passengerColor(jobTimes[id]);
                const mins = Math.floor((Date.now() - jobTimes[id]) / 60000);
                const ll = jobs[id].getLatLng();
                jobs[id].setIcon(passengerIcon(color));
            }
        }, 30000);
    </script>
    </body></html>
    """;
}
