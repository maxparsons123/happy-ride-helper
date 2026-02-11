using System.Net.Http.Headers;
using System.Text.Json;
using AdaMain.Config;
using AdaMain.Core;
using AdaMain.Services;
using Microsoft.Extensions.Logging;

namespace AdaMain;

/// <summary>
/// Manual booking dialog — allows operators to take bookings over the phone.
/// Features: caller history, address verification via edge function, fare calculation, dispatch.
/// </summary>
public sealed class BookingForm : Form
{
    // ── Form fields ──
    private TextBox txtCallerName;
    private TextBox txtPhone;
    private ComboBox cmbPickup;
    private ComboBox cmbDropoff;
    private NumericUpDown nudPassengers;
    private ComboBox cmbVehicle;
    private ComboBox cmbPickupTime;
    private TextBox txtNotes;

    // Address verification status
    private Label lblPickupStatus;
    private Label lblDropoffStatus;

    // Fare result
    private Label lblFare;
    private Label lblEta;
    private Label lblPickupResolved;
    private Label lblDropoffResolved;

    // Action buttons
    private Button btnVerify;
    private Button btnBook;
    private Button btnCancel;

    // Status
    private Label lblStatus;

    // Services
    private readonly IFareCalculator _fareCalculator;
    private readonly IDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly SupabaseSettings _supabaseSettings;

    // State
    private FareResult? _lastFareResult;
    private bool _addressesVerified;
    private string[]? _callerPickupHistory;
    private string[]? _callerDropoffHistory;

    /// <summary>The completed booking state (set when confirmed).</summary>
    public BookingState? CompletedBooking { get; private set; }

    public BookingForm(
        IFareCalculator fareCalculator,
        IDispatcher dispatcher,
        ILogger logger,
        SupabaseSettings supabaseSettings,
        string? callerPhone = null,
        string? callerName = null)
    {
        _fareCalculator = fareCalculator;
        _dispatcher = dispatcher;
        _logger = logger;
        _supabaseSettings = supabaseSettings;

        BuildUi();

        if (!string.IsNullOrEmpty(callerPhone))
        {
            txtPhone.Text = callerPhone;
            _ = LoadCallerHistoryAsync(callerPhone);
        }
        if (!string.IsNullOrEmpty(callerName))
            txtCallerName.Text = callerName;
    }

    // ══════════════════════════════════════════
    //  UI CONSTRUCTION
    // ══════════════════════════════════════════

