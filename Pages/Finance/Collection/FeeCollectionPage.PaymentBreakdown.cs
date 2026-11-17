using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Finance.Collection;

public sealed partial class FeeCollectionPage
{
    private DataTable familyPaymentBreakdowns=new();
    private int historyReceiptChild;
    private bool changingHistorySelection;

    private void ShowWholeReceiptHistory()
    {
        var receipts=familyPaymentBreakdowns.AsEnumerable()
            .GroupBy(row=>GetText(row,"ReceiptNumber"),StringComparer.OrdinalIgnoreCase)
            .OrderBy(group=>Convert.ToDateTime(group.First()["TransactionDate"]))
            .ThenBy(group=>Convert.ToDateTime(group.First()["CreatedAt"])).ToArray();
        var references=receipts.Select(group=>group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var row in recentPayments.Rows.Cast<DataGridViewRow>().ToArray())
        {
            if(references.Contains(GetText(row.Cells["ReceiptNumber"].Value)))
                recentPayments.Rows.Remove(row);
            else
            {
                // Older entries have no saved receipt balance. Never substitute today's dues.
                if(row.Cells["ReceiptTotal"].Value is not null and not DBNull)
                    row.Cells["Amount"].Value=row.Cells["ReceiptTotal"].Value;
                row.Cells["ReceiptTotal"].Value=null;
                row.Cells["BalanceThen"].Value=null;
            }
        }
        foreach(var receipt in receipts)
        {
            var members=receipt.ToArray();var first=members[0];
            decimal paid=GetDecimal(first,"OriginalTotal");
            decimal adjusted=members.Sum(row=>GetDecimal(row,"Adjusted"));
            decimal net=members.Sum(row=>GetDecimal(row,"NetSettled"));
            bool recorded=members.All(row=>!row.IsNull("BalanceRemaining"));
            decimal due=recorded?members.Sum(row=>GetDecimal(row,"BalanceRemaining")):0m;
            string activity=members.All(row=>GetText(row,"PaymentStatus")=="Reversed")?"Reversed"
                :members.Any(row=>GetText(row,"PaymentStatus")=="Partially reversed"||GetText(row,"PaymentStatus")=="Reversed")?"Partially reversed"
                :adjusted!=0?"Adjusted":"Payment";
            recentPayments.Rows.Insert(0,FormatDate(first,"TransactionDate"),receipt.Key,
                members.Length==1?"1 student":$"{members.Length} students",activity,paid,GetText(first,"PaymentMethod"),0);
            var added=recentPayments.Rows[0];
            added.Cells["ReceiptTotal"].Value=recorded?paid+due:null;
            added.Cells["BalanceThen"].Value=recorded?due:null;
            added.Cells["Adjusted"].Value=adjusted;added.Cells["NetPaid"].Value=net;
            added.Cells["Amount"].ToolTipText=$"Whole receipt originally paid: Rs. {paid:N0}; adjustments: Rs. {adjusted:N0}; net payment: Rs. {net:N0}. Select to see each student's share.";
            added.Tag=true;
        }
        if(recentPayments.Rows.Count>0)recentPayments.CurrentCell=recentPayments.Rows[0].Cells["PaymentDate"];
    }

    private void SelectHistoryReceipt(DataGridView? children=null)
    {
        if(changingHistorySelection || paymentHistoryDetails is null)return;
        changingHistorySelection=true;
        try
        {
            historyReceiptChild=children?.CurrentRow?.Tag is int id?id:0;
            if(children is not null)
            {
                recentPayments.ClearSelection();
                if(children.CurrentRow is not null)children.CurrentRow.Selected=true;
            }
            else
            {
                foreach(var grid in paymentHistoryDetails.Controls.OfType<DataGridView>())grid.ClearSelection();
                if(recentPayments.CurrentRow is not null)recentPayments.CurrentRow.Selected=true;
            }
            UpdateReprintAvailability();
        }
        finally{changingHistorySelection=false;}
    }

