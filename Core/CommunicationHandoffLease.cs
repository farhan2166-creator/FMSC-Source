namespace FMSC_SMS.Core;

/// <summary>One handoff window per batch on the supported per-user LocalDB host.</summary>
internal static class CommunicationHandoffLease
{
    internal static FileStream Acquire(Guid batchId, string? directory = null)
    {
        if (batchId == Guid.Empty) throw new ArgumentException("A saved batch is required.", nameof(batchId));
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FMSC", "Communication", "Handoffs");
        Directory.CreateDirectory(directory);
        try
        {
            // An OS handle survives awaits, excludes other FMSC processes and
            // is released on crash. Do not delete the path while a lease exists.
            return new FileStream(Path.Combine(directory, batchId.ToString("N") + ".lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when ((uint)ex.HResult is 0x80070020 or 0x80070021)
        {
            throw new InvalidOperationException("This WhatsApp batch is already open in another FMSC window. Close it there before resuming.", ex);
        }
    }
}
