using System;
using System.Windows.Forms;

namespace AdaSdkModel;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Minimize Gen 2 blocking GC pauses that cause RTP underruns
        if (Environment.Is64BitProcess)
        {
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
            Console.WriteLine("🚀 .NET Runtime: Sustained Low Latency GC active");
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
