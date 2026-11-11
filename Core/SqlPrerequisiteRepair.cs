namespace FMSC_SMS.Core;

/// <summary>Never start LocalDB setup before its runtime is usable or after a pending reboot.</summary>
internal static class SqlPrerequisiteRepair
{
    internal static async Task<int> EnsureVcRuntimeAsync(Func<bool> usable, Func<string,Task<int>> run)
    {
        if (usable()) return 0;
        int code = await run("/install");
        if (code is 3010 or 1641) return 3010;
        // Registered-but-damaged runtimes can make /install a no-op.
        if (code == 0 && !usable())
        {
            code = await run("/repair");
            if (code is 3010 or 1641) return 3010;
        }
        // 1638 is acceptable only when a newer, usable runtime is present.
        if ((code is not (0 or 1638)) || !usable())
            throw new InvalidOperationException($"Microsoft Visual C++ repair failed or is unverified (code {code}). LocalDB repair was not started.");
        return 0;
    }
}
