using System.Data;

namespace FMSC_SMS.Pages.Reports.Certificates;

internal static class CertificateIssueRegisterData
{
    internal static readonly string[] GeneralTypes = {
        "Character", "Date of Birth", "Regular Student", "Sports", "No Objection", "Provisional",
        "Regular Teacher / Lecturer", "Bank Account Opening"
    };
    internal static readonly string[] LeavingTypes = { "School Leaving", "College Leaving" };

    // Only these fixed source descriptors build SQL. Operator-entered values are parameters.
    // Existing record-list procedures require Certificates.Create, or Students.Leave for leaving records.
    internal static string BuildQuery(bool general,bool leaving)
    {
        if (!general && !leaving) throw new UnauthorizedAccessException("Certificate access is required to view this register.");
        var parts=new List<string>();
        if (general)
        {
            parts.Add(Source("CharacterCertificates","CertificateRecordId","N'Character'",false,"CertificateDate","CharacterCertificateIssues","CertificateRecordId"));
            parts.Add(Source("DateOfBirthCertificates","CertificateRecordId","N'Date of Birth'",false,"CertificateDate","DateOfBirthCertificateIssues","CertificateRecordId"));
            parts.Add(Source("RegularStudentCertificates","RegularStudentCertificateId","N'Regular Student'",false,"CertificateDate","RegularStudentCertificateIssues","RegularStudentCertificateId"));
            parts.Add(Source("SportsCertificates","SportsCertificateId","N'Sports'",false));
            parts.Add(Source("NoObjectionCertificates","NoObjectionCertificateId","N'No Objection'",false));
            parts.Add(Source("ProvisionalCertificates","ProvisionalCertificateId","N'Provisional'",false));
            parts.Add(Source("RegularLecturerCertificates","RegularLecturerCertificateId","N'Regular Teacher / Lecturer'",true));
            parts.Add(Source("BankAccountOpeningCertificates","BankAccountOpeningCertificateId","N'Bank Account Opening'",true));
        }
        if (leaving) parts.Add(Source("LeaveCertificates","LeaveRecordId",
            "CASE WHEN C.CertificateType=N'College' THEN N'College Leaving' ELSE N'School Leaving' END",
            false,"LeaveDate","LeaveCertificateIssues","LeaveRecordId",true));
        return "EXEC dbo.RequirePermission @UserId,N'Reports.View';\n"
            +(general ? "EXEC dbo.RequirePermission @UserId,N'Certificates.Create';\n" : "")
            +(leaving ? "EXEC dbo.RequirePermission @UserId,N'Students.Leave';\n" : "")
            +";WITH Register AS (\n"+string.Join("\nUNION ALL\n",parts)+"\n)\n"
            +"""
            SELECT R.ActivityAt [Recorded On],R.CertificateType [Certificate Type],R.CertificateNumber [Certificate No.],
                R.RecipientType [Recipient Type],R.Recipient [Recipient],R.PersonReference [Student / Staff No.],
                R.CertificateDate [Certificate Date],R.EntryKind [Entry],R.IssueType [Issue Type],
                COALESCE(U.Username,N'') [Recorded By],R.CollectionStatus [Collection],
                R.CollectedAt [Collected On],R.CollectedBy [Collected By]
            FROM Register R LEFT JOIN dbo.Users U ON U.UserId=R.RecordedBy
            WHERE (@FromDate IS NULL OR CONVERT(date,R.ActivityAt)>=@FromDate)
              AND (@ToDate IS NULL OR CONVERT(date,R.ActivityAt)<=@ToDate)
              AND (@CertificateType IS NULL OR R.CertificateType=@CertificateType)
              AND (@RecipientType IS NULL OR R.RecipientType=@RecipientType)
              AND (@EntryKind IS NULL OR R.EntryKind=@EntryKind)
              AND (@CollectionStatus IS NULL OR R.CollectionStatus=@CollectionStatus)
              AND (@Search=N'' OR CHARINDEX(@Search,R.Recipient)>0 OR CHARINDEX(@Search,R.PersonReference)>0
                  OR CHARINDEX(@Search,R.CertificateNumber)>0)
            ORDER BY R.ActivityAt DESC,R.CertificateType,R.RecordId DESC,R.IssueId DESC;
            """;
    }

