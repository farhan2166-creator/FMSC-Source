using System.Data;
using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Finance.Collection;
using FMSC_SMS.Pages.Finance.StudentAccounts;

internal static partial class Program
{
    private static void CheckSharedGridAlignment()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        using var window=new Form{ClientSize=new Size(1440,860),ShowInTaskbar=false,
            StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000)};
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        for(int i=0;i<4;i++)layout.RowStyles.Add(new RowStyle(SizeType.Percent,25));
        window.Controls.Add(layout);
        using var students=HumanUi.Grid(false);
        students.DataError+=(_,e)=>throw new InvalidOperationException("Student alignment fixture",e.Exception);
        using var source=new DataTable();
        source.Columns.Add("AdmissionNumber");source.Columns.Add("StudentName");
        source.Columns.Add("EnrollmentDate",typeof(DateTime));source.Columns.Add("Total",typeof(decimal));
        source.Columns.Add("Count",typeof(int));source.Columns.Add("Selected",typeof(bool));
        source.Rows.Add("001a","Fictional Student With A Longer Name",DateTime.Today,1200m,3,true);
        source.Rows.Add("002","Another Fictional Student",DBNull.Value,0m,0,false);
        students.DataSource=source;students.Dock=DockStyle.Fill;layout.Controls.Add(students,0,0);
        using var assignments=new StudentFeeAssignmentsPanel(false);
        var fees=(DataGridView)typeof(StudentFeeAssignmentsPanel).GetField("grid",flags)!.GetValue(assignments)!;
        fees.DataError+=(_,e)=>throw new InvalidOperationException("Fee alignment fixture",e.Exception);
        fees.Dock=DockStyle.Fill;layout.Controls.Add(fees,0,1);
        var feeRow=fees.Rows[fees.Rows.Add()];
        feeRow.Cells["FeeTypeName"].Value="Fictional Tuition";
        feeRow.Cells["StandardAmount"].Value=6000m;feeRow.Cells["DiscountAmount"].Value=300m;feeRow.Cells["NetAmount"].Value=5700m;
        feeRow.Cells["Frequency"].Value="Monthly";feeRow.Cells["EffectiveFrom"].Value="Oct 2026";
        feeRow.Cells["EffectiveTo"].Value="Mar 2027";feeRow.Cells["PostedThrough"].Value="Oct 2026";
        using var collection=new FeeCollectionPage(false);
        var payments=(DataGridView)typeof(FeeCollectionPage).GetField("recentPayments",flags)!.GetValue(collection)!;
        payments.DataError+=(_,e)=>throw new InvalidOperationException("Payment alignment fixture",e.Exception);
        payments.Dock=DockStyle.Fill;layout.Controls.Add(payments,0,2);
        var paymentRow=payments.Rows[payments.Rows.Add()];
        paymentRow.Cells["PaymentDate"].Value=DateTime.Today;paymentRow.Cells["ReceiptNumber"].Value="Fictional receipt";
        paymentRow.Cells["TransactionType"].Value="Collected";paymentRow.Cells["ChargeType"].Value="Tuition";
        paymentRow.Cells["PaymentMethod"].Value="Cash";paymentRow.Cells["PaymentId"].Value=0;
        paymentRow.Cells["Amount"].Value=300m;paymentRow.Cells["Adjusted"].Value=0m;paymentRow.Cells["NetPaid"].Value=300m;
        paymentRow.Cells["ReceiptTotal"].Value=2300m;
        using var statement=HumanUi.Grid(false);statement.Dock=DockStyle.Fill;
        statement.Columns.Add("Description","Description");
        foreach(string name in new[]{"DebitAmount","PaidAmount","Balance"})
        {
            statement.Columns.Add(name,name);statement.Columns[name].ValueType=typeof(decimal);
            statement.Columns[name].DefaultCellStyle.Alignment=DataGridViewContentAlignment.MiddleRight;
        }
        statement.Rows.Add("Fictional statement entry",6000m,3700m,2300m);
        statement.Rows.Add("Fictional credit",0m,300m,-300m);
        layout.Controls.Add(statement,0,3);
        window.Show();Application.DoEvents();
        var format=typeof(DataGridView).GetMethod("OnCellFormatting",flags,null,new[]{typeof(DataGridViewCellFormattingEventArgs)},null)!;
        void Alignment(DataGridView grid,string name,object sample)
        {
            var column=grid.Columns[name];
            var args=new DataGridViewCellFormattingEventArgs(column.Index,0,sample,typeof(string),new DataGridViewCellStyle(column.DefaultCellStyle));
            format.Invoke(grid,new object[]{args});
            Check(args.CellStyle!.Alignment==column.HeaderCell.InheritedStyle.Alignment,
                name+" displays data aligned with its heading regardless of chosen visual alignment");
        }
        foreach(int width in new[]{1440,1080,1440})
        {
            window.ClientSize=new Size(width,860);
            foreach(var grid in new[]{students,fees,payments,statement})
            {HumanUi.ApplyGridStyle(grid,AppTheme.Primary);UiHelper.FitColumnsToWidth(grid);}
            Application.DoEvents();
            Alignment(students,"AdmissionNumber","001a");
            Alignment(students,"EnrollmentDate",DateTime.Today);
            Alignment(students,"Total",1200m);
            Alignment(students,"Count",3);
            Alignment(students,"Selected",true);
            foreach(string name in new[]{"StandardAmount","DiscountAmount","NetAmount","EffectiveFrom","EffectiveTo","PostedThrough","Frequency"})
                Alignment(fees,name,name.Contains("Amount")?6000m:"Oct 2026");
            foreach(string name in new[]{"Amount","Adjusted","NetPaid","ReceiptTotal"})
                Alignment(payments,name,300m);
            foreach(string name in new[]{"DebitAmount","PaidAmount","Balance"})
                Alignment(statement,name,2300m);
            Check(students.Columns["StudentName"].DefaultCellStyle.Alignment is DataGridViewContentAlignment.NotSet or DataGridViewContentAlignment.MiddleLeft,
                "Student descriptions remain left-aligned");
            Check(statement.Columns["Description"].DefaultCellStyle.Alignment is DataGridViewContentAlignment.NotSet or DataGridViewContentAlignment.MiddleLeft,
                "Financial descriptions remain left-aligned");
        }
        Check(source.Rows[0].Field<string>("AdmissionNumber")=="001a"&&source.Rows[0].Field<decimal>("Total")==1200m,
            "Formatting preserves identifier text and money values");
        Check(!collection.IsHandleCreated&&!assignments.IsHandleCreated,"Detached real grids never start their parent SQL loaders");
        students.Sort(students.Columns["Total"],System.ComponentModel.ListSortDirection.Ascending);
        Check(students.SortedColumn==students.Columns["Total"]&&students.Columns["Total"].HeaderCell.SortGlyphDirection==SortOrder.Ascending,
            "Aligned headers retain native sorting and its ascending indicator");
        Application.DoEvents();
        Directory.CreateDirectory("artifacts/grid-alignment");
        using(var bitmap=new Bitmap(window.Width,window.Height))
        {window.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save("artifacts/grid-alignment/review.png");}
        window.Close();
    }
}
