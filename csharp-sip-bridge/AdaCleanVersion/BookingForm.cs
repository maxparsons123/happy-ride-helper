// Ported from AdaSdkModel v2.8 — adapted for AdaCleanVersion models
using System.Net.Http.Headers;
using System.Text.Json;
using AdaCleanVersion.Config;
using AdaCleanVersion.Models;
using AdaCleanVersion.Services;
using Microsoft.Extensions.Logging;

namespace AdaCleanVersion;

/// <summary>
/// Manual booking dialog — allows operators to take bookings over the phone.
/// Features: caller history, iCabbi customer lookup, address verification via edge function,
/// fare calculation, and dispatch via BSQD + iCabbi.
/// </summary>
public sealed class BookingForm : Form
{
    private TextBox txtCallerName;
    private TextBox txtPhone;
    private ComboBox cmbPickup;
    private ComboBox cmbDropoff;
    private NumericUpDown nudPassengers;
    private ComboBox cmbVehicle;
    private ComboBox cmbPickupTime;
    private TextBox txtNotes;

    private Label lblPickupStatus, lblDropoffStatus;
    private Label lblFare, lblEta;
    private Label lblPickupResolved, lblDropoffResolved;

    private Button btnVerify, btnBook, btnCancel;
    private Label lblStatus;
    private Label lblIcabbiInfo;

    private readonly FareGeocodingService _fareService;
    private readonly IDispatcher _dispatcher;
    private readonly IcabbiBookingService? _icabbiService;
    private readonly ILogger _logger;
    private readonly SupabaseSettings _supabaseSettings;

    private FareResult? _lastFareResult;
    private bool _addressesVerified;
    private string[]? _callerPickupHistory;
    private string[]? _callerDropoffHistory;
    private string? _lastPickup;
    private string? _lastDestination;
    private IcabbiCustomerResult? _icabbiCustomer;

    private System.Windows.Forms.Timer _pickupDebounce;
    private System.Windows.Forms.Timer _dropoffDebounce;
    private CancellationTokenSource? _pickupCts, _dropoffCts;
    private bool _suppressTextChanged;

    public StructuredBooking? CompletedBooking { get; private set; }
    public FareResult? CompletedFare { get; private set; }

    public BookingForm(
        FareGeocodingService fareService,
        IDispatcher dispatcher,
        ILogger logger,
        SupabaseSettings supabaseSettings,
        IcabbiBookingService? icabbiService = null,
        string? callerPhone = null,
        string? callerName = null)
    {
        _fareService = fareService;
        _dispatcher = dispatcher;
        _icabbiService = icabbiService;
        _logger = logger;
        _supabaseSettings = supabaseSettings;

        BuildUi();

        if (!string.IsNullOrEmpty(callerPhone))
        {
            txtPhone.Text = callerPhone;
            _ = LoadCallerDataAsync(callerPhone);
        }
        if (!string.IsNullOrEmpty(callerName))
            txtCallerName.Text = callerName;
    }

