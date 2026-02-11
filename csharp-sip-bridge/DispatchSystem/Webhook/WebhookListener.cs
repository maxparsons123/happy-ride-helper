using System.Diagnostics;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DispatchSystem.Data;

namespace DispatchSystem.Webhook;

/// <summary>
/// Lightweight HTTP listener that receives booking webhooks and converts them to dispatch jobs.
/// POST /job  → creates a new pending job
/// GET  /health → 200 OK
/// </summary>
public sealed class WebhookListener : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly int _port;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public event Action<Job>? OnJobReceived;
    public event Action<string>? OnLog;

    public static bool IsRunAsAdmin()
    {
        var wi = WindowsIdentity.GetCurrent();
        var wp = new WindowsPrincipal(wi);
        return wp.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void RestartAsAdmin()
    {
        if (IsRunAsAdmin()) return;
        var exe = Process.GetCurrentProcess().MainModule?.FileName;
        if (exe == null) return;
        Process.Start(new ProcessStartInfo(exe) { Verb = "runas", UseShellExecute = true });
        Environment.Exit(0);
    }

    public bool IsRunning { get; private set; }

    public WebhookListener(int port = 5080)
    {
        _port = port;
        _listener.Prefixes.Add($"http://+:{_port}/");
    }

    public void Start()
    {
        if (IsRunning) return;

        // Ensure URL ACL is registered (requires admin once)
        if (!IsRunAsAdmin())
        {
            OnLog?.Invoke("⚠ Not running as admin — attempting URL ACL reservation...");
            AddUrlAcl();
        }

        _cts = new CancellationTokenSource();
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            OnLog?.Invoke($"❌ Cannot start webhook: {ex.Message}. Try running as Administrator.");
            return;
        }

        IsRunning = true;
        _listenTask = Task.Run(() => ListenLoop(_cts.Token));
        OnLog?.Invoke($"🌐 Webhook listening on port {_port}");
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _cts?.Cancel();
        _listener.Stop();
        IsRunning = false;
        OnLog?.Invoke("🌐 Webhook stopped");
    }

    private void AddUrlAcl()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"http add urlacl url=http://+:{_port}/ user=Everyone",
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            OnLog?.Invoke("✅ URL ACL reservation complete");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"⚠ URL ACL failed: {ex.Message}");
        }
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleRequest(ctx), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex) { OnLog?.Invoke($"❌ Webhook error: {ex.Message}"); }
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var res = ctx.Response;

            // Health check
            if (req.HttpMethod == "GET" && req.Url?.AbsolutePath == "/health")
            {
                Respond(res, 200, "{\"status\":\"ok\"}");
                return;
            }

            // Job webhook
            if (req.HttpMethod == "POST" && req.Url?.AbsolutePath == "/job")
            {
                using var reader = new System.IO.StreamReader(req.InputStream, Encoding.UTF8);
                var body = reader.ReadToEnd();
                var msg = JsonSerializer.Deserialize<BookingWebhookPayload>(body);

                if (msg == null || string.IsNullOrWhiteSpace(msg.pickup))
                {
                    Respond(res, 400, "{\"error\":\"missing pickup\"}");
                    return;
                }

                var job = new Job
                {
                    Pickup = msg.pickup,
                    Dropoff = msg.dropoff ?? "",
                    Passengers = msg.passengers > 0 ? msg.passengers : 1,
                    VehicleRequired = Enum.TryParse<VehicleType>(msg.vehicleType, true, out var vt) ? vt : VehicleType.Saloon,
                    SpecialRequirements = msg.specialRequirements,
                    EstimatedFare = msg.fare,
                    PickupLat = msg.pickupLat,
                    PickupLng = msg.pickupLng,
                    DropoffLat = msg.dropoffLat,
                    DropoffLng = msg.dropoffLng,
                    CallerPhone = msg.phoneNumber,
                    CallerName = msg.name,
                    BookingRef = msg.bookingRef,
                };

                OnJobReceived?.Invoke(job);
                OnLog?.Invoke($"📥 Webhook job: {job.Pickup} → {job.Dropoff} ({msg.phoneNumber})");
                Respond(res, 200, JsonSerializer.Serialize(new { ok = true, jobId = job.Id }));
                return;
            }

            Respond(res, 404, "{\"error\":\"not found\"}");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"❌ Webhook handler error: {ex.Message}");
            try { Respond(ctx.Response, 500, "{\"error\":\"internal\"}"); } catch { }
        }
    }

    private static void Respond(HttpListenerResponse res, int status, string json)
    {
        res.StatusCode = status;
        res.ContentType = "application/json";
        var buf = Encoding.UTF8.GetBytes(json);
        res.ContentLength64 = buf.Length;
        res.OutputStream.Write(buf, 0, buf.Length);
        res.Close();
    }

    public void Dispose()
    {
        Stop();
        _listener.Close();
    }

    // ── Payload DTO ──
    private class BookingWebhookPayload
    {
        public string? pickup { get; set; }
        public string? dropoff { get; set; }
        public string? destination { get; set; }
        public int passengers { get; set; }
        public string? vehicleType { get; set; }
        public string? specialRequirements { get; set; }
        public decimal? fare { get; set; }
        public double pickupLat { get; set; }
        public double pickupLng { get; set; }
        public double dropoffLat { get; set; }
        public double dropoffLng { get; set; }
        public string? phoneNumber { get; set; }
        public string? name { get; set; }
        public string? bookingRef { get; set; }
    }
}
