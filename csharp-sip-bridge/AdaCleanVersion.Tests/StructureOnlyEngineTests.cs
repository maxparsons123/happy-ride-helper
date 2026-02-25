using System.Net;
using System.Text;
using System.Text.Json;
using AdaCleanVersion.Models;
using AdaCleanVersion.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdaCleanVersion.Tests;

/// <summary>
/// Unit tests for StructureOnlyEngine.
/// Uses a fake HttpMessageHandler to return canned OpenAI responses,
/// so we test the deterministic logic: slot mapping, address parsing,
/// time parsing, merge behaviour, and warning generation.
/// </summary>
public class StructureOnlyEngineTests
{
    private static readonly ILogger Logger = NullLogger.Instance;
    private const string FakeApiKey = "sk-test-fake";

    // ───────────────────────────────────────────────
    // Helpers
    // ───────────────────────────────────────────────

    /// <summary>Build an engine backed by a handler that returns a canned NormalizedBooking JSON.</summary>
    private static StructureOnlyEngine BuildEngine(object normalizedResponse)
    {
        var json = JsonSerializer.Serialize(normalizedResponse);
        var openAiBody = WrapInOpenAiResponse(json);
        var handler = new FakeHandler(openAiBody, HttpStatusCode.OK);
        var client = new HttpClient(handler);
        return new StructureOnlyEngine(FakeApiKey, Logger, client);
    }

    private static StructureOnlyEngine BuildFailingEngine(HttpStatusCode status = HttpStatusCode.InternalServerError)
    {
        var handler = new FakeHandler("{\\\"error\\\":\\\"fail\\\"}", status);
        var client = new HttpClient(handler);
        return new StructureOnlyEngine(FakeApiKey, Logger, client);
    }

