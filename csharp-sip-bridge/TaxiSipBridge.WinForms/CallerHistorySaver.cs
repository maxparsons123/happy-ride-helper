using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TaxiSipBridge;

/// <summary>
/// Saves caller address history and booking transcript to the backend
/// after a successful booking. Call SaveAsync() after book_taxi succeeds.
/// </summary>
public static class CallerHistorySaver
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private const string EdgeFunctionUrl =
        "https://oerketnvlmptpfvttysy.supabase.co/functions/v1/caller-history-save";

    private const string AnonKey =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im9lcmtldG52bG1wdHBmdnR0eXN5Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3Njg2NTg0OTAsImV4cCI6MjA4NDIzNDQ5MH0.QJPKuVmnP6P3RrzDSSBVbHGrduuDqFt7oOZ0E-cGNqU";

    public static Action<string>? OnLog;
    private static void Log(string msg) => OnLog?.Invoke(msg);

    /// <summary>
    /// Save caller's addresses and transcript after a successful booking.
    /// Fire-and-forget safe — errors are logged but never thrown.
    /// </summary>
    public static async Task SaveAsync(
        string phone,
        string? name,
        string? pickup,
        string? destination,
        string? callId = null)
    {
        try
        {
            var payload = new
            {
                phone,
                name = name ?? "",
                pickup = pickup ?? "",
                destination = destination ?? "",
                call_id = callId ?? ""
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, EdgeFunctionUrl);
            request.Content = content;
            request.Headers.Add("apikey", AnonKey);
            request.Headers.Add("Authorization", $"Bearer {AnonKey}");

            var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                Log($"✅ Caller history saved for {phone} (pickup={pickup}, dest={destination}, call={callId})");
            }
            else
            {
                Log($"⚠️ Caller history save failed: HTTP {(int)response.StatusCode} — {body}");
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Caller history save error (non-fatal): {ex.Message}");
        }
    }
}
