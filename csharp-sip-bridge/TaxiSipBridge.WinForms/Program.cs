namespace TaxiSipBridge;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Enable visual styles and high DPI support
        ApplicationConfiguration.Initialize();
        
        // Set up global exception handling
        Application.ThreadException += (s, e) =>
        {
            MessageBox.Show($"Unhandled error: {e.Exception.Message}", "Error", 
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                MessageBox.Show($"Fatal error: {ex.Message}", "Fatal Error", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        
        Application.Run(new MainForm());
    }
}
