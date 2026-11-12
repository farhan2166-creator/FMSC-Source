using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FMSC_SMS.Core;

internal static class SingleInstanceStartup
{
    internal static AppInstanceLease? Enter()
    {
        try
        {
            AppInstanceLease? lease = AppInstanceLease.TryAcquire();
            if (lease is not null) return lease;
        }
        catch (UnauthorizedAccessException)
        {
            // Another Windows account can own the global object without granting us access.
            // Never fall back to a per-user lock or start an unguarded second window.
            AlreadyRunning();
            return null;
        }
        catch (Exception error) when (error is IOException or WaitHandleCannotBeOpenedException or System.Security.SecurityException)
        {
            ModernMessageDialog.ShowWarning(null,
                "FMSC could not check whether another copy is running. Close other FMSC windows and try again. If the problem continues, contact the administrator.",
                "FMSC - Startup");
            return null;
        }
        bool activated;
        try { activated = TryActivateRunningWindow(); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        { activated = false; }
        if (!activated) AlreadyRunning();
        return null;
    }

    private static void AlreadyRunning() => ModernMessageDialog.ShowInfo(null,
        "FMSC is already running on this computer. Use the existing window, or close it before opening FMSC again. It may be running in another Windows session.",
        "FMSC Already Running");

    private static bool TryActivateRunningWindow()
    {
        using var current = Process.GetCurrentProcess();
        Process[] candidates = Process.GetProcessesByName(current.ProcessName);
        try
        {
            foreach (Process process in candidates)
            {
                try
                {
                    if (process.Id == current.Id || process.SessionId != current.SessionId) continue;
                    IntPtr window = process.MainWindowHandle;
                    if (window != IntPtr.Zero && TryActivateWindow(window)) return true;
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                { /* The first copy may be starting/exiting, or belong to another Windows account. */ }
            }
            return false;
        }
        finally { foreach (Process process in candidates) process.Dispose(); }
    }

    internal static bool TryActivateWindow(IntPtr window)
    {
        if (IsIconic(window)) ShowWindowAsync(window, 9); // SW_RESTORE retains the previous maximized state.
        return SetForegroundWindow(ActivationWindow(window));
    }

    internal static IntPtr ActivationWindow(IntPtr window)
    {
        // Last-active bookkeeping can still point at a disabled owner while
        // a modal dialog opens in the background. Prefer its enabled popup.
        IntPtr root = GetAncestor(window, 3); // GA_ROOTOWNER (also handles WinForms' hidden owner).
        if (root == IntPtr.Zero) root = window;
        IntPtr popup = GetLastActivePopup(root);
        if (CanActivate(popup)) return popup;
        popup = GetWindow(window, 6); // GW_ENABLEDPOPUP
        if (CanActivate(popup)) return popup;
        popup = GetWindow(root, 6);
        return CanActivate(popup) ? popup : window;
    }

    private static bool CanActivate(IntPtr window) => window != IntPtr.Zero
        && IsWindowVisible(window) && IsWindowEnabled(window);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetLastActivePopup(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
