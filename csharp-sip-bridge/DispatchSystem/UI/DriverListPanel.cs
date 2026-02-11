using DispatchSystem.Data;

namespace DispatchSystem.UI;

/// <summary>
/// ListView showing all registered drivers with status and vehicle info.
/// </summary>
public sealed class DriverListPanel : Panel
{
    private readonly ListView _list;

    public event Action<string>? OnDriverSelected;

    public string? SelectedDriverId =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as string : null;

    public DriverListPanel()
    {
        var lbl = new Label
        {
            Text = "🚗 Drivers",
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
            new ColumnHeader { Text = "ID", Width = 60 },
            new ColumnHeader { Text = "Name", Width = 90 },
            new ColumnHeader { Text = "Reg", Width = 70 },
            new ColumnHeader { Text = "Status", Width = 65 },
            new ColumnHeader { Text = "Vehicle", Width = 65 },
            new ColumnHeader { Text = "GPS", Width = 50 }
        });

        _list.SelectedIndexChanged += (_, _) =>
        {
            if (SelectedDriverId != null) OnDriverSelected?.Invoke(SelectedDriverId);
        };

        Controls.Add(_list);
        Controls.Add(lbl);
    }

    public void RefreshDrivers(List<Driver> drivers)
    {
        if (InvokeRequired) { BeginInvoke(() => RefreshDrivers(drivers)); return; }

        _list.BeginUpdate();
        _list.Items.Clear();

        foreach (var d in drivers)
        {
            var age = (DateTime.UtcNow - d.LastGpsUpdate).TotalMinutes;
            var gpsAge = age < 1 ? "Live" : age < 5 ? $"{age:F0}m" : "Stale";

            var item = new ListViewItem(d.Id) { Tag = d.Id };
            item.SubItems.Add(d.Name);
            item.SubItems.Add(d.Registration);
            item.SubItems.Add(d.Status.ToString());
            item.SubItems.Add(d.Vehicle.ToString());
            item.SubItems.Add(gpsAge);

            item.ForeColor = d.Status switch
            {
                DriverStatus.Online => Color.LimeGreen,
                DriverStatus.OnJob => Color.Cyan,
                DriverStatus.Break => Color.Orange,
                _ => Color.Gray
            };

            _list.Items.Add(item);
        }

        _list.EndUpdate();
    }
}
