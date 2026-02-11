using DispatchSystem.Data;

namespace DispatchSystem.UI;

/// <summary>
/// Simple dialog to manually add a driver to the system.
/// </summary>
public sealed class AddDriverDialog : Form
{
    public Driver? NewDriver { get; private set; }

    private readonly TextBox _txtId;
    private readonly TextBox _txtName;
    private readonly TextBox _txtPhone;
    private readonly ComboBox _cboVehicle;

    public AddDriverDialog()
    {
        Text = "Add Driver";
        Size = new Size(380, 300);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.FromArgb(35, 35, 40);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10F);

        var y = 15;
        AddLabel("Driver ID:", y); _txtId = AddTextBox(y); y += 45;
        AddLabel("Name:", y); _txtName = AddTextBox(y); y += 45;
        AddLabel("Phone:", y); _txtPhone = AddTextBox(y); y += 45;
        AddLabel("Vehicle:", y);

        _cboVehicle = new ComboBox
        {
            Location = new Point(110, y),
            Size = new Size(230, 28),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(50, 50, 55),
            ForeColor = Color.White
        };
        _cboVehicle.Items.AddRange(Enum.GetNames<VehicleType>());
        _cboVehicle.SelectedIndex = 0;
        Controls.Add(_cboVehicle);
        y += 50;

        var btnOk = new Button
        {
            Text = "✅ Add",
            DialogResult = DialogResult.OK,
            Location = new Point(110, y),
            Size = new Size(100, 35),
            BackColor = Color.FromArgb(0, 120, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnOk.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_txtId.Text) || string.IsNullOrWhiteSpace(_txtName.Text))
            {
                MessageBox.Show("ID and Name are required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }
            NewDriver = new Driver
            {
                Id = _txtId.Text.Trim(),
                Name = _txtName.Text.Trim(),
                Phone = _txtPhone.Text.Trim(),
                Vehicle = Enum.Parse<VehicleType>(_cboVehicle.SelectedItem?.ToString() ?? "Saloon"),
                Status = DriverStatus.Offline
            };
        };

        var btnCancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(220, y),
            Size = new Size(100, 35),
            BackColor = Color.FromArgb(80, 40, 40),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };

        Controls.AddRange(new Control[] { btnOk, btnCancel });
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private void AddLabel(string text, int y)
    {
        Controls.Add(new Label { Text = text, Location = new Point(15, y + 3), AutoSize = true });
    }

    private TextBox AddTextBox(int y)
    {
        var tb = new TextBox
        {
            Location = new Point(110, y),
            Size = new Size(230, 28),
            BackColor = Color.FromArgb(50, 50, 55),
            ForeColor = Color.White
        };
        Controls.Add(tb);
        return tb;
    }
}
