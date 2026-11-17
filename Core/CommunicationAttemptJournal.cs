using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FMSC_SMS.Core;

/// <summary>Independent pre-HTTP evidence. SQL restores cannot turn a retained
/// submission into an unattempted message, even when numeric IDs are reused.</summary>
internal sealed class CommunicationAttemptJournal
{
    internal sealed record Identity(string SchoolScope,Guid RequestId,string Kind,int RecordId,
        string DestinationHash,string Channel,string ProviderCode,string ProfileHash);
    internal sealed record Attempt(long AttemptId,Guid AttemptKey,string Channel,string ProviderCode);
    internal sealed record Claim(Identity Identity,Attempt Attempt,DateTime RecordedUtc);
    internal sealed record Result(Guid AttemptKey,string State,string? ProviderMessageId,DateTime RecordedUtc);
    private readonly string root;
    private readonly Func<byte[],byte[]> protect,unprotect;

    internal CommunicationAttemptJournal(string root,Func<byte[],byte[]> protect,Func<byte[],byte[]> unprotect)
    {
        this.root=Path.GetFullPath(root);
        RejectLinks(this.root);
        this.protect=protect;
        this.unprotect=unprotect;
    }

    internal static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool IsHash(string? value)=>value is {Length:64} && value.All(char.IsAsciiHexDigit);
    private string Folder(Identity identity)
    {
        if(!IsHash(identity.SchoolScope) || identity.RequestId==Guid.Empty || identity.RecordId<=0
            || identity.Kind is not ("Student" or "Staff") || !IsHash(identity.DestinationHash)
            || !IsHash(identity.ProfileHash) || !((identity.Channel is "Sms" or "WhatsApp" && identity.ProviderCode=="Twilio")
                || (identity.Channel=="WhatsApp" && identity.ProviderCode=="MetaCloud")))
            throw new InvalidDataException("Invalid communication recovery identity.");
        string target=Hash(JsonSerializer.Serialize(identity));
        // Keep paths usable by Windows recovery tools; full identities remain
        // inside authenticated evidence, so any digest collision fails closed.
        return Path.Combine(root,identity.SchoolScope[..32],target[..32]);
    }
    private static void Validate(Claim claim)
    {
        if(claim.Identity is null || claim.Attempt is null || claim.Attempt.AttemptId<=0 || claim.Attempt.AttemptKey==Guid.Empty
            || claim.Attempt.Channel!=claim.Identity.Channel || claim.Attempt.ProviderCode!=claim.Identity.ProviderCode
            || claim.RecordedUtc.Kind!=DateTimeKind.Utc)
            throw new InvalidDataException("Invalid communication submission evidence.");
    }

    internal void AssertConsistent(Identity identity,IReadOnlyCollection<Attempt> databaseAttempts)
    {
        string folder=Folder(identity);
        if(!Directory.Exists(folder))return;
        RejectLinks(folder);
        // SQL allows at most three explicitly approved attempts per recipient.
        // Extra or malformed evidence is held for review, never discarded.
        var files=Directory.EnumerateFiles(folder,"*.claim.dpapi").Take(4).ToArray();
        if(files.Length>3)throw new InvalidDataException("Unexpected communication recovery evidence. Keep it for administrator review.");
        var keys=new HashSet<Guid>();
        foreach(string path in files)
        {
            var claim=Read<Claim>(path);Validate(claim);
            if(claim.Identity!=identity || Path.GetFileName(path)!=claim.Attempt.AttemptKey.ToString("N")+".claim.dpapi"
                || !keys.Add(claim.Attempt.AttemptKey) || !databaseAttempts.Contains(claim.Attempt))
                throw new CommunicationValidationException("Saved submission evidence differs from this database. Sending is held; reconcile the restored message before trying again.");
            string resultPath=Path.Combine(folder,claim.Attempt.AttemptKey.ToString("N")+".result.dpapi");
            if(File.Exists(resultPath))ValidateResult(Read<Result>(resultPath),claim.Attempt.AttemptKey);
        }
        foreach(string result in Directory.EnumerateFiles(folder,"*.result.dpapi").Take(4))
        {
            string key=Path.GetFileName(result).Replace(".result.dpapi","",StringComparison.Ordinal);
            if(!Guid.TryParseExact(key,"N",out Guid id) || !keys.Contains(id))
                throw new InvalidDataException("Submission outcome has no matching recovery claim. Keep it for administrator review.");
        }
    }

