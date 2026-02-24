using DispatchSystem.Data;

namespace DispatchSystem.UI;

/// <summary>
/// ListView showing all registered drivers with status and vehicle info.
/// </summary>
public sealed class DriverListPanel : Panel
{
    private readonly ListView _list;

    public event Action<string>? OnDriverSelected;
    public event Action<string>? OnDriverLongPressStart;
    public event Action? OnDriverLongPressEnd;
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

        // Long-press PTT on driver rows
        _list.MouseDown += (_, e) =>
        {
            var hit = _list.HitTest(e.Location);
            if (hit.Item?.Tag is string driverId)
                OnDriverLongPressStart?.Invoke(driverId);
        };
        _list.MouseUp += (_, _) => OnDriverLongPressEnd?.Invoke();
        _list.MouseLeave += (_, _) => OnDriverLongPressEnd?.Invoke();

        Controls.Add(_list);
        Controls.Add(lbl);
    }

    public void RefreshDrivers(List<Driver> drivers)
    {
        if (InvokeRequired) { BeginInvoke(() => RefreshDrivers(drivers)); return; }

        _list.BeginUpdate();

        // Build a lookup of incoming drivers
        var incoming = new Dictionary<string, Driver>();
        foreach (var d in drivers) incoming[d.Id] = d;

        // Remove drivers no longer present
        for (int i = _list.Items.Count - 1; i >= 0; i--)
        {
            if (_list.Items[i].Tag is string id && !incoming.ContainsKey(id))
                _list.Items.RemoveAt(i);
        }

        // Update existing or add new
        foreach (var d in drivers)
        {
            var age = (DateTime.UtcNow - d.LastGpsUpdate).TotalMinutes;
            var gpsAge = age < 1 ? "Live" : age < 5 ? $"{age:F0}m" : "Stale";
            var color = d.Status switch
            {
                DriverStatus.Online => Color.LimeGreen,
                DriverStatus.OnJob => Color.Cyan,
                DriverStatus.Break => Color.Orange,
                _ => Color.Gray
            };

            // Find existing item
            ListViewItem? existing = null;
            foreach (ListViewItem li in _list.Items)
            {
                if (li.Tag is string id && id == d.Id) { existing = li; break; }
            }

            if (existing != null)
            {
                // Only update sub-items if values changed (avoids flicker)
                UpdateSubItem(existing, 1, d.Name);
                UpdateSubItem(existing, 2, d.Registration);
                UpdateSubItem(existing, 3, d.Status.ToString());
                UpdateSubItem(existing, 4, d.Vehicle.ToString());
                UpdateSubItem(existing, 5, gpsAge);
                if (existing.ForeColor != color) existing.ForeColor = color;
            }
            else
            {
                var item = new ListViewItem(d.Id) { Tag = d.Id, ForeColor = color };
                item.SubItems.Add(d.Name);
                item.SubItems.Add(d.Registration);
                item.SubItems.Add(d.Status.ToString());
                item.SubItems.Add(d.Vehicle.ToString());
                item.SubItems.Add(gpsAge);
                _list.Items.Add(item);
            }
        }

        _list.EndUpdate();
    }

    private static void UpdateSubItem(ListViewItem item, int index, string value)
    {
        if (item.SubItems[index].Text != value)
            item.SubItems[index].Text = value;
    }
}
