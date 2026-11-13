using System.Diagnostics;

namespace FMSC_SMS.Core.Updates;

internal enum UpdateDownloadPhase { Connecting, Downloading, Retrying, Verifying, Preparing, Ready, Failed }

internal sealed record UpdateDownloadProgress(UpdateDownloadPhase Phase, long ReceivedBytes, long TotalBytes,
    double BytesPerSecond, TimeSpan Elapsed, TimeSpan? TimeRemaining, int PartNumber, int PartCount,
    int Attempt, long ReusedBytes, string Message, UpdateDownloadChoice Method = UpdateDownloadChoice.Full,
    long VerifiedBytes = 0)
{
    internal long BytesRemaining => Math.Max(0, TotalBytes - ReceivedBytes);
    internal int Percent => TotalBytes <= 0 ? 0 : (int)Math.Clamp(100d * ReceivedBytes / TotalBytes,
        0, Phase == UpdateDownloadPhase.Downloading && VerifiedBytes < TotalBytes ? 99 : 100);
}

internal sealed class UpdateGitCommandException : Exception
{
    internal string Reason { get; }
    internal bool IsRetryable { get; }
    internal UpdateGitCommandException(string reason, bool isRetryable = true) : base(reason)
        => (Reason, IsRetryable) = (reason, isRetryable);
}

internal static class UpdateRetry
{
    internal const int MaximumAttempts = 5;
    internal static async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken token,
        Action<int, TimeSpan>? retrying = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        for (int attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return await operation(); }
            catch (UpdateGitCommandException error)
            {
                token.ThrowIfCancellationRequested();
                if (!error.IsRetryable || attempt >= MaximumAttempts) throw;
                var wait = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                retrying?.Invoke(attempt + 1, wait);
                await (delay ?? Task.Delay)(wait, token);
            }
        }
    }
}

// Serializes progress from Git's stderr reader and the half-second heartbeat.
// Speed uses reported network bytes, never the later local Git blob copy.
internal sealed class UpdateDownloadMeter
{
    private readonly object sync = new();
    private readonly Func<TimeSpan> clock;
    private readonly IProgress<UpdateDownloadProgress>? progress;
    private long total, completed, inFlight, network, attemptBytes, reused, partLength;
    private int part, count, attempt;
    private TimeSpan lastNetwork, retryUntil;
    private UpdateDownloadPhase phase;
    private UpdateDownloadChoice method;
    private string message = "Connecting to the update repository…";

    internal UpdateDownloadMeter(IProgress<UpdateDownloadProgress>? progress, Func<TimeSpan>? clock = null)
    {
        var watch = Stopwatch.StartNew();
        this.clock = clock ?? (() => watch.Elapsed);
        this.progress = progress;
    }
    internal void Begin(UpdateArchive archive, UpdateDownloadChoice downloadMethod = UpdateDownloadChoice.Full)
    {
        lock (sync) { total = archive.Length; completed = inFlight = reused = 0; count = archive.Parts.Count; method = downloadMethod; }
    }
    internal void StartPart(int number, long length, int nextAttempt)
    {
        lock (sync)
        {
            part = number; partLength = length; attempt = nextAttempt; inFlight = attemptBytes = 0;
            phase = UpdateDownloadPhase.Downloading;
            message = $"Downloading {method.ToString().ToLowerInvariant()} update · part {part} of {count}";
        }
        Emit();
    }
    internal void NetworkBytes(long bytes)
    {
        lock (sync)
        {
            if (bytes < attemptBytes) return;
            network += bytes - attemptBytes; attemptBytes = bytes;
            inFlight = Math.Min(partLength, bytes); lastNetwork = clock();
        }
        Emit();
    }
    internal void Retry(int nextAttempt, TimeSpan delay)
    {
        lock (sync)
        {
            inFlight = 0; attempt = nextAttempt; phase = UpdateDownloadPhase.Retrying;
            retryUntil = clock() + delay;
        }
        Emit();
    }
    internal void CompletePart(long length, bool fromCache)
    {
        lock (sync) { completed += length; inFlight = 0; if (fromCache) reused += length; }
        Emit();
    }
    internal void SetPhase(UpdateDownloadPhase value, string text)
    {
        lock (sync) { phase = value; message = text; }
        Emit();
    }
    internal void Fail()
    {
        lock (sync) { inFlight = 0; phase = UpdateDownloadPhase.Failed; message = "Download stopped. Verified chunks are saved for retry."; }
        Emit();
    }
    internal UpdateDownloadProgress Snapshot()
    {
        lock (sync)
        {
            TimeSpan elapsed = clock();
            bool moving = (phase == UpdateDownloadPhase.Downloading && elapsed - lastNetwork < TimeSpan.FromSeconds(5)) ||
                phase == UpdateDownloadPhase.Ready;
            double speed = moving ? TransferMetrics.AverageSpeed(network, elapsed) : 0;
            long received = Math.Min(total, completed + inFlight);
            TimeSpan? remaining = phase == UpdateDownloadPhase.Downloading
                ? TransferMetrics.EstimateRemaining(total - received, speed) : null;
            string text = phase == UpdateDownloadPhase.Retrying
                ? $"Connection lost. Retrying in {Math.Max(0, (int)Math.Ceiling((retryUntil - elapsed).TotalSeconds))}s " +
                  $"(attempt {attempt}/{UpdateRetry.MaximumAttempts})…" : message;
            return new(phase, received, total, speed, elapsed, remaining, part, count, attempt, reused, text, method, completed);
        }
    }
    internal void Emit() => progress?.Report(Snapshot());
}