    private void BuildUi()
    {
        var bg = Color.FromArgb(30, 30, 30);
        var bgPanel = Color.FromArgb(45, 45, 48);
        var bgInput = Color.FromArgb(60, 60, 65);
        var fg = Color.FromArgb(220, 220, 220);
        var accent = Color.FromArgb(0, 122, 204);
        var green = Color.FromArgb(40, 167, 69);
        var red = Color.FromArgb(220, 53, 69);

        Text = "📋 New Booking";
        Size = new Size(520, 680);
        MinimumSize = new Size(480, 620);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = bg;
        ForeColor = fg;
        Font = new Font("Segoe UI", 9.5F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        var y = 15;

        // ── Caller Info ──
        AddSectionLabel("👤 Caller Information", 15, ref y);
        y += 5;

        AddLabel("Name:", 15, y + 3);
        txtCallerName = AddTextBox(110, y, 180, bgInput, fg);
        txtCallerName.PlaceholderText = "Customer name";

        AddLabel("Phone:", 305, y + 3);
        txtPhone = AddTextBox(360, y, 135, bgInput, fg);
        txtPhone.PlaceholderText = "+44...";
        y += 35;

        // ── Journey Details ──
        AddSectionLabel("🚕 Journey Details", 15, ref y);
        y += 5;

        AddLabel("Pickup:", 15, y + 3);
        cmbPickup = AddComboBox(110, y, 340, bgInput, fg);
        cmbPickup.Text = "";
        lblPickupStatus = new Label { Text = "", Location = new Point(455, y + 3), Size = new Size(30, 20), Font = new Font("Segoe UI", 11F) };
        Controls.Add(lblPickupStatus);
        y += 32;

        lblPickupResolved = new Label
        {
            Text = "",
            Location = new Point(110, y),
            Size = new Size(370, 16),
            ForeColor = Color.FromArgb(120, 180, 120),
            Font = new Font("Segoe UI", 7.5F, FontStyle.Italic)
        };
        Controls.Add(lblPickupResolved);
        y += 20;

        AddLabel("Drop-off:", 15, y + 3);
        cmbDropoff = AddComboBox(110, y, 340, bgInput, fg);
        cmbDropoff.Text = "";
        lblDropoffStatus = new Label { Text = "", Location = new Point(455, y + 3), Size = new Size(30, 20), Font = new Font("Segoe UI", 11F) };
        Controls.Add(lblDropoffStatus);
        y += 32;

        lblDropoffResolved = new Label
        {
            Text = "",
            Location = new Point(110, y),
            Size = new Size(370, 16),
            ForeColor = Color.FromArgb(120, 180, 120),
            Font = new Font("Segoe UI", 7.5F, FontStyle.Italic)
        };
        Controls.Add(lblDropoffResolved);
        y += 22;

        AddLabel("Passengers:", 15, y + 3);
        nudPassengers = new NumericUpDown
        {
            Location = new Point(110, y),
            Size = new Size(60, 25),
            Minimum = 1,
            Maximum = 16,
            Value = 1,
            BackColor = bgInput,
            ForeColor = fg,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(nudPassengers);

        AddLabel("Vehicle:", 210, y + 3);
        cmbVehicle = AddComboBox(280, y, 170, bgInput, fg);
        cmbVehicle.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbVehicle.Items.AddRange(new object[] { "Saloon", "Estate", "MPV (6-seat)", "Minibus (8-seat)" });
        cmbVehicle.SelectedIndex = 0;
        y += 35;

        AddLabel("Pickup Time:", 15, y + 3);
        cmbPickupTime = AddComboBox(110, y, 130, bgInput, fg);
        cmbPickupTime.Items.AddRange(new object[] { "ASAP", "In 15 mins", "In 30 mins", "In 1 hour", "Custom..." });
        cmbPickupTime.SelectedIndex = 0;
        y += 35;

        AddLabel("Notes:", 15, y + 3);
        txtNotes = new TextBox
        {
            Location = new Point(110, y),
            Size = new Size(380, 50),
            Multiline = true,
            BackColor = bgInput,
            ForeColor = fg,
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "Special instructions (wheelchair, luggage, etc.)"
        };
        Controls.Add(txtNotes);
        y += 60;

        // ── Verify Button ──
        btnVerify = new Button
        {
            Text = "🔍 Verify Addresses & Get Quote",
            Location = new Point(15, y),
            Size = new Size(475, 36),
            BackColor = accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold)
        };
        btnVerify.FlatAppearance.BorderSize = 0;
        btnVerify.Click += async (s, e) => await VerifyAndQuoteAsync();
        Controls.Add(btnVerify);
        y += 45;

        // ── Fare Result Panel ──
        var pnlFare = new Panel
        {
            Location = new Point(15, y),
            Size = new Size(475, 60),
            BackColor = Color.FromArgb(35, 50, 35),
            BorderStyle = BorderStyle.FixedSingle
        };

        lblFare = new Label
        {
            Text = "Fare: —",
            Location = new Point(10, 8),
            Size = new Size(220, 22),
            ForeColor = Color.LimeGreen,
            Font = new Font("Segoe UI", 12F, FontStyle.Bold)
        };
        pnlFare.Controls.Add(lblFare);

        lblEta = new Label
        {
            Text = "ETA: —",
            Location = new Point(10, 32),
            Size = new Size(220, 20),
            ForeColor = Color.FromArgb(180, 220, 180),
            Font = new Font("Segoe UI", 9F)
        };
        pnlFare.Controls.Add(lblEta);

        var lblVehicleInfo = new Label
        {
            Text = "💡 Vehicle auto-suggests based on passengers",
            Location = new Point(240, 20),
            Size = new Size(225, 20),
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 7.5F, FontStyle.Italic)
        };
        pnlFare.Controls.Add(lblVehicleInfo);

        Controls.Add(pnlFare);
        y += 70;

        // ── Status label ──
        lblStatus = new Label
        {
            Text = "",
            Location = new Point(15, y),
            Size = new Size(475, 18),
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8F, FontStyle.Italic),
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(lblStatus);
        y += 22;

        // ── Action buttons ──
        btnBook = new Button
        {
            Text = "✅ Confirm & Dispatch",
            Location = new Point(15, y),
            Size = new Size(230, 40),
            BackColor = green,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            Enabled = false
        };
        btnBook.FlatAppearance.BorderSize = 0;
        btnBook.Click += async (s, e) => await ConfirmBookingAsync();
        Controls.Add(btnBook);

        btnCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(260, y),
            Size = new Size(230, 40),
            BackColor = Color.FromArgb(80, 80, 85),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 10F)
        };
        btnCancel.FlatAppearance.BorderSize = 0;
        btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(btnCancel);

        // Auto-suggest vehicle based on passenger count
        nudPassengers.ValueChanged += (s, e) =>
        {
            var pax = (int)nudPassengers.Value;
            if (pax <= 4) cmbVehicle.SelectedIndex = 0;       // Saloon
            else if (pax <= 6) cmbVehicle.SelectedIndex = 2;   // MPV
            else cmbVehicle.SelectedIndex = 3;                  // Minibus
        };
    }

    // ══════════════════════════════════════════
    //  CALLER HISTORY
    // ══════════════════════════════════════════

    private async Task LoadCallerHistoryAsync(string phone)
    {
        try
        {
            SetStatus("Loading caller history…");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            // Normalize phone for lookup
            var normalized = phone.Trim().Replace(" ", "");
            var url = $"{_supabaseSettings.Url}/rest/v1/callers?phone_number=eq.{Uri.EscapeDataString(normalized)}&select=*";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("apikey", _supabaseSettings.AnonKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                SetStatus("No caller history found");
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;

            if (arr.GetArrayLength() == 0)
            {
                SetStatus("New caller — no history");
                return;
            }

            var caller = arr[0];

            // Populate name if we have it
            if (caller.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            {
                var name = nameEl.GetString();
                if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(txtCallerName.Text))
                    txtCallerName.Text = name;
            }

            // Populate pickup history
            if (caller.TryGetProperty("pickup_addresses", out var pickups) && pickups.ValueKind == JsonValueKind.Array)
            {
                _callerPickupHistory = pickups.EnumerateArray()
                    .Select(a => a.GetString() ?? "")
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Distinct()
                    .Take(15)
                    .ToArray();

                cmbPickup.Items.Clear();
                foreach (var addr in _callerPickupHistory)
                    cmbPickup.Items.Add(addr);
            }

            // Populate dropoff history
            if (caller.TryGetProperty("dropoff_addresses", out var dropoffs) && dropoffs.ValueKind == JsonValueKind.Array)
            {
                _callerDropoffHistory = dropoffs.EnumerateArray()
                    .Select(a => a.GetString() ?? "")
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Distinct()
                    .Take(15)
                    .ToArray();

                cmbDropoff.Items.Clear();
                foreach (var addr in _callerDropoffHistory)
                    cmbDropoff.Items.Add(addr);
            }

            // Show last booking info
            var totalBookings = 0;
            if (caller.TryGetProperty("total_bookings", out var tb))
                totalBookings = tb.GetInt32();

            SetStatus($"Returning caller — {totalBookings} previous booking{(totalBookings != 1 ? "s" : "")}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load caller history");
            SetStatus("Could not load history");
        }
    }

    // ══════════════════════════════════════════
    //  ADDRESS VERIFICATION & FARE
    // ══════════════════════════════════════════

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
        lblPickupStatus.Text = "⏳";
        lblDropoffStatus.Text = "⏳";

        try
        {
            var phone = txtPhone.Text.Trim();
            _lastFareResult = await _fareCalculator.ExtractAndCalculateWithAiAsync(pickup, dropoff, phone);

            // Update pickup status
            if (_lastFareResult.PickupLat.HasValue && _lastFareResult.PickupLat != 0)
            {
                lblPickupStatus.Text = "✅";
                lblPickupResolved.Text = _lastFareResult.PickupFormatted ?? pickup;
            }
            else
            {
                lblPickupStatus.Text = "⚠️";
                lblPickupResolved.Text = "Could not verify";
            }

            // Update dropoff status
            if (_lastFareResult.DestLat.HasValue && _lastFareResult.DestLat != 0)
            {
                lblDropoffStatus.Text = "✅";
                lblDropoffResolved.Text = _lastFareResult.DestFormatted ?? dropoff;
            }
            else
            {
                lblDropoffStatus.Text = "⚠️";
                lblDropoffResolved.Text = "Could not verify";
            }

            // Show fare
            lblFare.Text = $"Fare: {_lastFareResult.Fare}";
            lblEta.Text = $"ETA: {_lastFareResult.Eta}";

            // Handle disambiguation
            if (_lastFareResult.NeedsClarification)
            {
                var msg = "Some addresses need clarification:\n\n";
                if (_lastFareResult.PickupAlternatives?.Length > 0)
                    msg += $"Pickup alternatives:\n  • {string.Join("\n  • ", _lastFareResult.PickupAlternatives)}\n\n";
                if (_lastFareResult.DestAlternatives?.Length > 0)
                    msg += $"Drop-off alternatives:\n  • {string.Join("\n  • ", _lastFareResult.DestAlternatives)}\n";
                
                MessageBox.Show(msg, "Address Clarification", MessageBoxButtons.OK, MessageBoxIcon.Information);
                SetStatus("Please refine addresses and re-verify");
            }
            else
            {
                _addressesVerified = true;
                btnBook.Enabled = true;
                SetStatus("✅ Addresses verified — ready to dispatch");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Address verification failed");
            lblPickupStatus.Text = "❌";
            lblDropoffStatus.Text = "❌";
            SetStatus($"Verification failed: {ex.Message}");
        }
        finally
        {
            btnVerify.Enabled = true;
            btnVerify.Text = "🔍 Verify Addresses & Get Quote";
        }
    }

    // ══════════════════════════════════════════
    //  CONFIRM & DISPATCH
    // ══════════════════════════════════════════

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
            var booking = new BookingState
            {
                Name = txtCallerName.Text.Trim(),
                Pickup = cmbPickup.Text.Trim(),
                Destination = cmbDropoff.Text.Trim(),
                Passengers = (int)nudPassengers.Value,
                PickupTime = cmbPickupTime.Text == "ASAP" ? "now" : cmbPickupTime.Text,
                Fare = _lastFareResult.Fare,
                Eta = _lastFareResult.Eta,
                Confirmed = true,

                // Geocoded data
                PickupLat = _lastFareResult.PickupLat,
                PickupLon = _lastFareResult.PickupLon,
                PickupStreet = _lastFareResult.PickupStreet,
                PickupNumber = _lastFareResult.PickupNumber,
                PickupPostalCode = _lastFareResult.PickupPostalCode,
                PickupCity = _lastFareResult.PickupCity,
                PickupFormatted = _lastFareResult.PickupFormatted,

                DestLat = _lastFareResult.DestLat,
                DestLon = _lastFareResult.DestLon,
                DestStreet = _lastFareResult.DestStreet,
                DestNumber = _lastFareResult.DestNumber,
                DestPostalCode = _lastFareResult.DestPostalCode,
                DestCity = _lastFareResult.DestCity,
                DestFormatted = _lastFareResult.DestFormatted,
            };

            var phone = txtPhone.Text.Trim();

            // Dispatch to BSQD
            var dispatched = await _dispatcher.DispatchAsync(booking, phone);

            if (dispatched)
            {
                // Generate a ref
                booking.BookingRef = $"MAN-{DateTime.Now:HHmmss}";

                // Send WhatsApp notification
                _ = _dispatcher.SendWhatsAppAsync(phone);

                CompletedBooking = booking;
                SetStatus($"✅ Booking dispatched! Ref: {booking.BookingRef}");
                _logger.LogInformation("📋 Manual booking dispatched: {Ref} — {Pickup} → {Dest}",
                    booking.BookingRef, booking.Pickup, booking.Destination);

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

    // ── Helpers ──

    private void SetStatus(string text) => lblStatus.Text = text;

    private Label AddLabel(string text, int x, int y)
    {
        var lbl = new Label { Text = text, Location = new Point(x, y), AutoSize = true, ForeColor = Color.FromArgb(180, 180, 180) };
        Controls.Add(lbl);
        return lbl;
    }

    private void AddSectionLabel(string text, int x, ref int y)
    {
        var lbl = new Label
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            ForeColor = Color.FromArgb(0, 150, 220),
            Font = new Font("Segoe UI", 10F, FontStyle.Bold)
        };
        Controls.Add(lbl);
        y += 22;
    }

    private TextBox AddTextBox(int x, int y, int w, Color bg, Color fg)
    {
        var txt = new TextBox
        {
            Location = new Point(x, y),
            Size = new Size(w, 25),
            BackColor = bg,
            ForeColor = fg,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(txt);
        return txt;
    }

    private ComboBox AddComboBox(int x, int y, int w, Color bg, Color fg)
    {
        var cmb = new ComboBox
        {
            Location = new Point(x, y),
            Size = new Size(w, 25),
            BackColor = bg,
            ForeColor = fg,
            FlatStyle = FlatStyle.Flat,
            DropDownStyle = ComboBoxStyle.DropDown // Allow free text + dropdown
        };
        Controls.Add(cmb);
        return cmb;
    }
}
