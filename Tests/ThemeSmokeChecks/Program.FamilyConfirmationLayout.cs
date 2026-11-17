using System.Data;
using FMSC_SMS.Core;

internal static partial class Program
{
    private static void CheckFamilyConfirmationLayout()
    {
        using var rows = new DataTable();
        rows.Columns.Add("Adm. No."); rows.Columns.Add("Student");
        rows.Columns.Add("Cash (Rs.)",typeof(decimal));
        rows.Columns.Add("Discount (Rs.)",typeof(decimal));
        rows.Columns.Add("Advance Used (Rs.)",typeof(decimal));
        rows.Rows.Add("162","Fictional sibling A",2100m,0m,0m);
        rows.Rows.Add("163","Fictional sibling B",2400m,0m,0m);
        rows.Rows.Add("291","Fictional sibling C",500m,0m,0m);
        rows.Rows.Add("423","Fictional sibling D s/o Fictional guardian",0m,0m,500m);
        rows.Rows.Add("","Total",5000m,0m,500m);
        using var dialog = ModernMessageDialog.CreateDialog(null, new MessageDialogContent(
            "Record payment for 4 students?",
            Explanation: "One family receipt will be issued. Each student's fees and advance remain in their own account.",
            Facts: new[] { ("Cash received","Rs. 5,000"), ("Advance used","Rs. 500"),
                ("Discount","Rs. 0"),("Advance left","Rs. 200"),
                ("Payment date","17-11-2026 11:04 AM"),("Method","Cash"),
                ("Loaded student","423 - Fictional sibling D s/o Fictional guardian") },
            Records: rows,
            Notice: "This payment is saved immediately. Review the amounts before confirming.",
            Style: MessageDialogStyle.FamilyCollection),
            "Confirm Family Collection",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2);
        dialog.Show();Application.DoEvents();
        IEnumerable<Control> All(Control c)=>c.Controls.Cast<Control>().SelectMany(x=>new[]{x}.Concat(All(x)));
        var controls=All(dialog).ToArray();
        var grid=controls.OfType<DataGridView>().Single(g=>g.Name=="MessageRecords");
        var viewport=controls.Single(c=>c.Name=="MessageViewport");
        var facts=controls.OfType<TableLayoutPanel>().Single(t=>t.Name=="MessageFacts");
        var notice=controls.OfType<Label>().Single(l=>l.Name=="MessageNotice");
        Check(facts.ColumnCount==4 && facts.RowCount>=3,
            "Family collection shows side-by-side financial facts instead of one tall single-student list");
        Check(controls.OfType<Label>().Any(l=>l.Name=="DetailLabel"&&l.Text=="Loaded student") &&
              controls.OfType<Label>().Any(l=>l.Name=="DetailValue"&&l.Text=="Rs. 200"),
            "Loaded student and unused selected advance are clearly identified");
        Check(grid.Columns.Count==5 && grid.Columns.Cast<DataGridViewColumn>().Any(c=>c.HeaderText.Contains("Advance Used")) &&
              grid.Rows.Cast<DataGridViewRow>().Count(row=>!row.IsNewRow)==5,
            "Family breakdown includes a separate advance column and a visible total row");
        // Dialogs follow the same content-aware fitter as form grids. These
        // tests intentionally avoid brittle pixel widths or FillWeight values.
        Check(grid.AutoSizeColumnsMode==DataGridViewAutoSizeColumnsMode.None &&
              grid.Columns.Cast<DataGridViewColumn>().All(c=>c.AutoSizeMode==DataGridViewAutoSizeColumnMode.None),
            "Family confirmation delegates column sizing to the shared form-grid policy");
        var studentColumn=grid.Columns["Student"]!;
        var admissionColumn=grid.Columns["Adm. No."]!;
        Check(studentColumn.Width>admissionColumn.Width,
            "Student names receive more space than short admission numbers based on their content");
        int originalStudentWidth=studentColumn.Width;
        string originalStudentName=Convert.ToString(rows.Rows[3]["Student"])!;
        rows.Rows[3]["Student"]=new string('W',110);
        UiHelper.FitColumnsToWidth(grid);
        Check(studentColumn.Width>originalStudentWidth,
            "A longer student name grows its dialog column through the shared width fitter");
        rows.Rows[3]["Student"]=originalStudentName;
        UiHelper.FitColumnsToWidth(grid);
        Check(grid.Columns.Cast<DataGridViewColumn>()
                  .Where(c=>c.Visible && c.ValueType==typeof(decimal))
                  .All(c=>c.DefaultCellStyle.Alignment==DataGridViewContentAlignment.MiddleCenter &&
                          c.HeaderCell.InheritedStyle.Alignment==DataGridViewContentAlignment.MiddleCenter) &&
              admissionColumn.DefaultCellStyle.Alignment==DataGridViewContentAlignment.MiddleCenter &&
              studentColumn.DefaultCellStyle.Alignment==DataGridViewContentAlignment.MiddleLeft,
            "Shared fitting retains centered headings and money/identifier values with left-aligned student names");
        var students=grid.Rows.Cast<DataGridViewRow>().Where(r=>!r.IsNewRow).Take(4).ToArray();
        var total=grid.Rows[4];
        Check(total.Cells["Student"].Value?.ToString()=="Total" &&
              students.Sum(r=>Convert.ToDecimal(r.Cells["Cash (Rs.)"].Value))==
                  Convert.ToDecimal(total.Cells["Cash (Rs.)"].Value) &&
              students.Sum(r=>Convert.ToDecimal(r.Cells["Advance Used (Rs.)"].Value))==
                  Convert.ToDecimal(total.Cells["Advance Used (Rs.)"].Value) &&
              Convert.ToDecimal(total.Cells["Cash (Rs.)"].Value)==5000m &&
              Convert.ToDecimal(total.Cells["Advance Used (Rs.)"].Value)==500m,
            "The displayed totals reconcile exactly with each child's separate cash and advance");
        Check(total.DefaultCellStyle.Font?.Bold==true &&
              grid.Columns.Cast<DataGridViewColumn>().All(c=>c.SortMode==DataGridViewColumnSortMode.NotSortable),
            "The total row remains emphasized and cannot be sorted among students");
        Check(grid.Rows[3].Cells["Student"].Value?.ToString()?.Contains("(this student)")==false,
            "Family rows do not duplicate the loaded-student label and identity");
        Check(notice.Top>grid.Top && !notice.Font.Bold &&
              notice.Text.Contains("saved immediately",StringComparison.OrdinalIgnoreCase),
            "The compact warning follows the breakdown rather than dominating the summary");
        Check(grid.RectangleToScreen(grid.ClientRectangle).Bottom<=
              viewport.RectangleToScreen(viewport.ClientRectangle).Bottom,
            "All four students and the total row fit above the decision buttons");
        var decisions=controls.OfType<Button>()
            .Where(button=>button.DialogResult is DialogResult.Yes or DialogResult.No).ToArray();
        Check(decisions.Length==2 && decisions.All(button=>button.Visible&&button.Width>0&&button.Height>0) &&
              decisions.Any(button=>button.DialogResult==DialogResult.Yes&&button.Text.Contains("Record Payment")),
            "Payment confirmation retains clear and usable Yes/No choices");
        Check(ReferenceEquals(dialog.AcceptButton,decisions.Single(button=>button.DialogResult==DialogResult.No)),
            "The dialog still respects its explicit default choice");
        Directory.CreateDirectory("artifacts/billing-ui");
        using var bitmap=new Bitmap(dialog.Width,dialog.Height);
        dialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
        bitmap.Save("artifacts/billing-ui/family-confirmation.png");
        dialog.Close();
    }
}
