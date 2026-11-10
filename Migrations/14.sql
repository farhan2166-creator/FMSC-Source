-- Schema 14: Fee Types editor can configure the mandatory/transport flags used
-- by Setup Readiness, admissions and transport billing on a clean school database.
-- The installer owns the verified backup and outer migration transaction.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF ISNULL((SELECT TOP (1) TRY_CONVERT(int, VersionNumber)
           FROM dbo.DatabaseVersion ORDER BY VersionId DESC), 0) <> 13
    THROW 51000, 'Fee type policy configuration requires schema 13.', 1;
IF OBJECT_ID(N'dbo.GetFeeTypes', N'P') IS NULL
   OR OBJECT_ID(N'dbo.SaveFeeTypeWithDefaultFrequency', N'P') IS NULL
   OR COL_LENGTH(N'dbo.FeeTypes', N'IsMandatory') IS NULL
   OR COL_LENGTH(N'dbo.FeeTypes', N'IsTransportLinked') IS NULL
    THROW 51000, 'Required fee-type procedures/columns are missing; inspect database before upgrading.', 1;

-- Existing flags/records are left unchanged. Reading the fee types now returns
-- both flags so the editor can reflect their actual saved values.
EXEC(N'CREATE OR ALTER PROCEDURE dbo.GetFeeTypes
    @UserId int
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.RequirePermission @UserId, N''Fees.View'';
    SELECT FeeTypeId,
           FeeTypeCode,
           FeeTypeName,
           Description,
           DisplayOrder,
           IsSystemDefined,
           ISNULL(DefaultFrequency, N''Monthly'') AS DefaultFrequency,
           IsMandatory,
           IsTransportLinked
      FROM dbo.FeeTypes
     ORDER BY DisplayOrder, FeeTypeName;
END');

-- Preserve the established atomic name/description/default-frequency writer,
-- extending it with optional flags for newer UI callers.
EXEC(N'CREATE OR ALTER PROCEDURE dbo.SaveFeeTypeWithDefaultFrequency
    @UserId int,
    @FeeTypeId int,
    @FeeTypeCode nvarchar(30),
    @FeeTypeName nvarchar(100),
    @Description nvarchar(250) = NULL,
    @DisplayOrder int = 0,
    @DefaultFrequency nvarchar(20) = N''Monthly'',
    @IsMandatory bit = NULL,
    @IsTransportLinked bit = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    EXEC dbo.RequirePermission @UserId, N''Fees.Configuration.Manage'';
    IF @DefaultFrequency IS NULL
       OR @DefaultFrequency NOT IN (N''Monthly'', N''Yearly'', N''As Needed'')
        THROW 59251, ''Default frequency must be Monthly, Yearly, or As Needed.'', 1;

    DECLARE @Saved TABLE (FeeTypeId int);
    DECLARE @SavedFeeTypeId int;
    BEGIN TRANSACTION;

    INSERT INTO @Saved (FeeTypeId)
    EXEC dbo.SaveFeeType
        @UserId = @UserId,
        @FeeTypeId = @FeeTypeId,
        @FeeTypeCode = @FeeTypeCode,
        @FeeTypeName = @FeeTypeName,
        @Description = @Description,
        @DisplayOrder = @DisplayOrder;

    SELECT @SavedFeeTypeId = FeeTypeId FROM @Saved;
    IF @SavedFeeTypeId IS NULL OR @SavedFeeTypeId <= 0
        THROW 59252, ''The fee type was not saved, so nothing has been stored.'', 1;

    EXEC dbo.SetFeeTypeDefaultFrequency
        @UserId = @UserId,
        @FeeTypeId = @SavedFeeTypeId,
        @DefaultFrequency = @DefaultFrequency;

    -- NULL is deliberately backwards-compatible for older callers. The editor
    -- supplies both flags explicitly; editing names/frequencies never resets a
    -- legacy school''s policy behind its back.
    UPDATE dbo.FeeTypes
       SET IsMandatory = COALESCE(@IsMandatory, IsMandatory),
           IsTransportLinked = COALESCE(@IsTransportLinked, IsTransportLinked)
     WHERE FeeTypeId = @SavedFeeTypeId;

    IF @@ROWCOUNT <> 1
       OR NOT EXISTS (
           SELECT 1 FROM dbo.FeeTypes
            WHERE FeeTypeId = @SavedFeeTypeId
              AND DefaultFrequency = @DefaultFrequency
              AND (@IsMandatory IS NULL OR IsMandatory = @IsMandatory)
              AND (@IsTransportLinked IS NULL OR IsTransportLinked = @IsTransportLinked))
        THROW 59253, ''The fee type settings were not stored, so the save was rolled back.'', 1;

    COMMIT TRANSACTION;
    SELECT @SavedFeeTypeId AS FeeTypeId;
END');

INSERT dbo.DatabaseVersion(VersionNumber, AppliedOn, AppliedBy, Notes)
VALUES (N'14', SYSDATETIME(), SUSER_SNAME(),
        N'Editable mandatory and transport-linked fee type policy in fee setup');
