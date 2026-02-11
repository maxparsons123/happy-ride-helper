using DispatchSystem.Data;

namespace DispatchSystem.UI;

/// <summary>
/// DataGridView showing completed/cancelled job history with date filtering and CSV export.
/// </summary>
public sealed class JobHistoryPanel : Panel
{
    private readonly DataGridView _grid;
    private readonly DateTimePicker _dtFrom;
    private readonly DateTimePicker _dtTo;
    private readonly Button _btnFilter;
    private readonly Button _btnExport;
    private readonly Label _lblCount;

    public event Action<DateTime?, DateTime?>? OnFilterChanged;
    public event Action? OnExportRequested;

    private List<Job> _currentJobs = new();

    public JobHistoryPanel()
    {
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            BackColor = Color.FromArgb(40, 40, 45),
            Padding = new Padding(4, 4, 4, 4),
            FlowDirection = FlowDirection.LeftToRight
        };

        toolbar.Controls.Add(new Label
        {
            Text = "From:",
            ForeColor = Color.White,
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5F),
            Padding = new Padding(0, 4, 0, 0)
        });

        _dtFrom = new DateTimePicker
        {
            Format = DateTimePickerFormat.Short,
            Width = 100,
            Value = DateTime.Today.AddDays(-7)
        };

        toolbar.Controls.Add(new Label
        {
            Text = "To:",
            ForeColor = Color.White,
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5F),
            Padding = new Padding(6, 4, 0, 0)
        });

        _dtTo = new DateTimePicker
        {
            Format = DateTimePickerFormat.Short,
            Width = 100,
            Value = DateTime.Today.AddDays(1)
        };

        _btnFilter = new Button
        {
            Text = "🔍 Filter",
            BackColor = Color.FromArgb(50, 80, 140),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(80, 26),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold)
        };
        _btnFilter.Click += (_, _) => OnFilterChanged?.Invoke(_dtFrom.Value.Date, _dtTo.Value.Date.AddDays(1));

        _btnExport = new Button
        {
            Text = "📥 CSV",
            BackColor = Color.FromArgb(60, 100, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(70, 26),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold)
        };
        _btnExport.Click += (_, _) => ExportCsv();

        _lblCount = new Label
        {
            Text = "",
            ForeColor = Color.LightGray,
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5F),
            Padding = new Padding(10, 4, 0, 0)
        };

        toolbar.Controls.Add(_dtFrom);
        toolbar.Controls.Add(_dtTo);
        toolbar.Controls.Add(_btnFilter);
        toolbar.Controls.Add(_btnExport);
        toolbar.Controls.Add(_lblCount);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Color.FromArgb(25, 25, 30),
            ForeColor = Color.White,
            GridColor = Color.FromArgb(50, 50, 55),
            Font = new Font("Segoe UI", 8.5F),
            BorderStyle = BorderStyle.None,
            RowHeadersVisible = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            ColumnHeadersHeight = 30,
            RowTemplate = { Height = 26 },
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(25, 25, 30),
                ForeColor = Color.White,
                SelectionBackColor = Color.FromArgb(60, 60, 80),
                SelectionForeColor = Color.White
            },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(40, 40, 50),
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 8F, FontStyle.Bold)
            },
            EnableHeadersVisualStyles = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
        };

        _grid.Columns.AddRange(
            Col("ColId", "ID", 80),
            Col("ColStatus", "Status", 75),
            Col("ColPickup", "Pickup", 150),
            Col("ColDropoff", "Dropoff", 150),
            Col("ColPax", "Pax", 40),
            Col("ColPhone", "Phone", 100),
            Col("ColDriver", "Driver", 80),
            Col("ColFare", "Fare", 60),
            Col("ColCreated", "Created", 130),
            Col("ColCompleted", "Completed", 130)
        );

        Controls.Add(_grid);
        Controls.Add(toolbar);
    }

    public void RefreshHistory(List<Job> jobs)
    {
        if (InvokeRequired) { BeginInvoke(() => RefreshHistory(jobs)); return; }

        _currentJobs = jobs;
        _grid.SuspendLayout();
        _grid.Rows.Clear();

        foreach (var j in jobs)
        {
            var idx = _grid.Rows.Add(
                j.Id,
                j.Status.ToString(),
                j.Pickup,
                j.Dropoff,
                j.Passengers,
                j.CallerPhone ?? "—",
                j.AllocatedDriverId ?? "—",
                j.EstimatedFare?.ToString("C") ?? "—",
                j.CreatedAt.ToLocalTime().ToString("dd/MM HH:mm"),
                j.CompletedAt?.ToLocalTime().ToString("dd/MM HH:mm") ?? "—"
            );

            _grid.Rows[idx].Cells["ColStatus"].Style.ForeColor =
                j.Status == JobStatus.Completed ? Color.LimeGreen : Color.OrangeRed;
        }

        _lblCount.Text = $"{jobs.Count} jobs";
        _grid.ResumeLayout();
    }

    private void ExportCsv()
    {
        if (_currentJobs.Count == 0) return;

        using var dlg = new SaveFileDialog
        {
            Filter = "CSV files|*.csv",
            FileName = $"dispatch-history-{DateTime.Now:yyyyMMdd}.csv"
        };

        if (dlg.ShowDialog() != DialogResult.OK) return;

        var lines = new List<string>
        {
            "ID,Status,Pickup,Dropoff,Passengers,Phone,Driver,Fare,Created,Completed"
        };

        foreach (var j in _currentJobs)
        {
            lines.Add(string.Join(",",
                Esc(j.Id), j.Status, Esc(j.Pickup), Esc(j.Dropoff),
                j.Passengers, Esc(j.CallerPhone ?? ""), Esc(j.AllocatedDriverId ?? ""),
                j.EstimatedFare?.ToString("F2") ?? "",
                j.CreatedAt.ToString("o"),
                j.CompletedAt?.ToString("o") ?? ""));
        }

        File.WriteAllLines(dlg.FileName, lines);
        MessageBox.Show($"Exported {_currentJobs.Count} jobs.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string Esc(string s) => $"\"{s.Replace("\"", "\"\"")}\"";

    private static DataGridViewTextBoxColumn Col(string name, string header, int width) => new()
    {
        Name = name,
        HeaderText = header,
        Width = width,
        SortMode = DataGridViewColumnSortMode.NotSortable
    };
}
