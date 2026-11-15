using System.Reflection;
using System.Data;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Finance.Configuration;

internal static partial class Program
{
    private static void CheckFeeSetupReview()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, flags)!.GetValue(owner)!;
        void Call(object owner, string name) => owner.GetType().GetMethod(name, flags)!.Invoke(owner, null);
        Directory.CreateDirectory("artifacts/fee-setup-review");
        using var matrix = new FeeRateMatrixForm(null, false);
        using var copy = new CopyFeeRatesForm(0, 0, false);
        using var bulk = new BulkFeeRateChangesForm(0, 0, false);
        foreach (var form in new Form[] { matrix, copy, bulk })
        {
            if (form == matrix) matrix.PrepareForEmbedding();
            else if (form == copy) copy.PrepareForEmbedding();
            else bulk.PrepareForEmbedding();
            using var host = new Form { ClientSize = new Size(1600, 740) };
            form.Dock = DockStyle.Fill;
            host.Controls.Add(form);
            form.Show(); host.Show(); Application.DoEvents();
            var actions = form.Controls.Find("FeeToolActions", true).Single();
            foreach (int width in new[] { 1600, 1000 })
            {
                host.ClientSize = new Size(width, 740); Application.DoEvents();
                Check(HorizontallyCentered(actions.Bounds, actions.Parent!.ClientRectangle),
                    form.GetType().Name + " keeps its action row centered and within its parent after resize");
                Check(actions.Controls.Cast<Control>().Where(control => control.Visible).Select(control => control.Top).Distinct().Count() == 1,
                    form.GetType().Name + " keeps visible actions on one line");
            }
            host.ClientSize = new Size(1600, 740); Application.DoEvents();
            using var image = new Bitmap(host.Width, host.Height);
            host.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            image.Save("artifacts/fee-setup-review/" + form.GetType().Name + ".png");
            host.Controls.Remove(form);
        }
        foreach (string invalid in new[] { "", "0", "-1", "12.5", "abc", "100x", "1000001" })
        {
            Field<ModernNumericTextBox>(bulk, "adjustmentValue").Text = invalid;
            Call(bulk, "RunPreview");
            Check(!Field<ModernButton>(bulk, "applyButton").Enabled &&
                Field<Label>(bulk, "summaryLabel").Text.Contains("Enter", StringComparison.OrdinalIgnoreCase),
                "Bulk preview rejects invalid adjustment before SQL: " + invalid + " [" + Field<Label>(bulk, "summaryLabel").Text + "]");
        }
        Call(copy, "RefreshPreview");
        Check(!Field<ModernButton>(copy, "applyButton").Enabled, "Copy cannot apply without source/target selections");
        typeof(CopyFeeRatesForm).GetField("loading", flags)!.SetValue(copy, true);
        using var campuses = new DataTable(); campuses.Columns.Add("CampusId", typeof(int)); campuses.Columns.Add("CampusName"); campuses.Rows.Add(1, "Test Campus"); campuses.Rows.Add(2, "Other Campus");
        foreach (string name in new[] { "sourceCampus", "targetCampus" })
        {
            var field = Field<ModernComboBox>(copy, name); field.BindingContext = new BindingContext();
            field.DisplayMember = "CampusName"; field.ValueMember = "CampusId"; field.DataSource = campuses.Copy(); field.SelectedValue = 1;
        }
        typeof(CopyFeeRatesForm).GetField("loading", flags)!.SetValue(copy, false);
        Call(copy, "RefreshPreview");
        Check(Field<Label>(copy, "summaryLabel").Text.Contains("different destination"), "Copy refuses the same source and target before SQL");
        typeof(CopyFeeRatesForm).GetField("loading", flags)!.SetValue(copy, true);
        Field<ModernComboBox>(copy, "targetCampus").SelectedValue = 2;
        typeof(CopyFeeRatesForm).GetField("loading", flags)!.SetValue(copy, false);
        Call(copy, "RefreshPreview");
        Check(Field<Label>(copy, "summaryLabel").Text.Contains("Choose a class"), "Copy refuses an unselected class before SQL");
        using var feeTypes = new FeeTypesPage(true, false);
        var fields = (Dictionary<string, Control>)typeof(MasterPage).GetField("_controls", flags)!.GetValue(feeTypes)!;
        fields["FeeTypeName"].Text = "55555";
        ((ModernComboBox)fields["DefaultFrequency"]).SelectedIndex = 0;
        using var feeHost = new Form { ClientSize = new Size(1700, 700) };
        feeTypes.Dock = DockStyle.Fill; feeHost.Controls.Add(feeTypes); feeHost.Show(); Application.DoEvents();
        Check(!(bool)typeof(FeeTypesPage).GetMethod("CanSaveRecord", flags)!.Invoke(feeTypes, null)!, "Numeric-only fee names fail validation before SQL");
        var labels = (Dictionary<Control, Label>)typeof(MasterPage).GetField("_editorValidationLabels", flags)!.GetValue(feeTypes)!;
        Label message = labels[fields["FeeTypeName"]];
        Check(message.Visible && ((TableLayoutPanel)message.Parent!).GetColumnSpan(message) == 3, "Fee name errors use a full row below the field");
        using var feeImage = new Bitmap(feeHost.Width, feeHost.Height); feeHost.DrawToBitmap(feeImage, new Rectangle(Point.Empty, feeImage.Size));
        feeImage.Save("artifacts/fee-setup-review/FeeTypesPage.png");
    }
}
