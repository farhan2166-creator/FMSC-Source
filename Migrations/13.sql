-- Schema 13: make School Profile details and logo independent writes.
-- Installer applies this within its verified-backup migration transaction.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF ISNULL((SELECT TOP (1) TRY_CONVERT(int, VersionNumber)
           FROM dbo.DatabaseVersion ORDER BY VersionId DESC), 0) <> 12
    THROW 51000, 'Independent School Profile saves require schema 12.', 1;
IF OBJECT_ID(N'dbo.SaveSchoolProfileDetails', N'P') IS NOT NULL
   OR OBJECT_ID(N'dbo.SaveSchoolLogo', N'P') IS NOT NULL
    THROW 51000, 'Independent School Profile procedures already exist; inspect before retry.', 1;

-- Reuse the existing validated SaveSchoolProfile contract. Read the SAVED
-- logo under an update/serializable lock to avoid overwriting it during a
-- simultaneous logo save. Never take media values from unsaved UI controls.
EXEC(N'
CREATE PROCEDURE dbo.SaveSchoolProfileDetails
    @UserId int,
    @SchoolName nvarchar(300),
    @AddressLine nvarchar(500) = NULL,
    @City nvarchar(200) = NULL,
    @ContactNumber nvarchar(300) = NULL,
    @Email nvarchar(250) = NULL,
    @FeeProrationCutoffDay int = NULL,
    @TransportCutoffDay int = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @LogoImage varbinary(max),
            @LogoFileName nvarchar(max),
            @LogoContentType nvarchar(max);
    BEGIN TRY
        BEGIN TRANSACTION;
        SELECT @LogoImage = LogoImage,
               @LogoFileName = LogoFileName,
               @LogoContentType = LogoContentType
          FROM dbo.SchoolProfile WITH (UPDLOCK, HOLDLOCK)
         WHERE SchoolProfileId = 1;

        EXEC dbo.SaveSchoolProfile
             @UserId = @UserId,
             @SchoolName = @SchoolName,
             @AddressLine = @AddressLine,
             @City = @City,
             @ContactNumber = @ContactNumber,
             @Email = @Email,
             @LogoImage = @LogoImage,
             @LogoFileName = @LogoFileName,
             @LogoContentType = @LogoContentType,
             @FeeProrationCutoffDay = @FeeProrationCutoffDay,
             @TransportCutoffDay = @TransportCutoffDay;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;');

-- Image-only write: does not touch SchoolName, contact, fee cutoffs or
-- principal signature. Null payload explicitly removes the saved logo.
EXEC(N'
CREATE PROCEDURE dbo.SaveSchoolLogo
    @UserId int,
    @LogoImage varbinary(max) = NULL,
    @LogoFileName nvarchar(260) = NULL,
    @LogoContentType nvarchar(40) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.RequirePermission @UserId, N''System.Setup'';
    IF @LogoImage IS NULL
    BEGIN
        IF @LogoFileName IS NOT NULL OR @LogoContentType IS NOT NULL
            THROW 51131, ''Logo filename and format must be empty when the logo is removed.'', 1;
    END
    ELSE
    BEGIN
        IF DATALENGTH(@LogoImage) = 0 OR DATALENGTH(@LogoImage) > 2000000
            THROW 51130, ''Logo image must be 2 MB or smaller.'', 1;
        IF NULLIF(LTRIM(RTRIM(@LogoFileName)), N'''') IS NULL
           OR @LogoContentType NOT IN (N''image/png'', N''image/jpeg'')
           OR @LogoContentType IS NULL
            THROW 51131, ''Invalid logo image filename or format.'', 1;
    END;
    UPDATE dbo.SchoolProfile
       SET LogoImage = @LogoImage,
           LogoFileName = @LogoFileName,
           LogoContentType = @LogoContentType,
           UpdatedAt = SYSDATETIME(),
           UpdatedByUserId = @UserId
     WHERE SchoolProfileId = 1;
    IF @@ROWCOUNT <> 1
        THROW 51132, ''Save school information before uploading a logo.'', 1;
END;');

INSERT dbo.DatabaseVersion(VersionNumber, AppliedOn, AppliedBy, Notes)
VALUES(N'13', SYSDATETIME(), SUSER_SNAME(),
       N'Independent School Profile details and logo saving');
