using Microsoft.Data.SqlClient;

// Run only inside the existing guarded disposable lifecycle database.
internal static class CertificatePrintAuthorityChecks
{
    internal static Task Run(Fixture fixture, Report report)
    {
        var result = Sql.Query("""
            SET NOCOUNT ON; SET XACT_ABORT OFF;
            BEGIN TRANSACTION;
            BEGIN TRY
                INSERT dbo.Roles(RoleName,Description) VALUES(N'Fixture_Print_Authority',N'Temporary permission matrix');
                DECLARE @Role int=SCOPE_IDENTITY();
                INSERT dbo.Users(Username,PasswordHash,PasswordSalt,RoleId,MustChangePassword)
                SELECT N'fixture_print_authority',PasswordHash,PasswordSalt,@Role,0
                FROM dbo.Users WHERE UserId=@Source;
                DECLARE @User int=SCOPE_IDENTITY();
                INSERT dbo.UserRoles(UserId,RoleId) VALUES(@User,@Role);
                INSERT dbo.RolePermissions(RoleId,PermissionId)
                SELECT @Role,PermissionId FROM dbo.Permissions WHERE PermissionCode=N'Students.Leave';
                DECLARE @Signature TABLE(Image varbinary(max),ContentType nvarchar(40),Enabled bit);
                INSERT @Signature EXEC dbo.GetPrintedCertificateSignature @UserId=@User,@ForLeaving=1;
                IF NOT EXISTS(SELECT 1 FROM @Signature) THROW 59974,'Leaving-only signature reader returned no school settings.',1;
                BEGIN TRY
                    INSERT @Signature EXEC dbo.GetPrintedCertificateSignature @UserId=@User;
                    THROW 59974,'Leaving-only user gained ordinary certificate print authority.',1;
                END TRY
                BEGIN CATCH
                    IF ERROR_NUMBER()<>50003 THROW;
                END CATCH;
                DELETE dbo.RolePermissions WHERE RoleId=@Role;
                INSERT dbo.RolePermissions(RoleId,PermissionId)
                SELECT @Role,PermissionId FROM dbo.Permissions WHERE PermissionCode=N'Certificates.Print';
                INSERT @Signature EXEC dbo.GetPrintedCertificateSignature @UserId=@User;
                BEGIN TRY
                    INSERT @Signature EXEC dbo.GetPrintedCertificateSignature @UserId=@User,@ForLeaving=1;
                    THROW 59974,'Ordinary printer gained leaving authority.',1;
                END TRY
                BEGIN CATCH
                    IF ERROR_NUMBER()<>50003 THROW;
                END CATCH;
                ROLLBACK TRANSACTION;
                SELECT CONVERT(bit,1) Passed;
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
                THROW;
            END CATCH;
            """, Sql.P("@Source",fixture.Restricted));
        report.Check(result.Rows.Count==1 && Convert.ToBoolean(result.Rows[0]["Passed"]),
            "Signature reading preserves leaving-only and ordinary-print permission boundaries without modifying school data.");
        return Task.CompletedTask;
    }
}
