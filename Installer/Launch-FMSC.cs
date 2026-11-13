using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// GUI-subsystem bootstrap only. Recovery decisions remain in Launch-FMSC.ps1.
internal static class FmscLauncher
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            bool recover = args.Length == 1 && args[0] == "--recover";
            if (args.Length != 0 && !recover)
                throw new ArgumentException("Invalid FMSC launcher arguments.");

            string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string script = Path.Combine(root, "Launch-FMSC.ps1");
            if (!File.Exists(script)) throw new FileNotFoundException("The FMSC launcher is missing. Contact support.");
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = "-NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File \"" + script + "\"" +
                    (recover ? " -ResumeUpdate" : ""),
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using (Process process = Process.Start(start)) { }
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "FMSC - Startup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
