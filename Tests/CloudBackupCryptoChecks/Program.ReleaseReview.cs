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

        }
        finally { Directory.Delete(root,true); }
        Console.WriteLine("PASS: 12-hour schedule matrix, VC prerequisite repair and exclusive/resumable handoff checks.");
    }

}
