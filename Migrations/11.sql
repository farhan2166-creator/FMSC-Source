-- Schema 11: optional school-profile principal signature used only when enabled.
-- The installer owns the outer transaction and verified pre-migration backup.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF ISNULL((SELECT TOP (1) TRY_CONVERT(int,VersionNumber)
           FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0) <> 10
    THROW 51000,'Certificate signature setup requires schema 10.',1;
IF COL_LENGTH(N'dbo.SchoolProfile',N'PrincipalSignatureImage') IS NOT NULL
   OR COL_LENGTH(N'dbo.SchoolProfile',N'PrincipalSignatureContentType') IS NOT NULL
   OR COL_LENGTH(N'dbo.SchoolProfile',N'PrintPrincipalSignature') IS NOT NULL
   OR OBJECT_ID(N'dbo.SaveSchoolCertificateSignature',N'P') IS NOT NULL
   OR OBJECT_ID(N'dbo.GetSchoolCertificateSignature',N'P') IS NOT NULL
   OR OBJECT_ID(N'dbo.GetPrintedCertificateSignature',N'P') IS NOT NULL
    THROW 51000,'Certificate signature objects already exist; inspect before applying.',1;

ALTER TABLE dbo.SchoolProfile ADD
    PrincipalSignatureImage varbinary(max) NULL,
    PrincipalSignatureContentType nvarchar(40) NULL,
    PrintPrincipalSignature bit NOT NULL
        CONSTRAINT DF_SchoolProfile_PrintPrincipalSignature DEFAULT (0);

-- Keep the established school-profile read/save procedures intact.
-- Signature changes use the same System.Setup permission as School Profile.
EXEC(N'
CREATE OR ALTER PROCEDURE dbo.SaveSchoolCertificateSignature
    @UserId int,
    @SignatureImage varbinary(max) = NULL,
    @SignatureContentType nvarchar(40) = NULL,
    @PrintSignature bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.RequirePermission @UserId,N''System.Setup'';
    IF @SignatureImage IS NOT NULL AND
       (DATALENGTH(@SignatureImage)=0 OR DATALENGTH(@SignatureImage)>2000000)
       THROW 51120,''Signature image must be 2 MB or smaller.'',1;
    IF (@SignatureImage IS NULL AND @SignatureContentType IS NOT NULL)
       OR (@SignatureImage IS NOT NULL AND
           (@SignatureContentType IS NULL OR @SignatureContentType NOT IN (N''image/png'',N''image/jpeg'')))
       THROW 51121,''Invalid signature image format.'',1;
    IF @PrintSignature = 1 AND @SignatureImage IS NULL
       THROW 51122,''Upload a signature before enabling it.'',1;
    UPDATE dbo.SchoolProfile
       SET PrincipalSignatureImage=@SignatureImage,
           PrincipalSignatureContentType=@SignatureContentType,
           PrintPrincipalSignature=@PrintSignature
     WHERE SchoolProfileId=1;
    IF @@ROWCOUNT<>1 THROW 51123,''Save the school profile first.'',1;
END;');

EXEC(N'
CREATE OR ALTER PROCEDURE dbo.GetSchoolCertificateSignature
    @UserId int
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.RequirePermission @UserId,N''System.Setup'';
    SELECT PrincipalSignatureImage,PrincipalSignatureContentType,PrintPrincipalSignature
      FROM dbo.SchoolProfile WHERE SchoolProfileId=1;
END;');

EXEC(N'
CREATE OR ALTER PROCEDURE dbo.GetPrintedCertificateSignature
    @UserId int
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.RequirePermission @UserId,N''Certificates.Print'';
    SELECT PrincipalSignatureImage,PrincipalSignatureContentType,PrintPrincipalSignature
      FROM dbo.SchoolProfile WHERE SchoolProfileId=1;
END;');

INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'11',SYSDATETIME(),SUSER_SNAME(),
       N'Optional School Profile principal signature for certificate printing');
