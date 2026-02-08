namespace AdaMain;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        this.SuspendLayout();

        // MainForm
        this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
        this.ClientSize = new System.Drawing.Size(900, 600);
        this.Name = "MainForm";
        this.Text = "AdaMain - Voice AI Taxi Bridge";
        this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
        this.BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
        this.ForeColor = System.Drawing.Color.White;

        this.ResumeLayout(false);
    }
}
