using System;
using System.Windows.Forms;

namespace GammaPbxDemo;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
