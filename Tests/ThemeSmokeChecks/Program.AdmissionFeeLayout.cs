using System.Data;
using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Students.Admissions;

internal static partial class Program
{
    private static void CheckAdmissionFeeLayout()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var page = new StudentAdmissionPage();
        var type = typeof(StudentAdmissionPage);
        T Field<T>(string name) => (T)type.GetField(name, flags)!.GetValue(page)!;
        type.GetMethod("EnsureDetailsTabBuilt", flags)!.Invoke(page, null);
        var tab = Field<Panel>("tab2Panel");
        using var host = new Form { AutoScaleMode = AutoScaleMode.None, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ClientSize = new Size(1400, 720) };
        host.Controls.Add(tab); tab.Visible = true;
        host.Show(); Application.DoEvents();
        var root = tab.Controls.OfType<Panel>().Single().Controls.OfType<TableLayoutPanel>().Single();
        var fees = Field<DataGridView>("feeGrid");
        using var data = new DataTable();
        foreach (string name in new[] { "FeeTypeName", "Frequency", "ConcessionLabel" }) data.Columns.Add(name);
        foreach (string name in new[] { "StandardAmount", "DiscountAmount", "NetAmount" }) data.Columns.Add(name, typeof(decimal));
        data.Rows.Add("Tuition", "Monthly", "None", 2500m, 0m, 2500m);
        data.Rows.Add("Admission", "Once", "None", 1000m, 0m, 1000m);
        fees.DataSource = data;
        Field<Label>("feeStatus").Text = "2 fees selected.";
        Application.DoEvents();
        int newGridHeight = fees.Height;
        type.GetField("currentAdmissionId", flags)!.SetValue(page, 41);
        Field<CheckBox>("monthlyConcessionsFromAdmission").Visible = false;
        Application.DoEvents();
        Check(fees.Height > newGridHeight, "Existing admissions give the hidden concession option's space to the fee grid");
        Check(root.Height >= host.ClientSize.Height, "The fee workspace uses the full viewport height");
        Check(root.Controls.Cast<Control>().Where(control => control.Visible)
                .All(control => control.Width > 0 && control.Height > 0),
            "Visible fee and family sections retain usable workspace");
        Check(fees.ClientSize.Width > 0 && fees.Columns.Cast<DataGridViewColumn>()
                .Where(column => column.Visible).All(column => column.Width > 0),
            "Visible fee columns remain usable without requiring a fixed width distribution");
        Directory.CreateDirectory("artifacts");
        using (var bitmap = new Bitmap(tab.Width, tab.Height))
        {
            tab.DrawToBitmap(bitmap, tab.ClientRectangle);
            bitmap.Save(Path.GetFullPath("artifacts/admission-fees.png"));
        }
        for (int index = 0; index < 18; index++) data.Rows.Add("Extra fee " + index, "Monthly", "None", 100m, 0m, 100m);
        Application.DoEvents();
        int gridHeight = fees.Height;
        data.Rows.Add("Another fee", "Monthly", "None", 100m, 0m, 100m);
        Application.DoEvents();
        Check(fees.Rows.Count == data.Rows.Count && fees.DisplayedRowCount(false) < fees.Rows.Count,
            "Long fee lists retain all rows and allow scrolling without truncation");
        host.ClientSize = new Size(1400, 1000); Application.DoEvents();
        Check(root.Height >= host.ClientSize.Height && fees.Height >= gridHeight,
            "The fee workspace remains usable rather than shrinking in a taller window");
        var primary = Field<Panel>("tab1Panel");
        tab.Visible = false; host.Controls.Add(primary); primary.Visible = true;
        Application.DoEvents();
        var primaryViewport = primary.Controls.OfType<ResponsiveViewport>().Single();
        var primaryRoot = (TableLayoutPanel)primaryViewport.Controls[0];
        Check(primaryRoot.Height >= primaryViewport.ClientSize.Height,
            "The student information workspace covers its viewport and can overflow for scrolling");
        int informationHeight = primaryRoot.GetControlFromPosition(0, 0)!.Height;
        int placementHeight = primaryRoot.GetControlFromPosition(0, 1)!.Height;
        var informationCard = (HumanCard)primaryRoot.GetControlFromPosition(0, 0)!;
        var placementCard = (HumanCard)primaryRoot.GetControlFromPosition(0, 1)!;
        Check(informationCard.Height > informationCard.Header.Height &&
            placementCard.Height > placementCard.Header.Height,
            "Student information and placement retain visible content below their headers");
        var studentName = Field<ModernTextBox>("name");
        var birthplace = Field<ModernTextBox>("placeOfBirthInput");
        int FieldGap() => birthplace.PointToScreen(Point.Empty).Y - studentName.PointToScreen(Point.Empty).Y;
        int inputHeight = studentName.Height;
        host.ClientSize = new Size(1400, 1200); Application.DoEvents();
        Check(primaryRoot.GetControlFromPosition(0, 0)!.Height > informationHeight,
            "The student information section expands with the available height");
        Check(informationCard.Height >= informationHeight && placementCard.Height >= placementHeight,
            "A taller workspace does not compress either student information or placement");
        Check(FieldGap() > 0 && studentName.Height >= inputHeight && birthplace.Height > 0,
            "Resizing does not collapse or reverse the student information fields");
        using (var bitmap = new Bitmap(primary.Width, primary.Height))
        {
            primary.DrawToBitmap(bitmap, primary.ClientRectangle);
            bitmap.Save(Path.GetFullPath("artifacts/admission-information.png"));
        }
        host.ClientSize = new Size(1400, 350); Application.DoEvents();
        Check(primaryRoot.Height > primaryViewport.ClientSize.Height && primaryViewport.VerticalScroll.Visible,
            "Short windows scroll instead of compressing the admission fields");
        Check(!page.IsHandleCreated, "Fee layout verification never starts the admission SQL loader");
        host.Close();
    }
}
