using DispatchSystem.Data;

namespace DispatchSystem.UI;

/// <summary>
/// DataGridView showing active and recent jobs with wait-time color coding.
/// </summary>
public sealed class JobListPanel : Panel
{
    private readonly DataGridView _grid;

    public event Action<string>? OnJobSelected;

    public string? SelectedJobId
    {
        get
        {
            if (_grid.CurrentRow == null) return null;
            return _grid.CurrentRow.Cells["ColId"].Value as string;
        }
    }

    public JobListPanel()
    {
        var lbl = new Label
        {
            Text = "📋 Jobs",
            Dock = DockStyle.Top,
            Height = 25,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(40, 40, 45),
            Padding = new Padding(4, 2, 0, 0)
        };

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Color.FromArgb(25, 25, 30),
            ForeColor = Color.White,
            GridColor = Color.FromArgb(50, 50, 55),
            Font = new Font("Segoe UI", 9F),
            BorderStyle = BorderStyle.None,
            RowHeadersVisible = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 32,
            RowTemplate = { Height = 28 },
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(25, 25, 30),
                ForeColor = Color.White,
                SelectionBackColor = Color.FromArgb(60, 60, 80),
                SelectionForeColor = Color.White,
                Padding = new Padding(4, 0, 4, 0)
            },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(40, 40, 50),
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                Padding = new Padding(4, 0, 4, 0)
            },
            EnableHeadersVisualStyles = false,
        };

        // Columns
        _grid.Columns.AddRange(
            Col("ColId", "ID", 80),
            Col("ColStatus", "Status", 75),
            Col("ColPickup", "Pickup", 160),
            Col("ColDropoff", "Dropoff", 160),
            Col("ColPassengers", "Pax", 40),
            Col("ColPhone", "Phone", 100),
            Col("ColRef", "Ref", 80),
            Col("ColWait", "Wait", 60),
            Col("ColDriver", "Driver", 80),
            Col("ColEta", "ETA", 50),
            Col("ColFare", "Fare", 60)
        );

        _grid.SelectionChanged += (_, _) =>
        {
            if (SelectedJobId != null) OnJobSelected?.Invoke(SelectedJobId);
        };

        Controls.Add(_grid);
        Controls.Add(lbl);
    }

    public void RefreshJobs(List<Job> jobs)
    {
        if (InvokeRequired) { BeginInvoke(() => RefreshJobs(jobs)); return; }

        var selectedId = SelectedJobId;
        _grid.SuspendLayout();
        _grid.Rows.Clear();

        foreach (var j in jobs)
        {
            var waitMins = (int)(DateTime.UtcNow - j.CreatedAt).TotalMinutes;
            var waitText = waitMins < 1 ? "<1m" : $"{waitMins}m";

            var idx = _grid.Rows.Add(
                j.Id,
                j.Status.ToString(),
                j.Pickup,
                j.Dropoff,
                j.Passengers,
                j.CallerPhone ?? "—",
                j.BookingRef ?? "—",
                waitText,
                j.AllocatedDriverId ?? "—",
                j.DriverEtaMinutes?.ToString() ?? "—",
                j.EstimatedFare?.ToString("C") ?? "—"
            );

            var row = _grid.Rows[idx];

            // Status color
            var statusColor = j.Status switch
            {
                JobStatus.Pending => Color.Yellow,
                JobStatus.Allocated => Color.Cyan,
                JobStatus.Accepted => Color.LimeGreen,
                JobStatus.PickedUp => Color.DodgerBlue,
                JobStatus.Completed => Color.Gray,
                JobStatus.Cancelled => Color.DarkRed,
                _ => Color.White
            };
            row.Cells["ColStatus"].Style.ForeColor = statusColor;

            // Wait time color (green < 10, amber < 20, red 20+)
            if (j.Status == JobStatus.Pending || j.Status == JobStatus.Allocated)
            {
                var waitColor = waitMins < 10 ? Color.LimeGreen
                    : waitMins < 20 ? Color.Orange
                    : Color.Red;
                row.Cells["ColWait"].Style.ForeColor = waitColor;
                row.Cells["ColWait"].Style.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            }
        }

        // Restore selection
        if (selectedId != null)
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Cells["ColId"].Value as string == selectedId)
                {
                    row.Selected = true;
                    _grid.CurrentCell = row.Cells[0];
                    break;
                }
            }
        }

        _grid.ResumeLayout();
    }

    private static DataGridViewTextBoxColumn Col(string name, string header, int width) => new()
    {
        Name = name,
        HeaderText = header,
        Width = width,
        SortMode = DataGridViewColumnSortMode.NotSortable
    };
}