    private void BuildUi()
    {
        var bg = Color.FromArgb(30, 30, 30);
        var bgInput = Color.FromArgb(60, 60, 65);
        var fg = Color.FromArgb(220, 220, 220);
        var accent = Color.FromArgb(0, 122, 204);
        var green = Color.FromArgb(40, 167, 69);

        Text = "📋 New Booking";
        Size = new Size(520, 720);
        MinimumSize = new Size(480, 660);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = bg; ForeColor = fg;
        Font = new Font("Segoe UI", 9.5F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        var y = 15;

        AddSectionLabel("👤 Caller Information", 15, ref y);
        y += 5;
        AddLabel("Name:", 15, y + 3);
        txtCallerName = AddTextBox(110, y, 180, bgInput, fg); txtCallerName.PlaceholderText = "Customer name";
        AddLabel("Phone:", 305, y + 3);
        txtPhone = AddTextBox(360, y, 135, bgInput, fg); txtPhone.PlaceholderText = "+44...";
        y += 30;

        // iCabbi customer info line
        lblIcabbiInfo = new Label
        {
            Text = "", Location = new Point(15, y), Size = new Size(480, 18),
            ForeColor = Color.FromArgb(100, 180, 255), Font = new Font("Segoe UI", 8F, FontStyle.Italic)
        };
        Controls.Add(lblIcabbiInfo);
        y += 22;

        AddSectionLabel("🚕 Journey Details", 15, ref y);

        var btnRepeat = new Button
        {
            Text = "🔁 Repeat Last Journey", Location = new Point(300, y - 20), Size = new Size(185, 24),
            BackColor = Color.FromArgb(60, 60, 65), ForeColor = Color.FromArgb(180, 200, 255),
            FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 8F),
            Visible = false, Tag = "btnRepeat"
        };
        btnRepeat.FlatAppearance.BorderSize = 1;
        btnRepeat.FlatAppearance.BorderColor = Color.FromArgb(80, 100, 160);
        btnRepeat.Click += (s, e) => RepeatLastJourney();
        Controls.Add(btnRepeat);
        y += 5;

        AddLabel("Pickup:", 15, y + 3);
        cmbPickup = AddComboBox(110, y, 340, bgInput, fg);
        lblPickupStatus = new Label { Text = "", Location = new Point(455, y + 3), Size = new Size(30, 20), Font = new Font("Segoe UI", 11F) };
        Controls.Add(lblPickupStatus);
        y += 32;
        lblPickupResolved = new Label { Text = "", Location = new Point(110, y), Size = new Size(370, 16), ForeColor = Color.FromArgb(120, 180, 120), Font = new Font("Segoe UI", 7.5F, FontStyle.Italic) };
        Controls.Add(lblPickupResolved);
        y += 20;

        AddLabel("Drop-off:", 15, y + 3);
        cmbDropoff = AddComboBox(110, y, 340, bgInput, fg);
        lblDropoffStatus = new Label { Text = "", Location = new Point(455, y + 3), Size = new Size(30, 20), Font = new Font("Segoe UI", 11F) };
        Controls.Add(lblDropoffStatus);
        y += 32;
        lblDropoffResolved = new Label { Text = "", Location = new Point(110, y), Size = new Size(370, 16), ForeColor = Color.FromArgb(120, 180, 120), Font = new Font("Segoe UI", 7.5F, FontStyle.Italic) };
        Controls.Add(lblDropoffResolved);
        y += 22;

        AddLabel("Passengers:", 15, y + 3);
        nudPassengers = new NumericUpDown { Location = new Point(110, y), Size = new Size(60, 25), Minimum = 1, Maximum = 16, Value = 1, BackColor = bgInput, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle };
        Controls.Add(nudPassengers);
        AddLabel("Vehicle:", 210, y + 3);
        cmbVehicle = AddComboBox(280, y, 170, bgInput, fg);
        cmbVehicle.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbVehicle.Items.AddRange(new object[] { "Saloon", "Estate", "MPV (6-seat)", "Minibus (8-seat)" });
        cmbVehicle.SelectedIndex = 0;
        y += 35;

        AddLabel("Pickup Time:", 15, y + 3);
        cmbPickupTime = AddComboBox(110, y, 130, bgInput, fg);
        cmbPickupTime.Items.AddRange(new object[] { "ASAP", "In 15 mins", "In 30 mins", "In 1 hour" });
        cmbPickupTime.SelectedIndex = 0;
        y += 35;

        AddLabel("Notes:", 15, y + 3);
        txtNotes = new TextBox { Location = new Point(110, y), Size = new Size(380, 50), Multiline = true, BackColor = bgInput, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "Special instructions" };
        Controls.Add(txtNotes);
        y += 60;

        btnVerify = new Button
        {
            Text = "🔍 Verify Addresses & Get Quote", Location = new Point(15, y), Size = new Size(475, 36),
            BackColor = accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold)
        };
        btnVerify.FlatAppearance.BorderSize = 0;
        btnVerify.Click += async (s, e) => await VerifyAndQuoteAsync();
        Controls.Add(btnVerify);
        y += 45;

