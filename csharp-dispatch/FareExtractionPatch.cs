// ═══════════════════════════════════════════════════════════════
// FARE EXTRACTION PATCH for DispatchSystem
// ═══════════════════════════════════════════════════════════════
//
// This file contains the code changes needed to extract fare
// from MQTT job payloads and display it in the JobListPanel grid.
//
// ═══════════════════════════════════════════════════════════════

// ──────────────────────────────────────────────
// 1. MqttDispatchClient.cs — Parse fare from booking payload
// ──────────────────────────────────────────────
//
// In your OnBookingReceived handler where you build the Job object,
// add fare extraction. The web app sends fare under multiple possible
// field names. Add this helper method:

/*
/// <summary>
/// Extracts fare from MQTT payload, checking multiple field names.
/// Returns 0 if no fare found.
/// </summary>
private static decimal ExtractFare(JsonElement root)
{
    string[] fareFields = { "fare", "estimatedFare", "estimatedPrice", "price", "amount", "cost" };
    
    foreach (var field in fareFields)
    {
        if (root.TryGetProperty(field, out var val))
        {
            // Handle string values like "12.50" or "£12.50"
            if (val.ValueKind == JsonValueKind.String)
            {
                var str = val.GetString()?.Replace("£", "").Replace("$", "").Trim();
                if (decimal.TryParse(str, out var parsed))
                    return parsed;
            }
            // Handle numeric values
            else if (val.ValueKind == JsonValueKind.Number)
            {
                return val.GetDecimal();
            }
        }
    }
    return 0;
}
*/

// Then in your booking parse code, use it:
/*
    job.EstimatedFare = ExtractFare(root);
*/


// ──────────────────────────────────────────────
// 2. Job.cs — Ensure EstimatedFare property exists
// ──────────────────────────────────────────────
//
// Your Job model should have:
/*
    public decimal EstimatedFare { get; set; }
*/


// ──────────────────────────────────────────────
// 3. JobListPanel.cs — Add Fare column to DataGridView
// ──────────────────────────────────────────────
//
// OPTION A: If using DataGridView with auto-generated columns,
// just ensure EstimatedFare is in the data source.
//
// OPTION B: If manually building columns, add this column:

/*
// In your JobListPanel constructor or InitializeColumns method:

var colFare = new DataGridViewTextBoxColumn
{
    Name = "Fare",
    HeaderText = "💷 Fare",
    DataPropertyName = "FareDisplay", // see below
    Width = 80,
    DefaultCellStyle = new DataGridViewCellStyle
    {
        Alignment = DataGridViewContentAlignment.MiddleRight,
        Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
        ForeColor = Color.LimeGreen
    }
};

// Insert after the Passengers column (adjust index as needed):
_grid.Columns.Insert(5, colFare);
*/


// ──────────────────────────────────────────────
// 4. Job.cs — Add FareDisplay computed property
// ──────────────────────────────────────────────

/*
/// <summary>Formatted fare for grid display.</summary>
public string FareDisplay => EstimatedFare > 0 
    ? EstimatedFare.ToString("C2")  // £12.50 format
    : "—";
*/


// ──────────────────────────────────────────────
// 5. JobListPanel.cs — RefreshJobs with fare coloring
// ──────────────────────────────────────────────
//
// In your RefreshJobs method, after populating rows,
// add conditional formatting for the fare column:

/*
public void RefreshJobs(List<Job> jobs)
{
    // ... existing row population code ...

    // After populating, color fare cells
    foreach (DataGridViewRow row in _grid.Rows)
    {
        if (row.DataBoundItem is Job job)
        {
            var fareCell = row.Cells["Fare"];
            if (job.EstimatedFare > 0)
            {
                fareCell.Style.ForeColor = Color.LimeGreen;
                fareCell.Style.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            }
            else
            {
                fareCell.Style.ForeColor = Color.Gray;
            }
        }
    }
}
*/


// ──────────────────────────────────────────────
// 6. ALTERNATIVE: Manual row building (if not data-bound)
// ──────────────────────────────────────────────
//
// If you manually add rows to the grid, include fare:

/*
foreach (var job in jobs)
{
    var row = new DataGridViewRow();
    row.CreateCells(_grid,
        job.Id,
        job.Pickup,
        job.Dropoff,
        job.CallerPhone,
        job.Passengers,
        job.FareDisplay,    // ← Add this
        job.Status.ToString(),
        FormatAge(job.CreatedAt)
    );

    // Color the fare cell
    var fareIdx = 5; // adjust to your column index
    row.Cells[fareIdx].Style.ForeColor = job.EstimatedFare > 0 
        ? Color.LimeGreen 
        : Color.Gray;
    row.Cells[fareIdx].Style.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);

    _grid.Rows.Add(row);
}
*/


// ──────────────────────────────────────────────
// 7. DispatchDb.cs — Store/retrieve fare in SQLite
// ──────────────────────────────────────────────
//
// If your Jobs table doesn't have an EstimatedFare column yet:

/*
-- SQLite migration:
ALTER TABLE Jobs ADD COLUMN EstimatedFare REAL DEFAULT 0;
*/

// Update your InsertJob:
/*
cmd.CommandText = @"INSERT OR REPLACE INTO Jobs 
    (Id, Pickup, Dropoff, PickupLat, PickupLng, CallerPhone, CallerName, 
     Passengers, Status, CreatedAt, EstimatedFare)
    VALUES (@id, @pickup, @dropoff, @plat, @plng, @phone, @name, 
     @pax, @status, @created, @fare)";
// ... other params ...
cmd.Parameters.AddWithValue("@fare", job.EstimatedFare);
*/

// Update your GetActiveJobs reader:
/*
job.EstimatedFare = reader.IsDBNull(reader.GetOrdinal("EstimatedFare")) 
    ? 0 
    : reader.GetDecimal(reader.GetOrdinal("EstimatedFare"));
*/


// ──────────────────────────────────────────────
// 8. Copy Details context menu — include fare
// ──────────────────────────────────────────────
//
// In SetupJobContextMenu, update the copy text (already has it but confirm):
/*
var text = $"Job: {job.Id}\n" +
           $"Pickup: {job.Pickup}\n" +
           $"Dropoff: {job.Dropoff}\n" +
           $"Pax: {job.Passengers}\n" +
           $"Phone: {job.CallerPhone}\n" +
           $"Fare: {job.FareDisplay}\n" +
           $"Status: {job.Status}";
*/