    private async Task LoadFamilyPaymentBreakdownsAsync(int selectedAdmission,int version)
    {
        try
        {
            int[] selected=SelectedBillStudents();
            var data=await ReceiptPaymentBreakdown.ReadForStudentsAsync(selected);
            if(IsDisposed||version!=loadVersion||selectedAdmission!=admissionId){data.Dispose();return;}
            if(!selected.SequenceEqual(SelectedBillStudents())){data.Dispose();return;}
            familyPaymentBreakdowns.Dispose();familyPaymentBreakdowns=data;
        }
        catch(Exception ex){AppExceptionHandler.LogNonFatal(ex,"Recent Payments sibling breakdown");}
    }

    private bool ShowFamilyPaymentBreakdown(string reference)
    {
        if(paymentHistoryDetails is not {IsDisposed:false} panel)return false;
        if(panel.Tag is ValueTuple<DataTable,string> shown&&ReferenceEquals(shown.Item1,familyPaymentBreakdowns)&&shown.Item2==reference)return true;
        var members=familyPaymentBreakdowns.AsEnumerable()
            .Where(row=>string.Equals(GetText(row,"ReceiptNumber"),reference,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(members.Length==0)return false;
        SetPaymentHistoryMinimum(380);
        decimal original=GetDecimal(members[0],"OriginalTotal"),adjusted=members.Sum(row=>GetDecimal(row,"Adjusted")),net=members.Sum(row=>GetDecimal(row,"NetSettled"));
        var grid=HumanUi.Grid(false);
        grid.Name="receiptSiblingBreakdown";grid.Dock=DockStyle.Fill;grid.ReadOnly=true;
        grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill;
        grid.Columns.Add("Student","Student");
        foreach(var (name,title) in new[]{("Original","Original Payment"),("Adjusted","Adjustments"),("Net","Net Payment")})
        {
            grid.Columns.Add(name,title);
            grid.Columns[name].ValueType=typeof(decimal);
            grid.Columns[name].DefaultCellStyle.Format="N0";
            grid.Columns[name].DefaultCellStyle.Alignment=DataGridViewContentAlignment.MiddleRight;
            grid.Columns[name].MinimumWidth=AppScale.Scale(100);
            grid.Columns[name].FillWeight=20;
        }
        grid.Columns.Add("Status","Status");grid.Columns["Status"].MinimumWidth=AppScale.Scale(112);grid.Columns["Status"].FillWeight=24;
        grid.Columns["Original"].ToolTipText="The student's original payment on this receipt, including any advance used (Rs.).";
        grid.Columns["Adjusted"].ToolTipText="Changes to the original payment, such as refunds, corrections or reversals (Rs.).";
        grid.Columns["Net"].ToolTipText="Payment remaining after adjustments (Rs.). This is not the student's outstanding fee balance.";
        grid.Columns["Student"].MinimumWidth=AppScale.Scale(170);grid.Columns["Student"].FillWeight=44;
        foreach(DataGridViewColumn column in grid.Columns)column.SortMode=DataGridViewColumnSortMode.NotSortable;
        HumanUi.ApplyGridStyle(grid,AppTheme.Primary);
        // The modal breakdown uses the same shared centered header/value
        // alignment as the main fee grids. Student names remain left aligned.
        UiHelper.AlignDialogGrid(grid);
        grid.ColumnHeadersHeight=AppScale.Scale(34);grid.RowTemplate.Height=AppScale.Scale(28);
        int selected=-1;
        foreach(var member in members)
        {
            int index=grid.Rows.Add($"{GetText(member,"AdmissionNumber")} · {GetText(member,"StudentName")}",
                GetDecimal(member,"CashApplied")+GetDecimal(member,"CreditApplied"),GetDecimal(member,"Adjusted"),GetDecimal(member,"NetSettled"),GetText(member,"PaymentStatus"));
            grid.Rows[index].Tag=GetInt(member,"AdmissionId");
            if(GetInt(member,"AdmissionId")==admissionId)selected=index;
        }
        if(selected>=0)grid.CurrentCell=grid.Rows[selected].Cells[0];
        grid.ClearSelection();
        grid.Enter+=(_,_)=>SelectHistoryReceipt(grid);
        grid.CellClick+=(_,e)=>{if(e.RowIndex>=0)SelectHistoryReceipt(grid);};
        grid.CurrentCellChanged+=(_,_)=>{if(grid.ContainsFocus)SelectHistoryReceipt(grid);};
        grid.Layout+=(_,_)=>FitFamilyPaymentBreakdown(grid);
        string totals=$"Originally paid: Rs. {original:N0}    Adjustments: Rs. {adjusted:N0}    Net payment: Rs. {net:N0}";
        string heading=$"{reference} · Payments by student on this receipt";
        heading+=$"\r\nCash received: Rs. {GetDecimal(members[0],"ReceiptCashReceived"):N0} · Advance used: Rs. {GetDecimal(members[0],"ReceiptCreditApplied"):N0}";
        panel.SuspendLayout();
        try
        {
            foreach(Control child in panel.Controls.Cast<Control>().ToArray())child.Dispose();
            panel.Controls.Clear();panel.RowStyles.Clear();panel.RowCount=3;
            for(int i=0;i<3;i++)panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var title=new Label{Text=heading,AutoSize=true,Dock=DockStyle.Fill,Font=AppTheme.NormalFont};
            var total=new Label{Text=totals,AutoSize=true,Dock=DockStyle.Fill,Font=AppTheme.ButtonFont,Margin=new Padding(0,4,0,0)};
            panel.Controls.Add(title,0,0);panel.SetColumnSpan(title,4);
            panel.Controls.Add(grid,0,1);panel.SetColumnSpan(grid,4);
            FitFamilyPaymentBreakdown(grid);
            panel.Controls.Add(total,0,2);panel.SetColumnSpan(total,4);
            panel.Text=heading+Environment.NewLine+totals;
            panel.Tag=(familyPaymentBreakdowns,reference);
        }
        finally{panel.ResumeLayout(true);}
        return true;
    }

    private void SetPaymentHistoryMinimum(int height)
    {
        if(paymentHistoryDetails?.Parent is TableLayoutPanel root&&root.Parent is ResponsiveViewport viewport)
        {
            root.MinimumSize=new Size(0,AppScale.Scale(height));
            viewport.PerformLayout();
        }
    }

    private void FitFamilyPaymentBreakdown(DataGridView grid)
    {
        if(paymentHistoryDetails is not {IsDisposed:false} panel)return;
        int rowHeight=grid.Rows.Count>0?grid.Rows[0].Height:grid.RowTemplate.Height;
        int width=Math.Max(AppScale.Scale(300),panel.ClientSize.Width-panel.Padding.Horizontal);
        int textHeight=panel.Controls.OfType<Label>().Sum(label=>label.GetPreferredSize(new Size(width,0)).Height+label.Margin.Vertical);
        int formHeight=panel.FindForm()?.ClientSize.Height??AppScale.Scale(520);
        int available=formHeight-AppScale.Scale(240)
            -textHeight-panel.Padding.Vertical-panel.Margin.Vertical-grid.Margin.Vertical;
        int visibleRows=Math.Clamp((available-grid.ColumnHeadersHeight-AppScale.Scale(6))/Math.Max(1,rowHeight),1,formHeight<AppScale.Scale(480)?2:3);
        int height=grid.ColumnHeadersHeight+rowHeight*Math.Min(visibleRows,grid.Rows.Count)+AppScale.Scale(6);
        if(grid.MinimumSize.Height==height&&grid.MaximumSize.Height==height)return;
        grid.MinimumSize=Size.Empty;grid.MaximumSize=new Size(0,height);grid.MinimumSize=new Size(0,height);grid.Height=height;
        if(grid.CurrentCell is not null&&grid.IsHandleCreated)grid.FirstDisplayedScrollingRowIndex=grid.CurrentCell.RowIndex;
    }
}