        var pnlFare = new Panel { Location = new Point(15, y), Size = new Size(475, 60), BackColor = Color.FromArgb(35, 50, 35), BorderStyle = BorderStyle.FixedSingle };
        lblFare = new Label { Text = "Fare: —", Location = new Point(10, 8), Size = new Size(220, 22), ForeColor = Color.LimeGreen, Font = new Font("Segoe UI", 12F, FontStyle.Bold) };
        pnlFare.Controls.Add(lblFare);
        lblEta = new Label { Text = "ETA: —", Location = new Point(10, 32), Size = new Size(220, 20), ForeColor = Color.FromArgb(180, 220, 180), Font = new Font("Segoe UI", 9F) };
        pnlFare.Controls.Add(lblEta);
        Controls.Add(pnlFare);
        y += 70;

        lblStatus = new Label { Text = "", Location = new Point(15, y), Size = new Size(475, 18), ForeColor = Color.Gray, Font = new Font("Segoe UI", 8F, FontStyle.Italic), TextAlign = ContentAlignment.MiddleCenter };
        Controls.Add(lblStatus);
        y += 22;

        btnBook = new Button
        {
            Text = "✅ Confirm & Dispatch", Location = new Point(15, y), Size = new Size(230, 40),
            BackColor = green, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold), Enabled = false
        };
        btnBook.FlatAppearance.BorderSize = 0;
        btnBook.Click += async (s, e) => await ConfirmBookingAsync();
        Controls.Add(btnBook);

        btnCancel = new Button
        {
            Text = "Cancel", Location = new Point(260, y), Size = new Size(230, 40),
            BackColor = Color.FromArgb(80, 80, 85), ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand, Font = new Font("Segoe UI", 10F)
        };
        btnCancel.FlatAppearance.BorderSize = 0;
        btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(btnCancel);

        nudPassengers.ValueChanged += (s, e) =>
        {
            var pax = (int)nudPassengers.Value;
            if (pax <= 4) cmbVehicle.SelectedIndex = 0;
            else if (pax <= 6) cmbVehicle.SelectedIndex = 2;
            else cmbVehicle.SelectedIndex = 3;
        };

        _pickupDebounce = new System.Windows.Forms.Timer { Interval = 350 };
        _pickupDebounce.Tick += async (s, e) => { _pickupDebounce.Stop(); await FetchAutocompleteSuggestionsAsync(cmbPickup); };
        cmbPickup.TextChanged += (s, e) => { if (!_suppressTextChanged) { _pickupDebounce.Stop(); _pickupDebounce.Start(); } };

        _dropoffDebounce = new System.Windows.Forms.Timer { Interval = 350 };
        _dropoffDebounce.Tick += async (s, e) => { _dropoffDebounce.Stop(); await FetchAutocompleteSuggestionsAsync(cmbDropoff); };
        cmbDropoff.TextChanged += (s, e) => { if (!_suppressTextChanged) { _dropoffDebounce.Stop(); _dropoffDebounce.Start(); } };
    }

    // ══════════════════════════════════════
    //  CALLER HISTORY + iCabbi LOOKUP
    // ══════════════════════════════════════

    private async Task LoadCallerDataAsync(string phone)
    {
        // Run Supabase caller lookup and iCabbi customer lookup in parallel
        var callerTask = LoadCallerHistoryAsync(phone);
        var icabbiTask = LoadIcabbiCustomerAsync(phone);
        await Task.WhenAll(callerTask, icabbiTask);
    }

    private async Task LoadCallerHistoryAsync(string phone)
    {
        try
        {
            SetStatus("Loading caller history…");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var normalized = phone.Trim().Replace(" ", "");
            var url = $"{_supabaseSettings.Url}/rest/v1/callers?phone_number=eq.{Uri.EscapeDataString(normalized)}&select=*";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("apikey", _supabaseSettings.AnonKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode) { SetStatus("No caller history found"); return; }
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;
            if (arr.GetArrayLength() == 0) { SetStatus("New caller — no history"); return; }
            var caller = arr[0];

            if (caller.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            {
                var name = nameEl.GetString();
                if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(txtCallerName.Text))
                    txtCallerName.Text = name;
            }

            if (caller.TryGetProperty("pickup_addresses", out var pickups) && pickups.ValueKind == JsonValueKind.Array)
            {
                _callerPickupHistory = pickups.EnumerateArray().Select(a => a.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).Distinct().Take(15).ToArray();
                cmbPickup.Items.Clear();
                foreach (var addr in _callerPickupHistory) cmbPickup.Items.Add(addr);
            }

            if (caller.TryGetProperty("dropoff_addresses", out var dropoffs) && dropoffs.ValueKind == JsonValueKind.Array)
            {
                _callerDropoffHistory = dropoffs.EnumerateArray().Select(a => a.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).Distinct().Take(15).ToArray();
                cmbDropoff.Items.Clear();
                foreach (var addr in _callerDropoffHistory) cmbDropoff.Items.Add(addr);
            }

            var totalBookings = 0;
            if (caller.TryGetProperty("total_bookings", out var tb)) totalBookings = tb.GetInt32();

            var hasLastPickup = caller.TryGetProperty("last_pickup", out var lp) && lp.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(lp.GetString());
            var hasLastDest = caller.TryGetProperty("last_destination", out var ld) && ld.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(ld.GetString());

            if (hasLastPickup && hasLastDest)
            {
                _lastPickup = lp.GetString()!;
                _lastDestination = ld.GetString()!;
                foreach (Control c in Controls)
                {
                    if (c is Button b && b.Tag?.ToString() == "btnRepeat")
                    {
                        b.Visible = true;
                        b.Text = $"🔁 Repeat: {Truncate(_lastPickup, 15)} → {Truncate(_lastDestination, 15)}";
                        break;
                    }
                }
            }
            SetStatus($"Returning caller — {totalBookings} previous booking{(totalBookings != 1 ? "s" : "")}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load caller history");
            SetStatus("Could not load history");
        }
    }

    private async Task LoadIcabbiCustomerAsync(string phone)
    {
        if (_icabbiService == null) return;

        try
        {
            _icabbiCustomer = await _icabbiService.GetCustomerByPhoneAsync(phone);
            if (_icabbiCustomer != null)
            {
                SafeInvoke(() =>
                {
                    lblIcabbiInfo.Text = $"🚕 iCabbi: {_icabbiCustomer.Name ?? "Unknown"} (ID: {_icabbiCustomer.CustomerId}, {_icabbiCustomer.BookingCount} bookings)";

                    // Auto-fill name from iCabbi if not already set
                    if (string.IsNullOrEmpty(txtCallerName.Text) && !string.IsNullOrEmpty(_icabbiCustomer.Name))
                        txtCallerName.Text = _icabbiCustomer.Name;

                    // Add iCabbi default address to pickup suggestions
                    if (!string.IsNullOrEmpty(_icabbiCustomer.Address))
                    {
                        if (!cmbPickup.Items.Contains(_icabbiCustomer.Address))
                            cmbPickup.Items.Insert(0, _icabbiCustomer.Address);
                    }
                });
            }
            else
            {
                SafeInvoke(() => lblIcabbiInfo.Text = "🚕 iCabbi: No customer record found");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "iCabbi customer lookup failed");
        }
    }

    // ══════════════════════════════════════
    //  ADDRESS VERIFICATION & FARE
    // ══════════════════════════════════════

    private async Task VerifyAndQuoteAsync()
    {
        var pickup = cmbPickup.Text.Trim();
        var dropoff = cmbDropoff.Text.Trim();
        if (string.IsNullOrEmpty(pickup) || string.IsNullOrEmpty(dropoff))
        {
            MessageBox.Show("Please enter both pickup and drop-off addresses.", "Missing Info", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        btnVerify.Enabled = false;
        btnVerify.Text = "⏳ Verifying…";
        SetStatus("Resolving addresses and calculating fare…");
        lblPickupStatus.Text = "⏳"; lblDropoffStatus.Text = "⏳";

        try
        {
            var phone = txtPhone.Text.Trim();

            // Use FareGeocodingService to geocode both addresses
            var pickupGeo = await _fareService.GeocodeAddressAsync(pickup, "pickup", phone);
            var dropoffGeo = await _fareService.GeocodeAddressAsync(dropoff, "destination", phone);

            lblPickupStatus.Text = pickupGeo != null && pickupGeo.Lat != 0 ? "✅" : "⚠️";
            lblPickupResolved.Text = pickupGeo?.Address ?? pickup;
            lblDropoffStatus.Text = dropoffGeo != null && dropoffGeo.Lat != 0 ? "✅" : "⚠️";
            lblDropoffResolved.Text = dropoffGeo?.Address ?? dropoff;

            if (pickupGeo == null || dropoffGeo == null || pickupGeo.Lat == 0 || dropoffGeo.Lat == 0)
            {
                SetStatus("⚠️ Could not verify one or both addresses");
                btnVerify.Enabled = true;
                btnVerify.Text = "🔍 Verify Addresses & Get Quote";
                return;
            }

            if (pickupGeo.IsAmbiguous || dropoffGeo.IsAmbiguous)
            {
                var msg = "Some addresses need clarification:\n\n";
                if (pickupGeo.IsAmbiguous && pickupGeo.Alternatives?.Count > 0)
                    msg += $"Pickup alternatives:\n  • {string.Join("\n  • ", pickupGeo.Alternatives)}\n\n";
                if (dropoffGeo.IsAmbiguous && dropoffGeo.Alternatives?.Count > 0)
                    msg += $"Drop-off alternatives:\n  • {string.Join("\n  • ", dropoffGeo.Alternatives)}\n";
                MessageBox.Show(msg, "Address Clarification", MessageBoxButtons.OK, MessageBoxIcon.Information);
                SetStatus("Please refine addresses and re-verify");
                btnVerify.Enabled = true;
                btnVerify.Text = "🔍 Verify Addresses & Get Quote";
                return;
            }

            // Calculate fare using haversine distance
            var distMiles = HaversineDistanceMiles(pickupGeo.Lat, pickupGeo.Lon, dropoffGeo.Lat, dropoffGeo.Lon);
            var fareAmount = CalculateLocalFare(distMiles);
            var fareStr = $"£{fareAmount:F2}";

            _lastFareResult = new FareResult
            {
                Pickup = pickupGeo,
                Destination = dropoffGeo,
                Fare = fareStr,
                FareSpoken = $"{(int)fareAmount} pounds {(int)((fareAmount % 1) * 100)} pence",
                DistanceMiles = distMiles,
                DriverEta = "10 minutes",
                DriverEtaMinutes = 10,
                BusyLevel = "normal",
                BusyMessage = "We should be able to get you a taxi shortly."
            };

            // Try iCabbi fare quote if available
            if (_icabbiService != null)
            {
                try
                {
                    var icabbiFare = await _icabbiService.GetFareQuoteAsync(_lastFareResult, (int)nudPassengers.Value);
                    if (icabbiFare != null)
                    {
                        fareStr = icabbiFare.FareFormatted;
                        _lastFareResult = _lastFareResult with { Fare = fareStr };
                        _logger.LogInformation("[BookingForm] iCabbi fare override: {Fare}", fareStr);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[BookingForm] iCabbi fare quote failed, using local fare");
                }
            }

            lblFare.Text = $"Fare: {_lastFareResult.Fare}";
            lblEta.Text = $"ETA: {_lastFareResult.DriverEta} | {_lastFareResult.DistanceMiles:F1} miles";

            _addressesVerified = true;
            btnBook.Enabled = true;
            SetStatus("✅ Addresses verified — ready to dispatch");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Address verification failed");
            lblPickupStatus.Text = "❌"; lblDropoffStatus.Text = "❌";
            SetStatus($"Verification failed: {ex.Message}");
        }
        finally
        {
            btnVerify.Enabled = true;
            btnVerify.Text = "🔍 Verify Addresses & Get Quote";
        }
    }

    // ══════════════════════════════════════
    //  CONFIRM & DISPATCH
    // ══════════════════════════════════════

    private async Task ConfirmBookingAsync()
    {
        if (!_addressesVerified || _lastFareResult == null)
        {
            MessageBox.Show("Please verify addresses first.", "Not Verified", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        btnBook.Enabled = false;
        btnBook.Text = "⏳ Dispatching…";
        SetStatus("Dispatching booking…");

        try
        {
            var booking = new StructuredBooking
            {
                CallerName = txtCallerName.Text.Trim(),
                Pickup = new StructuredAddress
                {
                    HouseNumber = _lastFareResult.Pickup.StreetNumber,
                    StreetName = _lastFareResult.Pickup.StreetName,
                    City = _lastFareResult.Pickup.City,
                    Postcode = _lastFareResult.Pickup.PostalCode
                },
                Destination = new StructuredAddress
                {
                    HouseNumber = _lastFareResult.Destination.StreetNumber,
                    StreetName = _lastFareResult.Destination.StreetName,
                    City = _lastFareResult.Destination.City,
                    Postcode = _lastFareResult.Destination.PostalCode
                },
                Passengers = (int)nudPassengers.Value,
                PickupTime = cmbPickupTime.Text == "ASAP" ? "ASAP" : cmbPickupTime.Text
            };

            var phone = txtPhone.Text.Trim();

            // Try iCabbi dispatch first if available
            if (_icabbiService != null)
            {
                try
                {
                    var icabbiResult = await _icabbiService.CreateAndDispatchAsync(booking, _lastFareResult, phone);
                    if (icabbiResult.Success)
                    {
                        _logger.LogInformation("[BookingForm] ✅ iCabbi dispatch: Journey={JourneyId}, TripId={TripId}",
                            icabbiResult.JourneyId, icabbiResult.TripId);
                        SetStatus($"✅ iCabbi dispatched! Journey: {icabbiResult.JourneyId}");

                        CompletedBooking = booking;
                        CompletedFare = _lastFareResult;
                        await Task.Delay(1500);
                        DialogResult = DialogResult.OK;
                        Close();
                        return;
                    }
                    else
                    {
                        _logger.LogWarning("[BookingForm] iCabbi dispatch failed: {Msg}, falling back to BSQD", icabbiResult.Message);
                        SetStatus($"iCabbi failed ({icabbiResult.Message}), trying BSQD…");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[BookingForm] iCabbi dispatch error, falling back to BSQD");
                }
            }

            // Fallback to BSQD dispatch
            var dispatched = await _dispatcher.DispatchAsync(booking, _lastFareResult, phone);

            if (dispatched)
            {
                _ = _dispatcher.SendWhatsAppAsync(phone);
                CompletedBooking = booking;
                CompletedFare = _lastFareResult;
                SetStatus("✅ Booking dispatched!");
                await Task.Delay(1500);
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                SetStatus("❌ Dispatch failed — check logs");
                btnBook.Enabled = true;
                btnBook.Text = "✅ Confirm & Dispatch";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Booking dispatch failed");
            SetStatus($"Error: {ex.Message}");
            btnBook.Enabled = true;
            btnBook.Text = "✅ Confirm & Dispatch";
        }
    }

    private void RepeatLastJourney()
    {
        if (string.IsNullOrEmpty(_lastPickup) || string.IsNullOrEmpty(_lastDestination)) return;
        _suppressTextChanged = true;
        cmbPickup.Text = _lastPickup;
        cmbDropoff.Text = _lastDestination;
        _suppressTextChanged = false;
        SetStatus("🔁 Last journey loaded — click Verify to get quote");
    }

    // ══════════════════════════════════════
    //  ADDRESS AUTOCOMPLETE
    // ══════════════════════════════════════

    private async Task FetchAutocompleteSuggestionsAsync(ComboBox cmb)
    {
        var input = cmb.Text.Trim();
        if (input.Length < 3) return;
        var isPickup = cmb == cmbPickup;
        if (isPickup) { _pickupCts?.Cancel(); _pickupCts = new CancellationTokenSource(); }
        else { _dropoffCts?.Cancel(); _dropoffCts = new CancellationTokenSource(); }
        var ct = isPickup ? _pickupCts!.Token : _dropoffCts!.Token;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var payload = JsonSerializer.Serialize(new { input, phone = txtPhone.Text.Trim() });
            var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_supabaseSettings.Url}/functions/v1/address-autocomplete") { Content = content };
            request.Headers.Add("apikey", _supabaseSettings.AnonKey);
            request.Headers.Add("Authorization", $"Bearer {_supabaseSettings.AnonKey}");

            var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode || ct.IsCancellationRequested) return;
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("predictions", out var predictions)) return;
            var suggestions = predictions.EnumerateArray().Select(p => p.GetProperty("description").GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (ct.IsCancellationRequested || suggestions.Count == 0) return;

            var history = isPickup ? _callerPickupHistory : _callerDropoffHistory;
            var merged = new List<string>();
            if (history != null)
                merged.AddRange(history.Where(h => h.Contains(input, StringComparison.OrdinalIgnoreCase)));
            foreach (var s in suggestions)
                if (!merged.Contains(s, StringComparer.OrdinalIgnoreCase)) merged.Add(s);

            _suppressTextChanged = true;
            var cursorPos = cmb.SelectionStart;
            var currentText = cmb.Text;
            cmb.Items.Clear();
            foreach (var item in merged.Take(8)) cmb.Items.Add(item);
            cmb.Text = currentText;
            cmb.SelectionStart = cursorPos;
            cmb.SelectionLength = 0;
            _suppressTextChanged = false;
            if (merged.Count > 0 && cmb.Focused) cmb.DroppedDown = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "Autocomplete request failed"); }
    }

    // ══════════════════════════════════════
    //  HELPERS
    // ══════════════════════════════════════

    private void SafeInvoke(Action action)
    {
        if (InvokeRequired) Invoke(action);
        else action();
    }

    private void SetStatus(string text)
    {
        if (InvokeRequired) Invoke(() => lblStatus.Text = text);
        else lblStatus.Text = text;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static double HaversineDistanceMiles(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 3958.8; // Earth radius in miles
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static decimal CalculateLocalFare(double distMiles)
    {
        // Basic fare: £3.50 base + £1.80/mile
        return Math.Max(3.50m, 3.50m + (decimal)(distMiles * 1.80));
    }

    private Label AddLabel(string text, int x, int y)
    {
        var lbl = new Label { Text = text, Location = new Point(x, y), AutoSize = true, ForeColor = Color.FromArgb(180, 180, 180) };
        Controls.Add(lbl);
        return lbl;
    }

    private void AddSectionLabel(string text, int x, ref int y)
    {
        var lbl = new Label { Text = text, Location = new Point(x, y), AutoSize = true, ForeColor = Color.FromArgb(0, 150, 220), Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
        Controls.Add(lbl);
        y += 22;
    }

    private TextBox AddTextBox(int x, int y, int w, Color bg, Color fg)
    {
        var txt = new TextBox { Location = new Point(x, y), Size = new Size(w, 25), BackColor = bg, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle };
        Controls.Add(txt);
        return txt;
    }

    private ComboBox AddComboBox(int x, int y, int w, Color bg, Color fg)
    {
        var cmb = new ComboBox { Location = new Point(x, y), Size = new Size(w, 25), BackColor = bg, ForeColor = fg, FlatStyle = FlatStyle.Flat, DropDownStyle = ComboBoxStyle.DropDown };
        Controls.Add(cmb);
        return cmb;
    }
}
