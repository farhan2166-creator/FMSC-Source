-- Schema 15: first official admission works on schema-only/empty school databases.
-- Installer owns the verified pre-migration backup and enclosing migration transaction.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF ISNULL((SELECT TOP (1) TRY_CONVERT(int,VersionNumber)
           FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0) <> 14
    THROW 51000,'Admission counter initialization requires schema 14.',1;
IF OBJECT_ID(N'dbo.AdmissionNumberCounter',N'U') IS NULL
   OR OBJECT_ID(N'dbo.Students',N'U') IS NULL
   OR COL_LENGTH(N'dbo.AdmissionNumberCounter',N'CounterId') IS NULL
   OR COL_LENGTH(N'dbo.AdmissionNumberCounter',N'LastNumber') IS NULL
   OR COL_LENGTH(N'dbo.Students',N'AdmissionNumber') IS NULL
    THROW 51000,'Required admission counter/student schema is missing; inspect before upgrading.',1;

-- Do not change an existing school counter: historic/ad-hoc numbering is authoritative.
IF NOT EXISTS(SELECT 1 FROM dbo.AdmissionNumberCounter WITH (UPDLOCK,HOLDLOCK)
              WHERE CounterId=1)
BEGIN
    DECLARE @Highest bigint=0, @Unusable int=0;
    ;WITH OfficialNumbers AS
    (
        SELECT Number = LTRIM(RTRIM(AdmissionNumber))
          FROM dbo.Students
         WHERE NULLIF(LTRIM(RTRIM(AdmissionNumber)),N'') IS NOT NULL
    ), NumericPrefixes AS
    (
        SELECT Prefix = NULLIF(LEFT(Number,
            PATINDEX(N'%[^0-9]%',Number+N'!')-1),N'')
          FROM OfficialNumbers
    ), Parsed AS
    (
        SELECT NumericPart=TRY_CONVERT(bigint,Prefix)
          FROM NumericPrefixes
    )
    SELECT @Highest=ISNULL(MAX(NumericPart),0),
           @Unusable=COUNT(CASE WHEN NumericPart IS NULL THEN 1 END)
      FROM Parsed;

    -- A historic register containing unparseable official IDs is not safe to
    -- reconstruct by guessing; stop before altering the live database.
    IF @Unusable<>0 OR @Highest>2147483647
        THROW 51000,'Admission counter is absent and existing admission numbers cannot be safely reconstructed. The database was not changed.',1;

    INSERT dbo.AdmissionNumberCounter(CounterId,LastNumber)
    VALUES(1,CONVERT(int,@Highest));
END;

INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'15',SYSDATETIME(),SUSER_SNAME(),
       N'Initialize missing first admission counter while preserving existing numbering');
