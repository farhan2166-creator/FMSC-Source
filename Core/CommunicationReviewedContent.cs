using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FMSC_SMS.Core;

/// <summary>Stable review evidence for the actual rendered message and scoped fee membership.</summary>
internal static class CommunicationReviewedContent
{
    internal sealed record FeeMember(int AdmissionId,decimal Outstanding);
    internal sealed record Reviewed(string Kind,int RecordId,string Phone,string Hash,string Text,FeeMember[] FeeMembers);

    internal static string ReviewJson(CommunicationTarget target,CommunicationPreview preview,string body)
    {
        CommunicationPreparedMessages.ValidateFormat(body,target.Purpose);
        return JsonSerializer.Serialize(preview.Entries.Where(e=>e.Included)
            .OrderBy(e=>e.Kind,StringComparer.Ordinal).ThenBy(e=>e.RecordId).Select(e=>new Reviewed(
                e.Kind,e.RecordId,e.PhoneE164!,Signature(target,preview,e,body),
                CommunicationPreparedMessages.Render(body,CommunicationPreparedMessages.FromPreview(e)),
                target.Audience!=CommunicationAudience.OutstandingFees ? Array.Empty<FeeMember>() : preview.Entries
                    .Where(m=>m.EligiblePhone==e.PhoneE164).OrderBy(m=>m.RecordId)
                    .Select(m=>new FeeMember(m.RecordId,m.Outstanding ?? 0)).ToArray())).ToArray());
    }
    internal static string Signature(CommunicationTarget target,CommunicationPreview preview,
        CommunicationPreviewEntry entry,string body)
    {
        string rendered=CommunicationPreparedMessages.Render(body,CommunicationPreparedMessages.FromPreview(entry));
        // Fee membership matters even when the grand total has not changed.
        // Never silently substitute another child sharing a guardian number.
        string members=target.Audience!=CommunicationAudience.OutstandingFees ? "" : JsonSerializer.Serialize(
            preview.Entries.Where(e=>e.EligiblePhone==entry.PhoneE164).OrderBy(e=>e.RecordId)
            .Select(e=>new {e.RecordId,e.RecordName,e.AdmissionNumber,e.Outstanding}));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            entry.Kind,entry.RecordId,entry.PhoneE164,rendered,members
        }))));
    }
}
