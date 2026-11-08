using Microsoft.Data.SqlClient;
using System.Security.Cryptography;

internal sealed class Fixture
{
    internal int Admin, Restricted, Campus, Class6, Class7, Section, Placement6, Placement7, SourceYear, NextYear, Tuition, AdmissionFee, Purchase;
    internal DateTime AdmissionDate, MonthlyDate, PromotionDate, NextMonthlyDate;
    internal DateTime BirthDate => AdmissionDate.AddYears(-12).AddMonths(-3);

    internal static Fixture Create()
    {
        if (Sql.Count("SELECT COUNT(*) FROM dbo.Students;") != 0 || Sql.Count("SELECT COUNT(*) FROM dbo.Users;") != 0)
            throw new InvalidOperationException("Reference fixtures require an empty schema-only database, never imported school data.");
        // The runner publishes baseline 1, then applies any future numbered migrations.
        if (Convert.ToString(Sql.Scalar("SELECT TOP(1) VersionNumber FROM dbo.DatabaseVersion ORDER BY VersionId DESC;")) != DatabaseHelper.RequiredDatabaseSchemaVersion)
            throw new InvalidOperationException("Initialize the owned baseline fixture and apply any future migrations before seeding reference data.");
        Sql.Query("INSERT dbo.AdmissionNumberCounter(CounterId,LastNumber) VALUES(1,1000);");
        // DacFx round-trips this property as varchar. Repair ONLY this disposable fixture's identity/type.
        Sql.Query("DECLARE @Scope uniqueidentifier=NEWID(); EXEC sys.sp_updateextendedproperty @name=N'FMSC_QuickRegistrationScope',@value=@Scope;");
        using (var c = DatabaseHelper.GetConnection()) { c.Open(); DatabaseCompatibility.Validate(c); }
        Sql.Query("INSERT dbo.Roles(RoleName,Description) VALUES(N'Administrator',N'Fictional full-authority test role'),(N'Accountant',N'Fictional restricted test role');");
        // The baseline installs 28 codes; migration 9 supplies three new
        // communication codes. Seed only missing fixture permissions, then
        // verify that every pre-existing definition exactly matches the catalog.
        foreach (var definition in PermissionCatalog.All.Values)
            Sql.Query("""
                IF NOT EXISTS(SELECT 1 FROM dbo.Permissions WHERE PermissionCode=@Code)
                    INSERT dbo.Permissions(PermissionCode,PermissionName,Description)
                    VALUES(@Code,@Name,@Description);
                IF NOT EXISTS(SELECT 1 FROM dbo.Permissions
                    WHERE PermissionCode=@Code AND PermissionName=@Name AND Description=@Description)
                    THROW 51099,'Permission metadata drifted from the audited catalog.',1;
                """,
                Sql.P("@Code", definition.Code), Sql.P("@Name", definition.Name),
                Sql.P("@Description", definition.Description));
        if (Sql.Count("SELECT COUNT(*) FROM dbo.Permissions;") != PermissionCatalog.All.Count)
            throw new InvalidOperationException("The fixture contains unexpected permission entries.");
        var f = new Fixture();
        byte[] salt = RandomNumberGenerator.GetBytes(32);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Guid.NewGuid().ToString("N"), salt, 100_000, HashAlgorithmName.SHA512, 64);
        f.Admin = Sql.Key("BootstrapFirstAdministrator", Sql.P("@Username", "level1_owner"), Sql.P("@PasswordHash", hash), Sql.P("@PasswordSalt", salt));
        f.Restricted = Sql.Count("""
            INSERT dbo.Users(Username,PasswordHash,PasswordSalt,RoleId,MustChangePassword)
            SELECT N'level1_restricted',@Hash,@Salt,RoleId,0 FROM dbo.Roles WHERE RoleName=N'Accountant';
            DECLARE @Id int=SCOPE_IDENTITY();
            INSERT dbo.UserRoles(UserId,RoleId) SELECT @Id,RoleId FROM dbo.Roles WHERE RoleName=N'Accountant';
            INSERT dbo.RolePermissions(RoleId,PermissionId)
            SELECT R.RoleId,P.PermissionId FROM dbo.Roles R CROSS JOIN dbo.Permissions P
            WHERE R.RoleName=N'Accountant' AND P.PermissionCode IN(N'Fees.View',N'Fees.Collect',N'Students.View',N'Reports.View',N'Fees.Reports.View',N'Fees.Statement.View');
            SELECT @Id;
            """, Sql.P("@Hash", hash), Sql.P("@Salt", salt));
        CurrentUser.UserId = f.Admin;
        Sql.Proc("SaveSchoolProfile", Sql.P("@UserId", f.Admin), Sql.P("@SchoolName", "Fictional Meadow School"),
            Sql.P("@AddressLine", "Synthetic Test Lane"), Sql.P("@City", "Example City"), Sql.P("@ContactNumber", null),
            Sql.P("@Email", "school@example.invalid"), new SqlParameter("@LogoImage", SqlDbType.VarBinary) { Value = DBNull.Value },
            Sql.P("@LogoFileName", null), Sql.P("@LogoContentType", null), Sql.P("@FeeProrationCutoffDay", null));
        f.Campus = Sql.Key("SaveCampus", Sql.P("@UserId", f.Admin), Sql.P("@CampusCode", "TEST"), Sql.P("@CampusName", "Fictional Main Campus"), Sql.P("@CampusType", "School"));
        int program = Sql.Key("SaveAcademicProgram", Sql.P("@UserId", f.Admin), Sql.P("@AcademicProgramId", 0),
            Sql.P("@ProgramType", "School"), Sql.P("@ProgramCode", "TEST"), Sql.P("@ProgramName", "Fictional Middle School"));
        f.Class6 = Sql.Key("SaveClass", Sql.P("@UserId", f.Admin), Sql.P("@ClassName", "Class 6"), Sql.P("@ClassCode", "C6"), Sql.P("@AcademicProgramId", program));
        f.Class7 = Sql.Key("SaveClass", Sql.P("@UserId", f.Admin), Sql.P("@ClassName", "Class 7"), Sql.P("@ClassCode", "C7"), Sql.P("@AcademicProgramId", program));
        f.Section = Sql.Key("SaveSection", Sql.P("@UserId", f.Admin), Sql.P("@SectionId", 0), Sql.P("@SectionCode", "A"), Sql.P("@SectionName", "Section A"));
        f.Placement6 = f.Placement(f.Class6); f.Placement7 = f.Placement(f.Class7);
        int year = DateTime.UtcNow.Year;
        f.AdmissionDate = new DateTime(year - 1, 4, 1);
        // Current-year admissions bill normally; backdated previous-year admissions
        // intentionally preserve history without creating fees (schema 60 onward).
        f.MonthlyDate = new DateTime(year, DateTime.UtcNow.Month, 1);
        f.PromotionDate = new DateTime(year, 1, 1);
        f.NextMonthlyDate = new DateTime(year, DateTime.UtcNow.Month, 1);
        f.SourceYear = f.Year(year - 1, false); f.NextYear = f.Year(year, true);
        f.Tuition = f.Fee("TUITION", "Tuition fee", true, "Monthly");
        f.AdmissionFee = f.Fee("ADMISSION", "Admission fee", true, "Yearly");
        f.Purchase = f.Fee("PURCHASE", "Fictional workbook purchase", false, "As Needed");
        f.Rate(f.Class6, f.Tuition, 5000, "Monthly"); f.Rate(f.Class6, f.AdmissionFee, 2000, "Yearly");
        f.Rate(f.Class7, f.Tuition, 6000, "Monthly"); f.Rate(f.Class7, f.AdmissionFee, 2000, "Yearly");
        return f;
    }

    private int Placement(int classId) => Sql.Count("INSERT dbo.CampusClassSections(CampusId,ClassId,SectionId) VALUES(@Campus,@Class,@Section); SELECT CONVERT(int,SCOPE_IDENTITY());",
        Sql.P("@Campus", Campus), Sql.P("@Class", classId), Sql.P("@Section", Section));
    // Schema 63 requires consecutive YYYY-YYYY names; dates remain synthetic Jan-Dec.
    private int Year(int year, bool current) => Sql.Key("SaveAcademicYear", Sql.P("@AcademicYearId", 0), Sql.P("@YearName", $"{year:D4}-{year+1:D4}"),
        Sql.P("@StartDate", new DateTime(year, 1, 1)), Sql.P("@EndDate", new DateTime(year, 12, 31)), Sql.P("@IsCurrent", current), Sql.P("@Remarks", "Synthetic fixture"), Sql.P("@UserId", Admin));
    private int Fee(string code, string name, bool mandatory, string frequency)
    {
        int id = Sql.Key("SaveFeeType", Sql.P("@UserId", Admin), Sql.P("@FeeTypeCode", code), Sql.P("@FeeTypeName", name));
        // Mandatory/system flags are reference data, not a supported SaveFeeType edit.
        Sql.Query("UPDATE dbo.FeeTypes SET IsMandatory=@Mandatory,IsSystemDefined=@Mandatory,DefaultFrequency=@Frequency WHERE FeeTypeId=@Id;",
            Sql.P("@Mandatory", mandatory), Sql.P("@Frequency", frequency), Sql.P("@Id", id)); return id;
    }
    internal int Rate(int classId, int fee, decimal amount, string frequency) => Sql.Key("SaveFeeConfiguration",
        Sql.P("@UserId", Admin), Sql.P("@CampusId", Campus), Sql.P("@ClassId", classId), Sql.P("@FeeTypeId", fee),
        Sql.P("@Amount", amount), Sql.P("@Frequency", frequency), Sql.P("@FeeConfigurationId", 0), Sql.P("@Remarks", "Synthetic rate"));
    internal SqlParameter[] AdmissionArgs(string name = "Ayaan Example", int? family = null, bool current = true) => new[] {
        Sql.P("@UserId", Admin), Sql.P("@AdmissionDate", current ? PromotionDate : AdmissionDate), Sql.P("@AdmissionCampusId", Campus),
        Sql.P("@AcademicYearId", current ? NextYear : SourceYear), Sql.P("@ClassSectionId", Placement6),
        Sql.P("@StudentName", name), Sql.P("@FatherName", "Fictional Parent"), Sql.P("@DateOfBirth", BirthDate),
        Sql.P("@PlaceOfBirth", "Example City"), Sql.P("@Gender", "Male"), Sql.P("@BFormNumber", null), Sql.P("@Religion", null),
        Sql.P("@BloodGroup", null), Sql.P("@ContactNumber", null), Sql.P("@AlternateContactNumber", null),
        Sql.P("@Address", "Fictional student address"), Sql.P("@PreviousSchool", "Fictional Primary School"), Sql.P("@FatherOccupation", "Fictional occupation"),
        Sql.P("@Disability", "No"), Sql.P("@UsesTransport", false), Sql.P("@QuickRegistration", false), Sql.P("@FamilyStudentId", family),
        Sql.TvpParameter("@SelectedFeeTypeIds", "FeeTypeIdList", Tvp.FeeTypeIds(Array.Empty<int>())),
        Sql.TvpParameter("@AdmissionDiscounts", "AdmissionFeeDiscountType", Tvp.AdmissionFeeDiscounts(Array.Empty<(int,decimal)>()))
    };
}
