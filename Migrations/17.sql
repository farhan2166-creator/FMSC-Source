-- Restore leaving-certificate print authority without granting ordinary
-- certificate printing or School Profile administration. Installer owns backup/transaction.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF ISNULL((SELECT TOP (1) TRY_CONVERT(int,VersionNumber)
           FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0) <> 16
    THROW 51000,'Certificate print permission correction requires schema 16.',1;
IF OBJECT_ID(N'dbo.GetPrintedCertificateSignature',N'P') IS NULL
    THROW 51000,'Certificate signature reader is missing; inspect before upgrading.',1;

EXEC(N'ALTER PROCEDURE dbo.GetPrintedCertificateSignature
    @UserId int, @ForLeaving bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    IF @ForLeaving = 1
        EXEC dbo.RequirePermission @UserId,N''Students.Leave'';
    ELSE
        EXEC dbo.RequirePermission @UserId,N''Certificates.Print'';
    SELECT PrincipalSignatureImage,PrincipalSignatureContentType,PrintPrincipalSignature
      FROM dbo.SchoolProfile WHERE SchoolProfileId=1;
END;');

INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'17',SYSDATETIME(),SUSER_SNAME(),
       N'Preserve leaving-certificate print authority for optional principal signature');