internal sealed class UpdateInlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

internal static class UpdatePartDownload
{
    internal static async Task FetchAsync(IUpdateArtifactSource source, UpdateArchive archive, string version,
        string partDirectory, string cacheRoot, UpdateDownloadMeter meter, CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null, UpdateDownloadChoice method = UpdateDownloadChoice.Full)
    {
        // Both the manifest and archive digest have already passed strict validation.
        UpdateManifestPolicy.ValidateArchive(archive);
        if (!UpdateManifestPolicy.IsPublishedVersion(version)) throw new InvalidDataException("Invalid download version.");
        string cache = Path.Combine(cacheRoot, archive.Sha256.ToLowerInvariant());
        RejectLinks(cache);
        Directory.CreateDirectory(cache);
        meter.Begin(archive, method);
        for (int index = 0; index < archive.Parts.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var part = archive.Parts[index];
            string saved = Path.Combine(cache, part.Name);
            RejectLinks(saved);
            bool cached = Valid(saved, part);
            if (!cached)
            {
                if (File.Exists(saved)) File.Delete(saved);
                string incoming = saved + "." + Guid.NewGuid().ToString("N") + ".incoming";
                int attempt = 0;
                try
                {
                    async Task<bool> Transfer()
                    {
                        if (File.Exists(incoming)) File.Delete(incoming);
                        meter.StartPart(index + 1, part.Length, ++attempt);
                        await source.FetchPartAsync("versions/" + version + "/" + part.Name,
                            incoming, part.Length, token, new UpdateInlineProgress<long>(meter.NetworkBytes));
                        return true;
                    }
                    var transfer = UpdateRetry.RunAsync(Transfer, token, meter.Retry, retryDelay);
                    while (!transfer.IsCompleted)
                    {
                        await Task.WhenAny(transfer, Task.Delay(500, token));
                        if (token.IsCancellationRequested) break;
                        meter.Emit();
                    }
                    await transfer; // Always observe completion/cleanup before cancelling staging.
                    token.ThrowIfCancellationRequested();
                    meter.SetPhase(UpdateDownloadPhase.Verifying, $"Verifying part {index + 1} of {archive.Parts.Count}…");
                    if (!Valid(incoming, part)) throw new InvalidDataException("Downloaded part failed SHA-256 verification.");
                    // Concurrent readers can share verified chunks; never overwrite a winner.
                    try { File.Move(incoming, saved); }
                    catch (IOException) when (Valid(saved, part)) { }
                }
                catch { meter.Fail(); throw; }
                finally { if (File.Exists(incoming)) File.Delete(incoming); }
            }
            else
            {
                meter.StartPart(index + 1, part.Length, 0);
                meter.SetPhase(UpdateDownloadPhase.Downloading, $"Reusing verified part {index + 1} of {archive.Parts.Count}…");
            }
            File.Copy(saved, Path.Combine(partDirectory, part.Name));
            meter.CompletePart(part.Length, cached);
        }
    }

    private static bool Valid(string path, UpdatePart descriptor) => File.Exists(path) &&
        !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) &&
        new FileInfo(path).Length == descriptor.Length &&
        StringComparer.OrdinalIgnoreCase.Equals(UpdateChunks.Sha256(path), descriptor.Sha256);

    internal static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Links are forbidden in the update cache.");
    }
}
