using DispatchSystem.Data;

namespace DispatchSystem.UI;

/// <summary>
/// ListView showing active and recent jobs with status indicators.
/// </summary>
public sealed class JobListPanel : Panel
{
    private readonly ListView _list;

    public event Action<string>? OnJobSelected;

    public string? SelectedJobId =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as string : null;

    public JobListPanel()
    {
        var lbl = new Label
        {
            Text = "📋 Jobs",
            Dock = DockStyle.Top,
            Height = 25,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(40, 40, 45)
        };

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            BackColor = Color.FromArgb(25, 25, 30),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9F),
            BorderStyle = BorderStyle.None,
            HeaderStyle = ColumnHeaderStyle.Nonclickable
        };

        _list.Columns.AddRange(new[]
        {
            new ColumnHeader { Text = "ID", Width = 80 },
            new ColumnHeader { Text = "Status", Width = 75 },
            new ColumnHeader { Text = "Pickup", Width = 150 },
            new ColumnHeader { Text = "Dropoff", Width = 150 },
            new ColumnHeader { Text = "Driver", Width = 80 },
            new ColumnHeader { Text = "ETA", Width = 50 },
            new ColumnHeader { Text = "Fare", Width = 60 }
        });

        _list.SelectedIndexChanged += (_, _) =>
        {
            if (SelectedJobId != null) OnJobSelected?.Invoke(SelectedJobId);
        };

        Controls.Add(_list);
        Controls.Add(lbl);
    }

    public void RefreshJobs(List<Job> jobs)
    {
        if (InvokeRequired) { BeginInvoke(() => RefreshJobs(jobs)); return; }

        _list.BeginUpdate();
        _list.Items.Clear();

        foreach (var j in jobs)
        {
            var item = new ListViewItem(j.Id) { Tag = j.Id };
            item.SubItems.Add(j.Status.ToString());
            item.SubItems.Add(j.Pickup);
            item.SubItems.Add(j.Dropoff);
            item.SubItems.Add(j.AllocatedDriverId ?? "—");
            item.SubItems.Add(j.DriverEtaMinutes?.ToString() ?? "—");
            item.SubItems.Add(j.EstimatedFare?.ToString("C") ?? "—");

            item.ForeColor = j.Status switch
            {
                JobStatus.Pending => Color.Yellow,
                JobStatus.Allocated => Color.Cyan,
                JobStatus.Accepted => Color.LimeGreen,
                JobStatus.PickedUp => Color.DodgerBlue,
                JobStatus.Completed => Color.Gray,
                JobStatus.Cancelled => Color.DarkRed,
                _ => Color.White
            };

            _list.Items.Add(item);
        }

        _list.EndUpdate();
    }
}
