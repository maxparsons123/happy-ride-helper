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
        await _webView.ExecuteScriptAsync(
            $"updateDriver('{Esc(driverId)}', {lat}, {lng}, '{color}', '{Esc(name)} ({status})')");
    }

    public async Task AddJobMarker(string jobId, double lat, double lng, string pickup)
    {
        if (!_mapReady) return;
        await _webView.ExecuteScriptAsync(
            $"addJob('{Esc(jobId)}', {lat}, {lng}, '{Esc(pickup)}')");
    }

    public async Task RemoveJobMarker(string jobId)
    {
        if (!_mapReady) return;
        await _webView.ExecuteScriptAsync($"removeJob('{Esc(jobId)}')");
    }

    public async Task DrawAllocationLine(string jobId, double dLat, double dLng, double pLat, double pLng)
    {
        if (!_mapReady) return;
        await _webView.ExecuteScriptAsync(
            $"drawAllocation('{Esc(jobId)}', {dLat}, {dLng}, {pLat}, {pLng})");
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
        const lines = {};

        function driverIcon(color) {
            return L.divIcon({
                className: '',
                html: `<div style="background:${color};width:14px;height:14px;border-radius:50%;border:2px solid #fff;box-shadow:0 0 4px rgba(0,0,0,0.5)"></div>`,
                iconSize: [18, 18], iconAnchor: [9, 9]
            });
        }

        const jobIcon = L.icon({
            iconUrl: 'https://raw.githubusercontent.com/pointhi/leaflet-color-markers/master/img/marker-icon-red.png',
            shadowUrl: 'https://unpkg.com/leaflet@1.9.4/dist/images/marker-shadow.png',
            iconSize: [25, 41], iconAnchor: [12, 41], popupAnchor: [1, -34]
        });

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

        function addJob(id, lat, lng, label) {
            if (jobs[id]) map.removeLayer(jobs[id]);
            jobs[id] = L.marker([lat, lng], { icon: jobIcon })
                .bindPopup('<b>Job ' + id + '</b><br>' + label)
                .addTo(map);
        }

        function removeJob(id) {
            if (jobs[id]) { map.removeLayer(jobs[id]); delete jobs[id]; }
            if (lines[id]) { map.removeLayer(lines[id]); delete lines[id]; }
        }

        function drawAllocation(id, dLat, dLng, pLat, pLng) {
            if (lines[id]) map.removeLayer(lines[id]);
            lines[id] = L.polyline([[dLat, dLng], [pLat, pLng]], {
                color: '#2196F3', weight: 3, dashArray: '8,8'
            }).addTo(map);
        }
    </script>
    </body></html>
    """;
}