    internal Claim RecordClaim(Identity identity,Attempt attempt)
    {
        var claim=new Claim(identity,attempt,DateTime.UtcNow);Validate(claim);
        string folder=Folder(identity);Directory.CreateDirectory(folder);RejectLinks(folder);
        string path=Path.Combine(folder,attempt.AttemptKey.ToString("N")+".claim.dpapi");
        if(File.Exists(path))
        {
            var prior=Read<Claim>(path);Validate(prior);
            if(prior.Identity!=identity || prior.Attempt!=attempt)
                throw new InvalidDataException("The submission key already has different recovery evidence.");
            return prior;
        }
        WriteNew(path,claim);
        return claim;
    }

    internal void RecordResult(Claim claim,string state,string? providerMessageId)
    {
        Validate(claim);
        string folder=Folder(claim.Identity),key=claim.Attempt.AttemptKey.ToString("N");
        var saved=Read<Claim>(Path.Combine(folder,key+".claim.dpapi"));
        if(saved!=claim)throw new InvalidDataException("The durable submission claim changed.");
        var result=new Result(claim.Attempt.AttemptKey,state,providerMessageId,DateTime.UtcNow);
        ValidateResult(result,claim.Attempt.AttemptKey);
        string path=Path.Combine(folder,key+".result.dpapi");
        if(File.Exists(path))
        {
            var prior=Read<Result>(path);ValidateResult(prior,claim.Attempt.AttemptKey);
            if(prior.State!=state || prior.ProviderMessageId!=providerMessageId)
                throw new InvalidDataException("The original provider response cannot be overwritten.");
            return;
        }
        WriteNew(path,result);
    }

    internal Result? ReadResult(Identity identity,Guid attemptKey)
    {
        string path=Path.Combine(Folder(identity),attemptKey.ToString("N")+".result.dpapi");
        if(!File.Exists(path))return null;
        var result=Read<Result>(path);ValidateResult(result,attemptKey);return result;
    }
    private static void ValidateResult(Result result,Guid key)
    {
        if(key==Guid.Empty || result.AttemptKey!=key || result.RecordedUtc.Kind!=DateTimeKind.Utc
            || result.State is not ("Unknown" or "Failed" or "Submitted" or "Accepted" or "Delivered")
            || (result.State is "Submitted" or "Accepted" or "Delivered" && string.IsNullOrWhiteSpace(result.ProviderMessageId))
            || result.ProviderMessageId?.Length>150 || result.ProviderMessageId?.Any(char.IsControl)==true)
            throw new InvalidDataException("Invalid retained provider outcome.");
    }
    private T Read<T>(string path)
    {
        RejectLinks(path);
        var info=new FileInfo(path);
        if(!info.Exists || info.Length is < 32 or > 16384)
            throw new InvalidDataException("Communication recovery evidence is missing or damaged. Do not resend it.");
        byte[] sealedBytes=File.ReadAllBytes(path),clear=Array.Empty<byte>();
        try
        {
            clear=unprotect(sealedBytes);
            return JsonSerializer.Deserialize<T>(clear) ?? throw new InvalidDataException("Empty communication recovery evidence.");
        }
        catch(JsonException ex){throw new InvalidDataException("Damaged communication recovery evidence. Do not resend it.",ex);}
        finally{CryptographicOperations.ZeroMemory(clear);CryptographicOperations.ZeroMemory(sealedBytes);}
    }
    private void WriteNew<T>(string path,T value)
    {
        RejectLinks(Path.GetDirectoryName(path)!);
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        byte[] clear=Array.Empty<byte>(),sealedBytes=Array.Empty<byte>();
        try
        {
            clear=JsonSerializer.SerializeToUtf8Bytes(value);sealedBytes=protect(clear);
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {stream.Write(sealedBytes);stream.Flush(true);}
            File.Move(temp,path,overwrite:false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);CryptographicOperations.ZeroMemory(sealedBytes);
            if(File.Exists(temp))File.Delete(temp);
        }
    }
    private static void RejectLinks(string path)
    {
        FileSystemInfo? entry=Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        while(entry is not null)
        {
            if(entry.Exists && (entry.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new IOException("Communication recovery paths cannot be redirected.");
            entry=entry is DirectoryInfo directory ? directory.Parent : ((FileInfo)entry).Directory;
        }
    }
}
