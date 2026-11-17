using System.Diagnostics;
using System.Security.Principal;

namespace FMSC_SMS.Core;

/// <summary>
/// Runs under the enrolled Windows account while it is signed in. The task
/// command line carries no secrets, school user identity or recipient data;
/// every invocation must independently verify its DPAPI token and SQL lease.
/// </summary>
internal static class CommunicationBackgroundTaskSchedule
{
    private static string TaskName => "FMSC Communication Queue " +
        (WindowsIdentity.GetCurrent().User?.Value ??
         throw new UnauthorizedAccessException("Cannot identify the Windows scheduled-task owner."));

    internal static async Task ConfigureAsync(bool enabled,CancellationToken token=default)
    {
        if (!CurrentUser.IsAdministrator ||
            !CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Only a school administrator may configure unattended communication.");
        var executable=Path.Combine(AppContext.BaseDirectory,"FMSC_SMS.exe");
        if (enabled && !File.Exists(executable))
            throw new FileNotFoundException("The installed FMSC executable was not found.",executable);
        var arguments=enabled
            ? new List<string> {
                "/Create","/F","/TN",TaskName,"/SC","MINUTE","/MO","1",
                "/RL","LIMITED","/IT","/TR",
                "\"" + executable + "\" --communication-queue-worker"
              }
            : new List<string> {"/Delete","/F","/TN",TaskName};
        var start=new ProcessStartInfo {
            FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"schtasks.exe"),
            UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true
        };
        foreach(string part in arguments)start.ArgumentList.Add(part);
        using var process=Process.Start(start)
            ?? throw new IOException("Windows Task Scheduler could not start.");
        process.StandardInput.Close();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        Task<string> stdout=process.StandardOutput.ReadToEndAsync(deadline.Token);
        Task<string> stderr=process.StandardError.ReadToEndAsync(deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch(OperationCanceledException)
        {
            if(!process.HasExited)process.Kill(entireProcessTree:true);
            throw new InvalidOperationException("Configuring the communication background task timed out.");
        }
        string output=await stdout,error=await stderr;
        if(process.ExitCode!=0)
        {
            if(!enabled && (output+error).Contains("cannot find",StringComparison.OrdinalIgnoreCase))
                return;
            throw new InvalidOperationException("Windows could not "+
                (enabled?"enable":"disable")+" the FMSC background communication task. "+
                "Check the current Windows account and Task Scheduler access.");
        }
    }
}
