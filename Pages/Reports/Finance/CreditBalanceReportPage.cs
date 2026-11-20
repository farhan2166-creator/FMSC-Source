using FMSC_SMS.Core;
using Microsoft.Data.SqlClient;
namespace FMSC_SMS.Pages.Reports.Finance;

public sealed class CreditBalanceReportPage : CanonicalFinanceReportPage
{
    private readonly ModernNumericTextBox minimum = new() { Minimum = 0, Maximum = 1000000000m, ZeroAsPlaceholder = true };
    public CreditBalanceReportPage() : base("Student Advance Balance Register",
        "Unused advance held for students. Location filters use each student's current or latest placement.",
        "CreditBalanceRegister", "FMSC_Credit_Balances", new[] { "Students", "Families" }, Array.Empty<string>(),false,false,false,false)
    {
        minimum.TextChanged += (_, _) => FiltersChanged();
        Grid.ColumnWidthChanged+=(_,_)=>QueueFamilyRowSizing();
        Grid.CellPainting+=PaintFamilyChild;
        if(Grid.Parent?.Parent is HumanCard resultCard)resultCard.Header.Visible=false;
    }
    protected override string ReportLayoutKey=>base.ReportLayoutKey+(ViewName=="Families"?".compact-family":"");
    protected override IReadOnlyList<string> ReportRequiredColumnKeys=>ViewName=="Families"
        ?new[]{"Family / Student","Members","Student Advances","Available Credit"}:base.ReportRequiredColumnKeys;
    protected override IEnumerable<(string, Control)> AdditionalReportFilters() => new[] { ("Minimum advance per student (Rs.)",(Control)minimum) };
    protected override string AdditionalFilterSignature => NumericFilterSignature(minimum.Text);
    protected override string AdditionalExportFileScope =>
        minimum.Value > 0 ? "min Rs " + minimum.Value.ToString("#,##0.##") : "";
    protected override string AdditionalFilterDescription => (minimum.Value>0?$"Minimum advance per student: Rs. {minimum.Value:#,##0.##}. ":"")+"Only students matching the filters are included.";
    protected override IReadOnlyList<ReportColumnDef> DefineReportColumns()=>base.DefineReportColumns().Select(column=>column with {
        Header=column.Key switch {
            "Available Credit"=>ViewName=="Families"?"Family Total":"Available Advance",
            "Students"=>"Students with Advance","Members"=>"Children / Class","Family / Student"=>"Father / Parent",
            "Student Advances"=>"Advance",
            "Last Activity"=>"Last Advance Activity",_=>column.Header
        },DefaultVisible=ViewName!="Families"||column.Key is "Family / Student" or "Members" or "Student Advances" or "Available Credit"
    }).ToArray();
    protected override void OnReportPageBound()
    {
        base.OnReportPageBound();
        Grid.DefaultCellStyle.WrapMode=DataGridViewTriState.True;
        Grid.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.DisplayedCells;
        Grid.ColumnHeadersDefaultCellStyle.WrapMode=DataGridViewTriState.True;
        Grid.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        foreach(DataGridViewColumn column in Grid.Columns)
            column.DefaultCellStyle.WrapMode=DataGridViewTriState.True;
        Grid.AutoResizeRows(DataGridViewAutoSizeRowsMode.DisplayedCells);
        if(ViewName=="Families")
        {
            // Establish minima before enabling fill calculations on a newly bound grid.
            Grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.None;
            foreach(DataGridViewColumn column in Grid.Columns)column.AutoSizeMode=DataGridViewAutoSizeColumnMode.None;
            foreach(DataGridViewColumn column in Grid.Columns)
            {
                if(!column.Visible)continue;
                column.MinimumWidth=DpiLayout.Pixels(column.Name=="Members"?260:column.Name=="Family / Student"?160:110,Grid.DeviceDpi);
                column.FillWeight=column.Name=="Members"?48:column.Name=="Family / Student"?26:13;
            }
            Grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill;
            foreach(DataGridViewColumn column in Grid.Columns)
                if(column.Visible)column.AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;
            Grid.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.None;
            SizeFamilyRows();
        }
    }
    private bool sizingFamilies;
    private bool familySizingQueued;
    private void QueueFamilyRowSizing()
    {
        if(familySizingQueued||sizingFamilies||!Grid.IsHandleCreated||Grid.IsDisposed)return;
        familySizingQueued=true;
        Grid.BeginInvoke((Action)(()=>{familySizingQueued=false;if(!Grid.IsDisposed)SizeFamilyRows();}));
    }
    private int ChildHeight(FamilyAdvanceLayout.Child child,int width)=>
        TextRenderer.MeasureText(child.Name,Grid.Font,new Size(width,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPadding).Height+
        TextRenderer.MeasureText(child.Placement,AppTheme.SmallFont,new Size(width,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPadding).Height+12;
    private void SizeFamilyRows()
    {
        if(sizingFamilies||ViewName!="Families"||!Grid.Columns.Contains("Members"))return;
        sizingFamilies=true;
        try
        {
            int width=Math.Max(40,Grid.Columns["Members"].Width-16);
            foreach(DataGridViewRow row in Grid.Rows)
                if(row.DataBoundItem is DataRowView view&&view.Row.Table.Columns.Contains(FamilyAdvanceLayout.Column))
                    row.Height=Math.Max(row.MinimumHeight,FamilyAdvanceLayout.Read(Convert.ToString(view.Row[FamilyAdvanceLayout.Column])!).Children.Sum(child=>ChildHeight(child,width))+4);
        }
        finally{sizingFamilies=false;}
    }
    private void PaintFamilyChild(object? sender,DataGridViewCellPaintingEventArgs e)
    {
        if(ViewName!="Families"||e.RowIndex<0||e.ColumnIndex<0||e.Graphics is null||
            Grid.Columns[e.ColumnIndex].Name is not ("Members" or "Student Advances")||
            Grid.Rows[e.RowIndex].DataBoundItem is not DataRowView view)return;
        var family=FamilyAdvanceLayout.Read(Convert.ToString(view.Row[FamilyAdvanceLayout.Column])!);
        e.PaintBackground(e.ClipBounds,true);
        bool amounts=Grid.Columns[e.ColumnIndex].Name=="Student Advances";
        bool selected=(e.State&DataGridViewElementStates.Selected)!=0;
        Color foreground=selected?e.CellStyle!.SelectionForeColor:e.CellStyle!.ForeColor;
        int y=e.CellBounds.Top+4,width=Math.Max(40,Grid.Columns["Members"].Width-16);
        var state=e.Graphics.Save();e.Graphics.SetClip(e.CellBounds);
        foreach(var child in family.Children)
        {
            int height=ChildHeight(child,width);
            var bounds=new Rectangle(e.CellBounds.Left+8,y,e.CellBounds.Width-16,height-8);
            if(amounts)TextRenderer.DrawText(e.Graphics,child.Advance.ToString("#,##0.##"),Grid.Font,bounds,foreground,TextFormatFlags.Right|TextFormatFlags.Top|TextFormatFlags.NoPadding);
            else
            {
                int nameHeight=TextRenderer.MeasureText(child.Name,Grid.Font,new Size(width,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPadding).Height;
                TextRenderer.DrawText(e.Graphics,child.Name,Grid.Font,bounds,foreground,TextFormatFlags.WordBreak|TextFormatFlags.NoPadding);
                bounds.Y+=nameHeight;bounds.Height=Math.Max(1,height-nameHeight-8);
                TextRenderer.DrawText(e.Graphics,child.Placement,AppTheme.SmallFont,bounds,selected?foreground:AppTheme.MutedText,TextFormatFlags.WordBreak|TextFormatFlags.NoPadding);
            }
            y+=height;
            if(!ReferenceEquals(child,family.Children[^1]))
            {
                using var separator=new Pen(AppTheme.Border);
                e.Graphics.DrawLine(separator,e.CellBounds.Left+8,y-4,e.CellBounds.Right-8,y-4);
            }
        }
        e.Graphics.Restore(state);e.Paint(e.ClipBounds,DataGridViewPaintParts.Border);e.Handled=true;
    }
    protected override bool AppendReportTotals => false;
    protected override IEnumerable<SqlParameter> BuildParameters()
    {
        yield return DatabaseHelper.Int("@UserId",CurrentUser.UserId);
        yield return DatabaseHelper.Int("@CampusId",Campus.SelectedId);
        yield return DatabaseHelper.Int("@ClassId",Class.SelectedId);
        yield return DatabaseHelper.Int("@SectionId",Section.SelectedId);
    }
    protected override async Task<DataSet> ReadReportAsync(SqlParameter[] parameters, CancellationToken token)
    {
        var rows = await Task.Run(() => DatabaseHelper.QuerySql(CreditBalanceReportData.Query,parameters),token);
        var result = new DataSet(); result.Tables.Add(rows); return result;
    }
    protected override Func<DataTable, DataTable?, (DataTable Rows, DataTable? Totals)> CaptureResultTransform(SqlParameter[] parameters)
    {
        bool families = ViewName == "Families"; decimal amount = minimum.Value;
        return (rows, _) => { var result = CreditBalanceReportData.Create(rows,families,amount); return (result.Rows,result.Totals); };
    }
}
