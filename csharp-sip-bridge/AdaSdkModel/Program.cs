// Last updated: 2026-02-21 (v2.8)
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

        // Catch shutdown race: BeginInvoke callbacks processed after handle destruction
        Application.ThreadException += (_, ex) =>
        {
            if (ex.Exception is System.IO.IOException ioe && ioe.HResult == unchecked((int)0x80070006))
                return; // "The handle is invalid" — safe to swallow on shutdown
            // Re-throw anything else
            throw ex.Exception;
        };
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.Run(new MainForm());
    }
}
