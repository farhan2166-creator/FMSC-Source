using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// Independent FMSC uninstall launcher. It runs no cleanup code in this process:
// the elevated PowerShell engine stages itself outside the application folder.
internal static class FmscUninstall
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            if (args.Length != 0)
                throw new InvalidOperationException("FMSC Uninstall does not accept command-line arguments.");

            string folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(folder))
                throw new IOException("Cannot find the FMSC uninstaller location.");

            string script = Path.Combine(folder, "UpdateSupport", "Uninstall-FMSC.ps1");
            if (!File.Exists(script))
                script = Path.Combine(folder, "Uninstall-FMSC.ps1");
            if (!File.Exists(script))
                throw new FileNotFoundException("The FMSC uninstaller files are incomplete.");

            var launch = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = "-NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File " + Quote(script),
                WorkingDirectory = folder,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using (Process started = Process.Start(launch))
            {
                if (started == null) throw new IOException("Could not start the uninstaller.");
            }
        }
        catch (Win32Exception error)
        {
            if (error.NativeErrorCode != 1223)
                MessageBox.Show(error.Message, "FMSC Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "FMSC Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string Quote(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('"') >= 0 ||
            path.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            throw new InvalidOperationException("Invalid uninstaller path.");
        return "\"" + path + "\"";
    }
}
