using System.Text.Json;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Students.Admissions;

internal static class AdmissionSiblingSyncData
{
    internal sealed record Field(string Column, string Label, int MaxLength, bool Required = false, bool Phone = false);
    internal static readonly Field Address = new("Address", "Address", SchemaLengths.Address, true);
    internal static readonly Field PrimaryPhone = new("ContactNumber", "Primary Phone", SchemaLengths.StudentContactNumber, Phone: true);
    internal static readonly Field AlternatePhone = new("AlternateContactNumber", "Alternate Phone", SchemaLengths.AlternateContactNumber, Phone: true);
    internal static readonly Field FatherName = new("FatherName", "Father Name", SchemaLengths.StudentFatherName, true);
    internal static readonly Field PreviousSchool = new("PreviousSchool", "Previous School", SchemaLengths.StudentPreviousSchool);
    internal sealed record Row(int AdmissionId, string AdmissionNumber, string StudentName, string OldValue);
    internal sealed record Preview(int Actor, int Anchor, int FamilyId, Field Field, string NewValue, IReadOnlyList<Row> Rows)
    {
        internal int Changes => Rows.Count(row => row.OldValue != NewValue);
    }

    internal static bool CanUse => CurrentUser.HasPermission("Students.View") && CurrentUser.HasPermission("Students.Administer");
    internal static string Normalize(Field field, string value)
    {
        Field known = field.Column switch
        {
            "Address" => Address, "ContactNumber" => PrimaryPhone, "AlternateContactNumber" => AlternatePhone,
            "FatherName" => FatherName, "PreviousSchool" => PreviousSchool,
            _ => throw new ArgumentException("Unsupported sibling field.")
        };
        if (field != known) throw new ArgumentException("Unsupported sibling field definition.");
        _ = SiblingFieldSyncSql.Read(field.Column);
        value = value.Trim();
        if (value.Length > field.MaxLength || (field.Required && value.Length == 0))
            throw new InvalidOperationException($"Enter a valid {field.Label} before syncing.");
        if (field.Phone && !PakistanPhoneNumber.IsValidOptional(value))
            throw new InvalidOperationException(PakistanPhoneNumber.ValidationMessage);
        return Convert.ToString(RecordTextPolicy.NormalizeParameter("@" + field.Column, value)) ?? "";
    }

    internal static async Task<Preview> ReadAsync(int anchor, Field field, string value)
    {
        if (!CanUse) throw new UnauthorizedAccessException("Student administration permission is required.");
        int actor = CurrentUser.UserId;
        value = Normalize(field, value);
        using var table = await DatabaseHelper.QuerySqlAsync(SiblingFieldSyncSql.Read(field.Column),
            DatabaseHelper.Int("@UserId", actor), DatabaseHelper.Int("@Anchor", anchor));
        if (actor != CurrentUser.UserId || !CanUse) throw new UnauthorizedAccessException("Signed-in access changed.");
        var rows = table.AsEnumerable().Select(row => new Row(row.Field<int>("AdmissionId"),
            Convert.ToString(row["AdmissionNumber"]) ?? "", Convert.ToString(row["StudentName"]) ?? "",
            Convert.ToString(row["OldValue"]) ?? "")).ToArray();
        if (rows.Length < 2 || rows.All(row => row.AdmissionId != anchor))
            throw new InvalidOperationException("No linked siblings are available. Link the siblings first.");
        return new(actor, anchor, Convert.ToInt32(table.Rows[0]["FamilyId"]), field, value, rows);
    }

    internal static async Task<int> ApplyAsync(Preview preview)
    {
        if (preview.Actor != CurrentUser.UserId || !CanUse)
            throw new UnauthorizedAccessException("Signed-in access changed. Reopen the sibling preview.");
        string value = Normalize(preview.Field, preview.NewValue);
        using var result = await DatabaseHelper.QuerySqlAsync(SiblingFieldSyncSql.Apply(preview.Field.Column),
            DatabaseHelper.Int("@UserId", preview.Actor), DatabaseHelper.Int("@Anchor", preview.Anchor),
            DatabaseHelper.Int("@FamilyId", preview.FamilyId), DatabaseHelper.P("@NewValue", value),
            DatabaseHelper.P("@Reviewed", JsonSerializer.Serialize(preview.Rows.Select(row => new { row.AdmissionId, row.OldValue }))));
        return Convert.ToInt32(result.Rows[0]["Changed"]);
    }
}
