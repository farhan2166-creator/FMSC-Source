using System.Globalization;

namespace FMSC_SMS.Core;

/// <summary>Read-only receipt selection and factual cash/advance descriptions for the common fee composer.</summary>
internal static class FeeMessageReceiptSource
{
    // Credit-only settlements have official receipts too. Keep individual copies
    // scoped to that child and never share a family receipt outside its saved family.
    internal const string LatestReceiptSql = """
            EXEC dbo.RequirePermission @UserId,N'Fees.View';
            SELECT TOP(1) R.ReceiptNumber
            FROM dbo.CollectionReceipts R
            WHERE R.PaymentMethod<>N'Fee changes'
                AND EXISTS(SELECT 1 FROM dbo.CollectionReceiptPayments L JOIN dbo.FeePayments P ON P.FeePaymentId=L.FeePaymentId
                    WHERE L.CollectionReceiptId=R.CollectionReceiptId AND P.Status=N'Completed' AND P.TotalAmount>0
                        AND (@Family=1 OR P.AdmissionId=@AdmissionId))
                AND ((@Family=0 AND EXISTS(SELECT 1 FROM OPENJSON(R.SnapshotJson,'$.Students') WITH(AdmissionId int) S
                            WHERE S.AdmissionId=@AdmissionId))
                    OR (@Family=1 AND (SELECT COUNT(*) FROM OPENJSON(R.SnapshotJson,'$.Students'))>1
                        AND NOT EXISTS(SELECT AdmissionId FROM OPENJSON(R.SnapshotJson,'$.Students') WITH(AdmissionId int)
                            EXCEPT SELECT CONVERT(int,[value]) FROM OPENJSON(@FamilyIds))))
            ORDER BY R.VisitSequence DESC,R.ReceiptNumber DESC;

        """;

    internal static string PaymentDetails(decimal cash,decimal credit,string language)
    {
        if(cash<0 || credit<0 || cash+credit<=0)
            throw new CommunicationValidationException("Review the saved receipt's payment details before sharing it.");
        if(language is not ("en" or "ur"))
            throw new CommunicationValidationException("Choose English or Urdu for the receipt message.");
        var parts=new List<string>();
        if(cash>0)parts.Add((language=="ur" ? "موصولہ رقم: PKR " : "Amount received: PKR ")+cash.ToString("N2",CultureInfo.InvariantCulture));
        if(credit>0)parts.Add((language=="ur" ? "استعمال شدہ پیشگی رقم: PKR " : "Credit applied: PKR ")+credit.ToString("N2",CultureInfo.InvariantCulture));
        return string.Join(language=="ur" ? "؛ " : "; ",parts)+".";
    }
}
