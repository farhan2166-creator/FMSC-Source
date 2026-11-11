using FMSC_SMS.Core;
namespace FMSC_SMS.Pages.Finance.Configuration;
/// <summary>The Fee Types tab of Fee Configuration - the same editor-plus-records layout as Define Classes, replacing the Fee Type Manager dialog (and the "+" beside the fee-type dropdown that opened it).</summary>
public sealed class FeeTypesPage : MasterPage
{
    protected override bool ReserveEditorValidationSpace => true;
    protected override int EditorValidationLines => 2;
    protected override bool EditorValidationSpansCaption => true;
    private FormValidator? _validator;
    protected override bool CanSaveRecord()
    {
        if (_validator is null)
        {
            _validator = CreateValidator();
            BindEditorValidationMessages(_validator);
        }
        _validator.Begin();
        _validator.RequiredFields(this);
        return _validator.Finish();
    }
    protected override void EditorCleared(bool afterSave) { base.EditorCleared(afterSave); _validator?.ClearAll(); }
    protected override void RecordLoaded(DataGridViewRow row) { base.RecordLoaded(row); _validator?.ClearAll(); }
    private static readonly string[] Frequencies =
    {
        "Monthly",
        "Yearly",
        "As Needed"
    };
    public FeeTypesPage(bool splitEditorAndRecords = true) : this(splitEditorAndRecords, true) { }
    internal FeeTypesPage(bool splitEditorAndRecords, bool initialize)
        : base(
            "Fee Types",
            "Mark required fees as Mandatory, link transport fees, then enter class rates.",
            "GetFeeTypes",
            "SaveFeeTypeWithDefaultFrequency",
            new[]
            {
                new FieldSpec(
                    "FeeTypeCode",
                    "Code",
                    Required: true),
                new FieldSpec(
                    "FeeTypeName",
                    UiText.Fees.FeeType,
                    Required: true),
                new FieldSpec(
                    "DefaultFrequency",
                    "Charge frequency",
                    FieldKind.ComboString,
                    Required: true,
                    Options: Frequencies),
                new FieldSpec(
                    "Description",
                    "Description",
                    MaxLength: SchemaLengths.FeeTypeDescription),
                new FieldSpec("IsMandatory", "Mandatory fee", FieldKind.Boolean),
                new FieldSpec("IsTransportLinked", "Transport linked", FieldKind.Boolean),
                new FieldSpec("DisplayOrder", "Display Order", FieldKind.Integer),
            },
            v => new[]
            {
                DatabaseHelper.P(
                    "@UserId",
                    CurrentUser.UserId),
                DatabaseHelper.Int(
                    "@FeeTypeId",
                    Convert.ToInt32(
                        v["Id"] ?? 0)),
                DatabaseHelper.P(
                    "@FeeTypeCode",
                    v["FeeTypeCode"]),
                DatabaseHelper.P(
                    "@FeeTypeName",
                    v["FeeTypeName"]),
                DatabaseHelper.P(
                    "@Description",
                    v["Description"] ?? string.Empty),
                DatabaseHelper.Int(
                    "@DisplayOrder",
                    Convert.ToInt32(v["DisplayOrder"] ?? 0)),
                DatabaseHelper.P(
                    "@DefaultFrequency",
                    v["DefaultFrequency"]),
                DatabaseHelper.Bit(
                    "@IsMandatory",
                    Convert.ToBoolean(v["IsMandatory"])),
                DatabaseHelper.Bit(
                    "@IsTransportLinked",
                    Convert.ToBoolean(v["IsTransportLinked"]))
            },
            splitEditorAndRecords: splitEditorAndRecords,
            reorderProcedure: "ReorderFeeTypes",
            reorderItemField: "FeeTypeId",
            getParams: () => new[]
            {
                DatabaseHelper.Int(
                    "@UserId",
                    CurrentUser.UserId)
            },
            deleteProcedure:
                "DeleteFeeType",
            deleteIdParameter:
                "@FeeTypeId",
            deleteDisplayField:
                "FeeTypeName",
            deleteBlockedProcedure:
                "GetFeeTypeDeleteBlockedIds", initialize: initialize)
    {
        // Both switches form one row directly below Description, starting at
        // the description input rather than repeating captions on the left.
        PlaceBooleanOptionsBelow("Description", "IsMandatory", "IsTransportLinked");
    }
    /// <summary>Formats the complete fee-type result without another database read.</summary>
    protected override DataTable ShapeRecords(DataTable table)
    {
        if (table.Columns.Contains("Description"))
            table.Columns["DefaultFrequency"]!.SetOrdinal(table.Columns["Description"]!.Ordinal + 1);
        if (table.Columns.Contains("IsSystemDefined"))
            table.Columns.Remove("IsSystemDefined");
        foreach (DataRow row in table.Rows)
            if (table.Columns.Contains("Description") && row["Description"] == DBNull.Value)
                row["Description"] = string.Empty;
        return table;
    }
    /// <summary>Tells the fee screens that read fee types (the Configuration tab's drop-down, Apply To Existing Students) that the list has changed.</summary>
    protected override void AfterSave(
        int id,
        Dictionary<string, object?> values) =>
        FeeTypeCatalog.NotifyChanged();
    /// <summary>A deleted fee type must leave the Configuration tab's drop-down and Apply To Existing Students too, not only after the next save.</summary>
    protected override void AfterDelete(int id) =>
        FeeTypeCatalog.NotifyChanged();
}
