# Ada Taxi — VaxVoIP Edition

Production-ready taxi booking voice bot using VaxVoIP SIP Server SDK + OpenAI Realtime API.

## Prerequisites

- Windows with .NET 8.0+
- VaxVoIP TeleServer SDK (COM component) — [vaxvoip.com](https://www.vaxvoip.com)
- OpenAI API key with Realtime API access

## Configuration

Set environment variables:
```
VAXVOIP_LICENSE=your-license-key
OPENAI_API_KEY=sk-...
GOOGLE_MAPS_API_KEY=AIza...    (optional — falls back to Nominatim)
BSQD_WEBHOOK_URL=...           (optional — dispatch integration)
```

## Architecture

```
Phone → VaxVoIP SIP Server → G.711 A-law → OpenAI Realtime API
                                  ↑
                          address-dispatch edge function
                          (Gemini AI geocoding + fare calc)
```

- **VaxVoIPSipServer**: COM-based SIP server handling registration, calls, and RTP audio
- **OpenAIRealtimeClient**: WebSocket client with G.711 A-law passthrough, response gating, barge-in, no-reply watchdog
- **TaxiBookingOrchestrator**: Bridges SIP ↔ AI, handles tool calls (sync_booking_data, book_taxi, end_call)
- **FareCalculator**: AI-powered address resolution via Supabase edge function + Haversine fallback

## Running

```bash
dotnet run
```

The server listens on SIP port 5060 by default. Point your SIP trunk or softphone at the server IP.