    private static string WrapInOpenAiResponse(string content) =>
        JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new { content }
                }
            }
        });

    private static ExtractionRequest MakeRequest(
        string name = "John",
        string pickup = "52A David Road",
        string dest = "Manchester Airport",
        string pax = "3",
        string time = "5pm tomorrow") =>
        new()
        {
            Slots = new RawSlots
            {
                Name = name,
                Pickup = pickup,
                Destination = dest,
                Passengers = pax,
                PickupTime = time
            }
        };

    private static StructuredBooking MakeExistingBooking() =>
        new()
        {
            CallerName = "John",
            Pickup = new StructuredAddress
            {
                HouseNumber = "52A",
                StreetName = "David Road",
                City = "Leeds"
            },
            Destination = new StructuredAddress
            {
                StreetName = "Manchester Airport"
            },
            Passengers = 3,
            PickupTime = "2026-02-25 14:00",
            PickupDateTime = new DateTime(2026, 2, 25, 14, 0, 0)
        };

    // ═══════════════════════════════════════════════
    // 1. NEW BOOKING NORMALIZATION
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task ExtractAsync_FullBooking_ReturnsStructuredBooking()
    {
        var engine = BuildEngine(new
        {
            pickup_location = "52A David Road, Leeds",
            dropoff_location = "Manchester Airport",
            pickup_time = "2026-02-26 17:00",
            number_of_passengers = 3,
            luggage = (string?)null,
            special_requests = (string?)null
        });

        var result = await engine.ExtractAsync(MakeRequest());

        Assert.True(result.Success);
        Assert.NotNull(result.Booking);
        Assert.Equal("John", result.Booking!.CallerName);
        Assert.Equal("52A", result.Booking.Pickup.HouseNumber);
        Assert.Equal("David Road", result.Booking.Pickup.StreetName);
        Assert.Equal("Leeds", result.Booking.Pickup.City);
        Assert.Equal("Manchester Airport", result.Booking.Destination.StreetName);
        Assert.Equal(3, result.Booking.Passengers);
        Assert.Equal("2026-02-26 17:00", result.Booking.PickupTime);
        Assert.NotNull(result.Booking.PickupDateTime);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task ExtractAsync_AsapTime_PickupDateTimeIsNull()
    {
        var engine = BuildEngine(new
        {
            pickup_location = "10 High Street",
            dropoff_location = "Train Station",
            pickup_time = "ASAP",
            number_of_passengers = 1,
            luggage = (string?)null,
            special_requests = (string?)null
        });

        var result = await engine.ExtractAsync(MakeRequest(time: "now please"));

        Assert.True(result.Success);
        Assert.Equal("ASAP", result.Booking!.PickupTime);
        Assert.Null(result.Booking.PickupDateTime);
        Assert.True(result.Booking.IsAsap);
    }

    [Fact]
    public async Task ExtractAsync_MissingPickup_AddsWarning()
    {
        var engine = BuildEngine(new
        {
            pickup_location = (string?)null,
            dropoff_location = "Manchester",
            pickup_time = "ASAP",
            number_of_passengers = 2,
            luggage = (string?)null,
            special_requests = (string?)null
        });

        var result = await engine.ExtractAsync(MakeRequest());

        Assert.True(result.Success);
        Assert.Contains(result.Warnings, w => w.Contains("Pickup"));
    }

    [Fact]
    public async Task ExtractAsync_MissingDestination_AddsWarning()
    {
        var engine = BuildEngine(new
        {
            pickup_location = "10 High Street",
            dropoff_location = (string?)null,
            pickup_time = "ASAP",
            number_of_passengers = 1,
            luggage = (string?)null,
            special_requests = (string?)null
        });

        var result = await engine.ExtractAsync(MakeRequest());

        Assert.True(result.Success);
        Assert.Contains(result.Warnings, w => w.Contains("Destination"));
    }

    [Fact]
    public async Task ExtractAsync_NullPassengers_DefaultsTo1()
    {
        var engine = BuildEngine(new
        {
            pickup_location = "A",
            dropoff_location = "B",
            pickup_time = "ASAP",
            number_of_passengers = (int?)null,
            luggage = (string?)null,
            special_requests = (string?)null
        });

        var result = await engine.ExtractAsync(MakeRequest());

        Assert.Equal(1, result.Booking!.Passengers);
    }

    [Fact]
    public async Task ExtractAsync_ApiError_ReturnsFailure()
    {
        var engine = BuildFailingEngine();

        var result = await engine.ExtractAsync(MakeRequest());

        Assert.False(result.Success);
        Assert.Equal("Normalization returned no result", result.Error);
    }

    [Fact]
    public async Task ExtractAsync_AddressWithPostcode_ParsedCorrectly()
    {
        var engine = BuildEngine(new
        {
            pickup_location = "52A David Road, Holbeck, Leeds LS11 5RG",
            dropoff_location = "Manchester Airport",
            pickup_time = "ASAP",
            number_of_passengers = 1,
            luggage = (string?)null,
            special_requests = (string?)null
        });

        var result = await engine.ExtractAsync(MakeRequest());

        Assert.Equal("52A", result.Booking!.Pickup.HouseNumber);
        Assert.Equal("David Road", result.Booking.Pickup.StreetName);
        Assert.Equal("Holbeck", result.Booking.Pickup.Area);
        Assert.Equal("Leeds", result.Booking.Pickup.City);
        Assert.Equal("LS11 5RG", result.Booking.Pickup.Postcode);
    }

    // ═══════════════════════════════════════════════
    // 2. UPDATE WITH PARTIAL CHANGED FIELDS
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task ExtractUpdateAsync_OnlyTimeChanged_PreservesOtherFields()
    {
        // AI returns only the changed field; others are null
        var engine = BuildEngine(new
        {
            pickup_location = (string?)null,
            dropoff_location = (string?)null,
            pickup_time = "2026-02-25 17:00",
            number_of_passengers = (int?)null,
            luggage = (string?)null,
            special_requests = (string?)null
        });

        var existing = MakeExistingBooking();
        var changed = new HashSet<string> { "pickup_time" };

        var result = await engine.ExtractUpdateAsync(
            MakeRequest(time: "5pm"), existing, changed);

        Assert.True(result.Success);
        // Time updated
        Assert.Equal("2026-02-25 17:00", result.Booking!.PickupTime);
        Assert.NotNull(result.Booking.PickupDateTime);
        // Pickup preserved from existing
        Assert.Equal("52A", result.Booking.Pickup.HouseNumber);
        Assert.Equal("David Road", result.Booking.Pickup.StreetName);
        Assert.Equal("Leeds", result.Booking.Pickup.City);
        // Destination preserved
        Assert.Equal("Manchester Airport", result.Booking.Destination.StreetName);
        // Passengers preserved
        Assert.Equal(3, result.Booking.Passengers);
    }

    [Fact]
    public async Task ExtractUpdateAsync_PickupAndPassengersChanged_PreservesRest()
    {
        var engine = BuildEngine(new
        {
            pickup_location = "99 New Street, Bradford",
            dropoff_location = (string?)null,
            pickup_time = (string?)null,
            number_of_passengers = 5,
            luggage = (string?)null,
            special_requests = (string?)null
        });

        var existing = MakeExistingBooking();
        var changed = new HashSet<string> { "pickup", "passengers" };

        var result = await engine.ExtractUpdateAsync(
            MakeRequest(pickup: "99 New Street, Bradford", pax: "5"),
            existing, changed);

        Assert.True(result.Success);
        // Changed fields updated
        Assert.Equal("99", result.Booking!.Pickup.HouseNumber);
        Assert.Equal("New Street", result.Booking.Pickup.StreetName);
        Assert.Equal("Bradford", result.Booking.Pickup.City);
        Assert.Equal(5, result.Booking.Passengers);
        // Unchanged fields preserved
        Assert.Equal("Manchester Airport", result.Booking.Destination.StreetName);
        Assert.Equal("2026-02-25 14:00", result.Booking.PickupTime);
    }

    [Fact]
    public async Task ExtractUpdateAsync_ApiFailure_ReturnsError()
    {
        var engine = BuildFailingEngine();

        var existing = MakeExistingBooking();
        var changed = new HashSet<string> { "pickup_time" };

        var result = await engine.ExtractUpdateAsync(MakeRequest(), existing, changed);

        Assert.False(result.Success);
        Assert.Equal("Update normalization returned no result", result.Error);
    }

    // ═══════════════════════════════════════════════
    // 3. MERGE LOGIC — PRESERVING UNCHANGED FIELDS
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task MergeUpdate_AllNullFromAi_PreservesEntireExisting()
    {
        // AI returns all nulls (nothing changed)
        var engine = BuildEngine(new
        {
            pickup_location = (string?)null,
            dropoff_location = (string?)null,
            pickup_time = (string?)null,
            number_of_passengers = (int?)null,
            luggage = (string?)null,
            special_requests = (string?)null
        });

        var existing = MakeExistingBooking();
        var changed = new HashSet<string> { "pickup_time" }; // intent was to change, but AI returned null

        var result = await engine.ExtractUpdateAsync(MakeRequest(), existing, changed);

        Assert.True(result.Success);
        Assert.Equal("52A", result.Booking!.Pickup.HouseNumber);
        Assert.Equal("David Road", result.Booking.Pickup.StreetName);
        Assert.Equal("Manchester Airport", result.Booking.Destination.StreetName);
        Assert.Equal(3, result.Booking.Passengers);
        Assert.Equal("2026-02-25 14:00", result.Booking.PickupTime);
        Assert.Equal(existing.PickupDateTime, result.Booking.PickupDateTime);
    }

    [Fact]
    public async Task MergeUpdate_DestinationChanged_PickupAndTimePreserved()
    {
        var engine = BuildEngine(new
        {
            pickup_location = (string?)null,
            dropoff_location = "Liverpool Lime Street Station",
            pickup_time = (string?)null,
            number_of_passengers = (int?)null,
            luggage = (string?)null,
            special_requests = (string?)null
        });

        var existing = MakeExistingBooking();
        var changed = new HashSet<string> { "destination" };

        var result = await engine.ExtractUpdateAsync(
            MakeRequest(dest: "Liverpool Lime Street"), existing, changed);

        Assert.True(result.Success);
        // Destination updated
        Assert.Equal("Liverpool Lime Street Station", result.Booking!.Destination.StreetName);
        // Everything else preserved
        Assert.Equal("52A", result.Booking.Pickup.HouseNumber);
        Assert.Equal(3, result.Booking.Passengers);
        Assert.Equal("2026-02-25 14:00", result.Booking.PickupTime);
        Assert.Equal(existing.PickupDateTime, result.Booking.PickupDateTime);
    }

    [Fact]
    public async Task MergeUpdate_TimeChangedToAsap_PickupDateTimeBecomesNull()
    {
        var engine = BuildEngine(new
        {
            pickup_location = (string?)null,
            dropoff_location = (string?)null,
            pickup_time = "ASAP",
            number_of_passengers = (int?)null,
            luggage = (string?)null,
            special_requests = (string?)null
        });

        var existing = MakeExistingBooking();
        var changed = new HashSet<string> { "pickup_time" };

        var result = await engine.ExtractUpdateAsync(MakeRequest(time: "now"), existing, changed);

        Assert.True(result.Success);
        Assert.Equal("ASAP", result.Booking!.PickupTime);
        Assert.Null(result.Booking.PickupDateTime);
        Assert.True(result.Booking.IsAsap);
        // Other fields preserved
        Assert.Equal("52A", result.Booking.Pickup.HouseNumber);
        Assert.Equal(3, result.Booking.Passengers);
    }

    [Fact]
    public async Task MergeUpdate_CallerNameAlwaysFromRequest()
    {
        var engine = BuildEngine(new
        {
            pickup_location = (string?)null,
            dropoff_location = (string?)null,
            pickup_time = (string?)null,
            number_of_passengers = (int?)null,
            luggage = (string?)null,
            special_requests = (string?)null
        });

        var existing = MakeExistingBooking(); // CallerName = "John"
        var changed = new HashSet<string>();

        var result = await engine.ExtractUpdateAsync(
            MakeRequest(name: "Jane"), existing, changed);

        Assert.True(result.Success);
        Assert.Equal("Jane", result.Booking!.CallerName);
    }

    // ═══════════════════════════════════════════════
    // Fake HttpMessageHandler
    // ═══════════════════════════════════════════════

    private class FakeHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public FakeHandler(string body, HttpStatusCode status)
        {
            _body = body;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
    }
}
