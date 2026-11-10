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

-- The legacy SaveSchoolProfile procedure accepts a 30-character contact.
-- Its signature is retained for existing callers. This new details-only
-- procedure owns a longer contact list without touching logo/signature data.
-- Match the School Profile textbox's 150 character ceiling.
ALTER TABLE dbo.SchoolProfile ALTER COLUMN ContactNumber nvarchar(150) NULL;

EXEC(N'
CREATE PROCEDURE dbo.SaveSchoolProfileDetails
    @UserId int,
    @SchoolName nvarchar(200),
    @AddressLine nvarchar(500) = NULL,
    @City nvarchar(100) = NULL,
    @ContactNumber nvarchar(150) = NULL,
    @Email nvarchar(150) = NULL,
    @FeeProrationCutoffDay int = NULL,
    @TransportCutoffDay int = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    EXEC dbo.RequirePermission @UserId, N''System.Setup'';
    SET @SchoolName = LTRIM(RTRIM(@SchoolName));
    IF NULLIF(@SchoolName, N'''') IS NULL
        THROW 59000, ''School name is required.'', 1;
    IF (@FeeProrationCutoffDay IS NOT NULL
        AND @FeeProrationCutoffDay NOT BETWEEN 0 AND 28)
       OR (@TransportCutoffDay IS NOT NULL
        AND @TransportCutoffDay NOT BETWEEN 0 AND 28)
        THROW 59001, ''Fee and transport cutoff days must be 1–28 or blank.'', 1;
    IF @FeeProrationCutoffDay = 0 SET @FeeProrationCutoffDay = NULL;
    IF @TransportCutoffDay = 0 SET @TransportCutoffDay = NULL;
    SET @AddressLine = NULLIF(LTRIM(RTRIM(@AddressLine)), N'''');
    SET @City = NULLIF(LTRIM(RTRIM(@City)), N'''');
    SET @ContactNumber = NULLIF(LTRIM(RTRIM(@ContactNumber)), N'''');
    SET @Email = NULLIF(LTRIM(RTRIM(@Email)), N'''');

    BEGIN TRY
        BEGIN TRANSACTION;
        UPDATE dbo.SchoolProfile WITH (UPDLOCK, HOLDLOCK)
           SET SchoolName = @SchoolName,
               AddressLine = @AddressLine,
               City = @City,
               ContactNumber = @ContactNumber,
               Email = @Email,
               FeeProrationCutoffDay = @FeeProrationCutoffDay,
               TransportCutoffDay = @TransportCutoffDay,
               UpdatedAt = SYSDATETIME(),
               UpdatedByUserId = @UserId
         WHERE SchoolProfileId = 1;
        IF @@ROWCOUNT = 0
        BEGIN
            INSERT dbo.SchoolProfile
                (SchoolProfileId,SchoolName,AddressLine,City,ContactNumber,Email,
                 FeeProrationCutoffDay,TransportCutoffDay,
                 CreatedAt,UpdatedAt,CreatedByUserId,UpdatedByUserId)
            VALUES
                (1,@SchoolName,@AddressLine,@City,@ContactNumber,@Email,
                 @FeeProrationCutoffDay,@TransportCutoffDay,
                 SYSDATETIME(),SYSDATETIME(),@UserId,@UserId);
        END;
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
       N'Independent School Profile saves and extended school contact list');
