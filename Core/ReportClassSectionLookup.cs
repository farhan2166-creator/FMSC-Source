using Microsoft.Data.SqlClient;

namespace FMSC_SMS.Core;

/// <summary>Reports retain historical placements after setup stops offering them for new enrollments.</summary>
internal static class ReportClassSectionLookup
{
    internal static Task<DataTable> QueryAsync(int campusId = 0, int classId = 0, int sectionId = 0)
    {
        SqlParameter[] parameters = {
            DatabaseHelper.Int("@UserId", CurrentUser.UserId),
            DatabaseHelper.Int("@CampusId", campusId),
            DatabaseHelper.Int("@ClassId", classId),
            DatabaseHelper.Int("@SectionId", sectionId)
        };
        const string sql = """
            EXEC dbo.RequireAuthenticatedUser @UserId;
            SELECT CCS.ClassSectionId, CCS.CampusId, CMP.CampusName,
                   CCS.ClassId, C.ClassName, CCS.SectionId, S.SectionName
            FROM dbo.CampusClassSections CCS
            JOIN dbo.Campuses CMP ON CMP.CampusId = CCS.CampusId
            JOIN dbo.Classes C ON C.ClassId = CCS.ClassId
            JOIN dbo.Sections S ON S.SectionId = CCS.SectionId
            WHERE (@CampusId = 0 OR CCS.CampusId = @CampusId)
              AND (@ClassId = 0 OR CCS.ClassId = @ClassId)
              AND (@SectionId = 0 OR CCS.SectionId = @SectionId)
            ORDER BY CMP.CampusName, C.ClassName, S.SectionName, S.SectionId;
            """;
        return LookupDataCache.GetAsync("ReportClassSectionLookup", parameters,
            commandParameters => Task.Run(() => DatabaseHelper.QuerySql(sql, commandParameters)),
            CancellationToken.None);
    }
}