    private static string Source(string table,string key,string type,bool staff,string date="CertificateDate",
        string? issues=null,string? issueKey=null,bool leave=false)
    {
        bool tracked=issues is not null;
        bool character=table=="CharacterCertificates";
        bool regular=table=="RegularStudentCertificates";
        bool birth=table=="DateOfBirthCertificates";
        string content=character||leave||birth?"P":regular?"I":"C";
        // Sports/NOC/Provisional track each copy and collection on the source
        // row. Staff certificates have no collection workflow.
        bool rowCopy=!tracked && !staff;
        string collectionSource=tracked ? "I" : "C";
        string hasIssue=tracked ? "I.IssuedAt IS NOT NULL" : rowCopy ? "1=1" : "1=0";
        string activity=tracked ? "COALESCE(I.IssuedAt,C.CreatedAt)" : "C.CreatedAt";
        string recordedBy=tracked ? "COALESCE(I.IssuedByUserId,C.CreatedByUserId)" : "C.CreatedByUserId";
        string collection=tracked || rowCopy
            ? $"CASE WHEN NOT ({hasIssue}) THEN N'Not tracked' WHEN {collectionSource}.CollectedAt IS NULL THEN N'Not Collected' ELSE N'Collected' END"
            : "N'Not tracked'";
        string collectedAt=tracked || rowCopy ? $"{collectionSource}.CollectedAt" : "CAST(NULL AS datetime2)";
        string collectedBy=tracked || rowCopy ? $"{collectionSource}.CollectedBy" : "CAST(NULL AS nvarchar(200))";
        string issueId=tracked ? regular?"I.RegularStudentCertificateIssueId":$"I.{(leave ? "Leave" : character ? "Character" : "DateOfBirth")}CertificateIssueId" : "CAST(NULL AS int)";
        return $"""
            SELECT {type} CertificateType,C.{key} RecordId,{issueId} IssueId,
                COALESCE(NULLIF(C.CertificateNumber,N''),CONCAT(N'Record #',C.{key})) CertificateNumber,
                N'{(staff ? "Staff" : "Student")}' RecipientType,{content}.{(staff ? "StaffName" : "StudentName")} Recipient,
                {(staff ? "CONCAT(N'Staff #',C.StaffId)" : "COALESCE(S.AdmissionNumber,S.QuickReference,CONCAT(N'ID-',C.AdmissionId))")} PersonReference,
                {content}.{date} CertificateDate,{activity} ActivityAt,{recordedBy} RecordedBy,
                CASE WHEN {hasIssue} THEN N'Issue logged' ELSE N'Saved record only' END EntryKind,
                {(leave ? "COALESCE(I.IssueType,N'')" : character ? "CONCAT(COALESCE(P.ExamType,N'Type not recorded'),CASE WHEN P.CorrectionReason IS NULL THEN N'' ELSE N' / Correction' END)" : "CAST(N'' AS nvarchar(50))")} IssueType,
                {collection} CollectionStatus,
                {collectedAt} CollectedAt,
                {collectedBy} CollectedBy
            FROM dbo.{table} C
            {(tracked ? $"LEFT JOIN dbo.{issues} I ON I.{issueKey}=C.{key}" : "")}
            {(character ? "LEFT JOIN dbo.CharacterCertificateIssueSnapshots P ON P.CharacterCertificateIssueId=I.CharacterCertificateIssueId" : "")}
            {(leave ? "LEFT JOIN dbo.LeaveCertificateIssueSnapshots P ON P.LeaveCertificateIssueId=I.LeaveCertificateIssueId" : "")}
            {(birth ? "LEFT JOIN dbo.DateOfBirthCertificateIssueSnapshots P ON P.DateOfBirthCertificateIssueId=I.DateOfBirthCertificateIssueId" : "")}
            {(staff ? "" : "LEFT JOIN dbo.Students S ON S.AdmissionId=C.AdmissionId")}
            """;
    }

    internal static DataTable Display(DataTable source)
    {
        var rows=new DataTable();
        foreach (DataColumn column in source.Columns)
            rows.Columns.Add(column.ColumnName,column.DataType==typeof(DateTime) ? typeof(string) : column.DataType);
        foreach (DataRow sourceRow in source.Rows)
        {
            var row=rows.NewRow();
            foreach (DataColumn column in source.Columns)
                row[column.ColumnName]=sourceRow[column] is DateTime date
                    ? date.ToString(column.ColumnName=="Certificate Date" ? "dd-MM-yyyy" : "dd-MM-yyyy HH:mm") : sourceRow[column];
            rows.Rows.Add(row);
        }
        return rows;
    }

    internal static DataTable Totals(DataTable rows)
    {
        var totals=new DataTable();
        foreach (string name in new[] { "Logged issues", "Saved records only", "Awaiting collection" }) totals.Columns.Add(name,typeof(int));
        totals.Rows.Add(rows.AsEnumerable().Count(r => Equals(r["Entry"],"Issue logged")),
            rows.AsEnumerable().Count(r => Equals(r["Entry"],"Saved record only")),
            rows.AsEnumerable().Count(r => Equals(r["Entry"],"Issue logged") && Equals(r["Collection"],"Not Collected")));
        return totals;
    }
}
