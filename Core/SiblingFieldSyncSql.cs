namespace FMSC_SMS.Core;

// Only the selected column is written. Values and reviewed identities remain
// parameters; callers cannot supply an arbitrary SQL identifier.
internal static class SiblingFieldSyncSql
{
    private static string Column(string field) => field switch
    {
        "Address" or "ContactNumber" or "AlternateContactNumber" or "FatherName" or "PreviousSchool" => field,
        _ => throw new ArgumentException("Unsupported sibling field.", nameof(field))
    };

    internal static string Read(string field) => $$"""
        SET NOCOUNT ON;
        EXEC dbo.RequirePermission @UserId,N'Students.View';
        EXEC dbo.RequirePermission @UserId,N'Students.Administer';
        DECLARE @FamilyId int=(SELECT FamilyId FROM dbo.FamilyMembers WHERE AdmissionId=@Anchor);
        SELECT ISNULL(@FamilyId,0) FamilyId,S.AdmissionId,
            COALESCE(S.AdmissionNumber,S.QuickReference,CONVERT(nvarchar(40),S.AdmissionId)) AdmissionNumber,
            S.StudentName,ISNULL(S.[{{Column(field)}}],N'') OldValue
        FROM dbo.Students S
        WHERE S.AdmissionId=@Anchor OR S.AdmissionId IN
            (SELECT AdmissionId FROM dbo.FamilyMembers WHERE FamilyId=@FamilyId)
        ORDER BY CASE WHEN S.AdmissionId=@Anchor THEN 0 ELSE 1 END,S.StudentName,S.AdmissionId;
        """;

    internal static string Apply(string field) => $$"""
        SET NOCOUNT ON;
        SET XACT_ABORT ON;
        EXEC dbo.RequirePermission @UserId,N'Students.View';
        EXEC dbo.RequirePermission @UserId,N'Students.Administer';
        IF @Anchor<=0 OR @FamilyId<=0 OR @NewValue IS NULL OR ISJSON(@Reviewed)<>1
            THROW 51210,'Invalid sibling field review.',1;
        IF DATALENGTH(@NewValue)>COL_LENGTH(N'dbo.Students',N'{{Column(field)}}')
            THROW 51210,'The shared value exceeds the field length.',1;
        IF N'{{Column(field)}}' IN (N'Address',N'FatherName') AND LEN(LTRIM(RTRIM(@NewValue)))=0
            THROW 51210,'A required family field cannot be cleared.',1;
        DECLARE @Expected TABLE(AdmissionId int NOT NULL PRIMARY KEY,OldValue nvarchar(max) NOT NULL);
        INSERT @Expected SELECT AdmissionId,OldValue FROM OPENJSON(@Reviewed)
            WITH(AdmissionId int,OldValue nvarchar(max));
        IF (SELECT COUNT(*) FROM @Expected)<2 OR NOT EXISTS(SELECT 1 FROM @Expected WHERE AdmissionId=@Anchor)
            THROW 51210,'Review the saved student and linked siblings before updating.',1;
        BEGIN TRY
            BEGIN TRANSACTION;
            DECLARE @CurrentFamily int;
            SELECT @CurrentFamily=FamilyId FROM dbo.FamilyMembers WITH(UPDLOCK,HOLDLOCK) WHERE AdmissionId=@Anchor;
            IF @CurrentFamily IS NULL OR @CurrentFamily<>@FamilyId
                THROW 51210,'Family links changed. Close and reopen the preview.',1;
            DECLARE @Members TABLE(AdmissionId int PRIMARY KEY);
            INSERT @Members SELECT AdmissionId FROM dbo.FamilyMembers WITH(UPDLOCK,HOLDLOCK) WHERE FamilyId=@FamilyId;
            IF EXISTS(SELECT AdmissionId FROM @Members EXCEPT SELECT AdmissionId FROM @Expected)
                OR EXISTS(SELECT AdmissionId FROM @Expected EXCEPT SELECT AdmissionId FROM @Members)
                THROW 51210,'Family links changed. Close and reopen the preview.',1;
            -- An exact retry after a lost reply may already contain the desired value.
            -- Every other concurrent change aborts the entire update.
            IF EXISTS(SELECT 1 FROM @Expected E LEFT JOIN dbo.Students S WITH(UPDLOCK,HOLDLOCK) ON S.AdmissionId=E.AdmissionId
                WHERE S.AdmissionId IS NULL OR
                    (CONVERT(varbinary(max),ISNULL(S.[{{Column(field)}}],N''))<>CONVERT(varbinary(max),E.OldValue)
                     AND CONVERT(varbinary(max),ISNULL(S.[{{Column(field)}}],N''))<>CONVERT(varbinary(max),@NewValue)))
                THROW 51210,'A reviewed value changed. Close and reopen the preview.',1;
            UPDATE S SET [{{Column(field)}}]=@NewValue,UpdatedAt=SYSDATETIME()
            FROM dbo.Students S JOIN @Expected E ON E.AdmissionId=S.AdmissionId
            WHERE CONVERT(varbinary(max),ISNULL(S.[{{Column(field)}}],N''))<>CONVERT(varbinary(max),@NewValue);
            DECLARE @Changed int=@@ROWCOUNT;
            COMMIT TRANSACTION;
            SELECT @Changed Changed;
        END TRY
        BEGIN CATCH
            IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
            THROW;
        END CATCH;
        """;
}
