using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FMSC_SMS.Core;
using FMSC_SMS.Services.Backup;

internal static class ReleaseReviewChecks
{
    internal static async Task RunAsync()
    {
        bool usable = false;
        var actions = new List<string>();
        int result = await SqlPrerequisiteRepair.EnsureVcRuntimeAsync(() => usable, action => {
            actions.Add(action); usable = action == "/repair"; return Task.FromResult(0);
        });
        if(result != 0 || !actions.SequenceEqual(new[]{"/install","/repair"}))
            throw new Exception("Damaged VC runtime was not repaired and reverified before LocalDB.");
        actions.Clear();
        await SqlPrerequisiteRepair.EnsureVcRuntimeAsync(() => true, action => { actions.Add(action); return Task.FromResult(0); });
        if(actions.Count != 0)throw new Exception("A healthy VC runtime was reinstalled.");
        foreach(int code in new[]{3010,1641})
            if(await SqlPrerequisiteRepair.EnsureVcRuntimeAsync(() => false,_ => Task.FromResult(code)) != 3010)
                throw new Exception("Prerequisite reboot did not stop SQL setup.");
        bool failed = false;
        try{await SqlPrerequisiteRepair.EnsureVcRuntimeAsync(() => false,_ => Task.FromResult(1603));}
        catch(InvalidOperationException){failed=true;}
        if(!failed)throw new Exception("Failed VC setup did not block SQL repair.");
        // All start times, both daily occurrences, missed runs and exact boundaries.
        for (int hour = 0; hour < 24; hour++)
        for (int minute = 0; minute < 24 * 60; minute += 15)
        {
            var now = new DateTime(2026,11,11).AddMinutes(minute);
            var anchor = new DateTime(2026,11,10,hour,7,0);
            var expected = anchor.AddHours(12 * Math.Floor((now-anchor).TotalHours / 12));
            var actual = CloudBackupSchedulePolicy.MostRecentScheduledTime(
                CloudBackupFrequency.Every12Hours,new TimeOnly(hour,7),DayOfWeek.Friday,now);
            if (actual != expected) throw new Exception($"12-hour due time: {hour}, {now}: {actual} != {expected}");
            if (CloudBackupSchedulePolicy.IsDue(true,CloudBackupFrequency.Every12Hours,
                    new TimeOnly(hour,7),DayOfWeek.Friday,now,expected))
                throw new Exception("An already completed occurrence was repeated.");
        }
        string root = Path.Combine(Path.GetTempPath(),"FMSC-ReleaseReview-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var batch = Guid.NewGuid();
            using (var lease = CommunicationHandoffLease.Acquire(batch,root))
            {
                bool blocked = false;
                await Task.Run(() => {
                    try { using var other = CommunicationHandoffLease.Acquire(batch,root); }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException) { blocked = true; }
                });
                if (!blocked) throw new Exception("Two windows acquired the same handoff batch.");
                using var separate = CommunicationHandoffLease.Acquire(Guid.NewGuid(),root);
            }
            using (var resumed = CommunicationHandoffLease.Acquire(batch,root)) { }

            string package = Path.Combine(root,"encrypted.fixture");
            byte[] bytes = Encoding.UTF8.GetBytes("synthetic ciphertext only");
            await File.WriteAllBytesAsync(package,bytes);
            string md5 = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
            string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var server = new DriveServer(bytes.Length,md5);
            using var http = new HttpClient(server);
            string? journalId = null;
            bool lost = false;
            try { await Upload(); } catch (HttpRequestException) { lost = true; }
            if (!lost || journalId != "reserved-file" || server.Creates != 1)
                throw new Exception("Uncertain upload did not retain its reserved ID.");
            var reconciled = await Upload();
            if (reconciled.RemoteId != journalId || server.Creates != 1 || server.Reservations != 1)
                throw new Exception("A lost successful upload was duplicated on retry.");
            server.WrongChecksum = true;
            bool refused = false;
            try { await Upload(); } catch (IOException) { refused = true; }
            if (!refused || server.Creates != 1) throw new Exception("Different remote bytes were accepted or overwritten.");

            // A partially created zero-byte placeholder is updated in place.
            var partial = new DriveServer(bytes.Length,md5) { IncompleteSha = sha };
            using var partialHttp = new HttpClient(partial);
            try { await CloudBackupTransport.DriveUploadAsync(package,"FMSC-test.fmscbackup",sha,
                DateTime.UtcNow,bytes.Length,"fixture",default,"reserved-file",_ => { },partialHttp); }
            catch(HttpRequestException) { }
            if(partial.Creates != 0 || partial.Patches != 1)
                throw new Exception("An owned incomplete upload was duplicated instead of updated in place.");

            // A failed journal write must prevent files.create entirely.
            var noJournal = new DriveServer(bytes.Length,md5);
            using var otherHttp = new HttpClient(noJournal);
            try {
                await CloudBackupTransport.DriveUploadAsync(package,"FMSC-test.fmscbackup",sha,
                    DateTime.UtcNow,bytes.Length,"fixture",default,null,
                    _ => throw new IOException("injected journal failure"),otherHttp);
            } catch (IOException) { }
            if (noJournal.Creates != 0) throw new Exception("Upload started without durable ID persistence.");

            Task<CloudRemoteBackup> Upload() => CloudBackupTransport.DriveUploadAsync(
                package,"FMSC-test.fmscbackup",sha,DateTime.UtcNow,bytes.Length,"fixture",default,
                journalId,id => journalId=id,http);
        }
        finally { Directory.Delete(root,true); }
        Console.WriteLine("PASS: 12-hour schedule matrix, VC prerequisite repair, exclusive/resumable handoff and Drive lost-receipt/journal/integrity checks.");
    }

    private sealed class DriveServer(long size,string md5) : HttpMessageHandler
    {
        internal int Creates, Reservations, Patches;
        internal bool Completed, WrongChecksum;
        internal string? IncompleteSha;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            string uri = request.RequestUri!.AbsoluteUri;
            if (uri.Contains("generateIds")) { Reservations++; return Json(new { ids=new[]{"reserved-file"} }); }
            if (uri.Contains("uploadType=resumable"))
            {
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                if(request.Method == HttpMethod.Patch)
                {
                    if(payload.RootElement.TryGetProperty("id",out _))throw new Exception("PATCH attempted to change a file ID.");
                    Patches++;
                }
                else
                {
                    if (payload.RootElement.GetProperty("id").GetString() != "reserved-file")
                        throw new Exception("files.create did not reuse its reserved ID.");
                    Creates++;
                }
                var response = Json(new { }); response.Headers.Location=new Uri("https://www.googleapis.com/upload-session"); return response;
            }
            if (uri.EndsWith("upload-session")) { Completed=true; throw new HttpRequestException("lost completion response"); }
            if (uri.Contains("/files/reserved-file"))
                return Completed ? Json(new { id="reserved-file",name="FMSC-test.fmscbackup",size=size.ToString(),md5Checksum=WrongChecksum?"bad":md5 })
                    : IncompleteSha is not null ? Json(new { id="reserved-file",name="FMSC-test.fmscbackup",size="0",appProperties=new { fmscPackageSha256=IncompleteSha } })
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.Contains("/drive/v3/files?")) return Json(new { files=Array.Empty<object>() });
            throw new Exception("Unexpected provider request: "+uri);
        }
        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
            { Content=new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json") };
    }
}
