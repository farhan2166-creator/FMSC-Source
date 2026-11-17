namespace FMSC_SMS.Core;

internal static class CollectionCreditAllocation
{
    // Every loaded charge can use only its owner's advance. The caller supplies
    // charges for the selected students; unselected siblings are never included.
    // No balance is pooled or transferred, even for a single family receipt.
    internal static Dictionary<long,decimal> Build(
        IEnumerable<(long ChargeId,int AdmissionId,decimal Due)> charges,
        IReadOnlyDictionary<int,decimal> balances)
    {
        var remaining=balances
            .ToDictionary(pair=>pair.Key,pair=>Math.Max(0m,pair.Value));
        var result=new Dictionary<long,decimal>();
        foreach(var charge in charges)
        {
            if(charge.ChargeId<=0||!remaining.TryGetValue(charge.AdmissionId,out decimal credit)||credit<=0m)continue;
            decimal take=Math.Min(Math.Max(0m,charge.Due),credit);
            if(take<=0m)continue;
            result[charge.ChargeId]=take;
            remaining[charge.AdmissionId]-=take;
        }
        return result;
    }
}
