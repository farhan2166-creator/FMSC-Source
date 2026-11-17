using System.Security.Cryptography;
using FMSC_SMS.Core;

internal static class AttemptJournalChecks
{
    internal static void Run(Action<bool,string> check)
    {
        string root=Path.Combine(Path.GetTempPath(),"Fmsc-Synthetic-Communication-"+Guid.NewGuid().ToString("N"));
        byte[] key=RandomNumberGenerator.GetBytes(32);
        byte[] Seal(byte[] clear)
        {
            byte[] nonce=RandomNumberGenerator.GetBytes(12),tag=new byte[16],cipher=new byte[clear.Length];
            using var aes=new AesGcm(key,16);aes.Encrypt(nonce,clear,cipher,tag);
            return nonce.Concat(tag).Concat(cipher).ToArray();
        }
        byte[] Open(byte[] cipher)
        {
            byte[] clear=new byte[cipher.Length-28];
            using var aes=new AesGcm(key,16);aes.Decrypt(cipher.AsSpan(0,12),cipher.AsSpan(28),cipher.AsSpan(12,16),clear);return clear;
        }
        void Refused(Action action,string reason)
        {
            bool refused=false;
            try{action();}catch(Exception ex) when(ex is IOException or InvalidDataException or InvalidOperationException or CryptographicException){refused=true;}
            check(refused,reason);
        }
        try
        {
            var identity=new CommunicationAttemptJournal.Identity(new string('A',64),Guid.NewGuid(),"Student",42,
                CommunicationAttemptJournal.Hash("+923001234567"),"Sms","Twilio",new string('B',64));
            var attempt=new CommunicationAttemptJournal.Attempt(17,Guid.NewGuid(),"Sms","Twilio");
            var journal=new CommunicationAttemptJournal(root,Seal,Open);
            journal.AssertConsistent(identity,Array.Empty<CommunicationAttemptJournal.Attempt>());
            check(!Directory.Exists(root),"Checking an untouched message does not manufacture submission evidence");
            var claim=journal.RecordClaim(identity,attempt);
            check(Directory.GetFiles(root,"*.claim.dpapi",SearchOption.AllDirectories).Length==1,
                "One durable submission proof exists before any provider operation");
            journal.AssertConsistent(identity,new[]{attempt});
            check(journal.RecordClaim(identity,attempt)==claim,
                "Exact claim recovery preserves the original pre-HTTP evidence rather than rewriting it");
            Refused(()=>journal.AssertConsistent(identity,Array.Empty<CommunicationAttemptJournal.Attempt>()),
                "A database restored before submission cannot treat the retained message as unattempted");
            Refused(()=>journal.AssertConsistent(identity,new[]{attempt with {AttemptKey=Guid.NewGuid()}}),
                "Reused numeric attempt IDs cannot conceal missing submission keys");
            Refused(()=>journal.AssertConsistent(identity,new[]{attempt with {AttemptId=18}}),
                "A matching key with a different SQL attempt identity is held for review");
            var actorIdentity=identity with {RequestId=Guid.NewGuid()};
            var actorAttempt=attempt with {AttemptId=19,AttemptKey=Guid.NewGuid(),ClaimedBy=17,ClaimKind="Worker",WorkerEnrollmentId=Guid.NewGuid()};
            journal.RecordClaim(actorIdentity,actorAttempt);
            journal.AssertConsistent(actorIdentity,new[]{actorAttempt});
            Refused(()=>journal.AssertConsistent(actorIdentity,new[]{actorAttempt with {ClaimedBy=18}}),
                "Retained worker identity cannot be silently reassigned while keeping the same SQL attempt key");
            Refused(()=>journal.AssertConsistent(actorIdentity,new[]{actorAttempt with {WorkerEnrollmentId=Guid.NewGuid()}}),
                "Retained worker enrollment distinguishes the service invocation from a later authorization");
            var restarted=new CommunicationAttemptJournal(root,Seal,Open);
            restarted.AssertConsistent(identity,new[]{attempt});
            check(restarted.ReadResult(identity,attempt.AttemptKey) is null,
                "A restarted process distinguishes an unknown submission from a retained provider result");
            restarted.RecordResult(claim,"Accepted","SM-synthetic-provider-reference");
            restarted.RecordResult(claim,"Accepted","SM-synthetic-provider-reference");
            var result=journal.ReadResult(identity,attempt.AttemptKey);
            check(result?.State=="Accepted" && result.ProviderMessageId=="SM-synthetic-provider-reference",
                "The provider reference survives a lost SQL acknowledgment without claiming delivery");
            Refused(()=>journal.RecordResult(claim,"Delivered","SM-synthetic-provider-reference"),
                "Later reconciliation cannot silently rewrite the original provider response");
            Refused(()=>journal.RecordResult(claim,"Accepted","SM-other"),
                "One attempt cannot acquire a different retained provider reference");
            var retry=attempt with {AttemptId=18,AttemptKey=Guid.NewGuid()};
            journal.RecordClaim(identity,retry);
            restarted.AssertConsistent(identity,new[]{attempt,retry});
            check(Directory.GetFiles(root,"*.claim.dpapi",SearchOption.AllDirectories).Length==3,
                "An independently authorized SQL retry adds distinct evidence and preserves its predecessor");
            Refused(()=>restarted.AssertConsistent(identity,new[]{retry}),
                "A larger SQL watermark cannot hide the disappearance of an earlier submission");
            // Only abandoned atomic writes are disposable. Durable claims,
            // provider results and unrelated files must survive cleanup.
            string evidenceFolder=Path.GetDirectoryName(
                Directory.GetFiles(root,attempt.AttemptKey.ToString("N")+".claim.dpapi",SearchOption.AllDirectories).Single())!;
            string temporaryKey=Guid.NewGuid().ToString("N");
            string stale=Path.Combine(evidenceFolder,temporaryKey+".claim.dpapi."+Guid.NewGuid().ToString("N")+".tmp");
            string recent=Path.Combine(evidenceFolder,temporaryKey+".result.dpapi."+Guid.NewGuid().ToString("N")+".tmp");
            string unrelated=Path.Combine(evidenceFolder,"unrelated.tmp");
            File.WriteAllBytes(stale,new byte[]{1,2,3});
            File.WriteAllBytes(recent,new byte[]{4,5,6});
            File.WriteAllBytes(unrelated,new byte[]{7});
            File.SetLastWriteTimeUtc(stale,DateTime.UtcNow.AddDays(-4));
            // More than sixteen abandoned writes must not strand old staging
            // files forever when this recipient has no remaining retry slots.
            var extraStale = new List<string>();
            for(int index=0;index<20;index++)
            {
                string item=Path.Combine(evidenceFolder,Guid.NewGuid().ToString("N")+
                    ".claim.dpapi."+Guid.NewGuid().ToString("N")+".tmp");
                File.WriteAllBytes(item,new byte[]{1,2,3});
                File.SetLastWriteTimeUtc(item,DateTime.UtcNow.AddDays(-4));
                extraStale.Add(item);
            }
            File.SetLastWriteTimeUtc(unrelated,DateTime.UtcNow.AddDays(-4));
            journal.RecordClaim(identity,retry);
            check(!File.Exists(stale) && extraStale.All(item=>!File.Exists(item)) && File.Exists(recent) && File.Exists(unrelated),
                "Cleanup removes only expired, correctly named atomic staging files");
            journal.AssertConsistent(identity,new[]{attempt,retry});
            check(journal.ReadResult(identity,attempt.AttemptKey)?.ProviderMessageId=="SM-synthetic-provider-reference",
                "Cleanup cannot delete durable claims or protected provider responses");
            File.Delete(recent);
            File.Delete(unrelated);
            journal.AssertConsistent(identity with {RequestId=Guid.NewGuid()},Array.Empty<CommunicationAttemptJournal.Attempt>());
            journal.AssertConsistent(identity with {SchoolScope=new string('C',64)},Array.Empty<CommunicationAttemptJournal.Attempt>());
            check(true,"Separate reviewed messages and schools do not inherit one another's submission evidence");
            check(!Directory.GetFiles(root,"*",SearchOption.AllDirectories).Any(file=>
                System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains("SM-synthetic-provider-reference",StringComparison.Ordinal)),
                "Retained references are protected on disk rather than written as plaintext metadata");
            var inaccessible=new CommunicationAttemptJournal(root,Seal,_=>throw new CryptographicException("Synthetic different account"));
            Refused(()=>inaccessible.AssertConsistent(identity,new[]{attempt,retry}),
                "Unreadable account-protected evidence fails closed instead of being treated as absent");
            string proof=Directory.GetFiles(root,attempt.AttemptKey.ToString("N")+".claim.dpapi",SearchOption.AllDirectories).Single();
            File.WriteAllBytes(proof,new byte[]{1,2,3});
            Refused(()=>journal.AssertConsistent(identity,new[]{attempt,retry}),
                "Corrupted durable evidence cannot authorize a new provider request");
            var cannotWrite=new CommunicationAttemptJournal(Path.Combine(root,"failed-write"),_=>throw new IOException("Synthetic full disk"),Open);
            Refused(()=>cannotWrite.RecordClaim(identity,attempt),
                "Failure to persist the pre-HTTP journal prevents submission");
            check(!Directory.GetFiles(root,"*.tmp",SearchOption.AllDirectories).Any(),
                "Failed writes leave no active partial recovery record");
        }
        finally{CryptographicOperations.ZeroMemory(key);if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
