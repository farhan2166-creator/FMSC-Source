using System.Data;
using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Finance.Collection;
using Microsoft.Data.SqlClient;

internal static partial class Program
{
    private static void CheckReceiptBreakdown()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        using var page=new FeeCollectionPage(false);var type=page.GetType();
        T Field<T>(string name)=>(T)type.GetField(name,flags)!.GetValue(page)!;
        type.GetField("admissionId",flags)!.SetValue(page,42);type.GetField("enrollmentId",flags)!.SetValue(page,1);
        typeof(AdmissionNumbers).GetField("_numbers",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,new Dictionary<int,string>{{42,"6590"}});
        typeof(AdmissionNumbers).GetField("_loadedAt",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,DateTime.UtcNow);
        var data=Field<DataTable>("familyPaymentBreakdowns");
        foreach(string name in new[]{"ReceiptNumber","AdmissionNumber","StudentName","PaymentStatus","PaymentMethod"})data.Columns.Add(name);
        data.Columns.Add("TransactionDate",typeof(DateTime));data.Columns.Add("CreatedAt",typeof(DateTime));
        data.Columns.Add("BalanceRemaining",typeof(decimal));
        data.Columns.Add("AdmissionId",typeof(int));
        foreach(string name in new[]{"CashApplied","CreditApplied","OriginalTotal","ReceiptCashReceived","ReceiptCreditApplied","Adjusted","NetSettled"})data.Columns.Add(name,typeof(decimal));
        void Member(string receipt,int id,string number,string name,decimal share,decimal adjusted,decimal total,string status)
        {
            var row=data.NewRow();row["ReceiptNumber"]=receipt;row["AdmissionNumber"]=number;row["StudentName"]=name;
            row["PaymentStatus"]=status;row["AdmissionId"]=id;row["CashApplied"]=share;row["CreditApplied"]=0m;
            row["OriginalTotal"]=total;row["ReceiptCashReceived"]=total;row["ReceiptCreditApplied"]=0m;row["Adjusted"]=adjusted;row["NetSettled"]=share-adjusted;data.Rows.Add(row);
            row["TransactionDate"]=row["CreatedAt"]=new DateTime(2026,10,27);row["PaymentMethod"]="Cash";row["BalanceRemaining"]=500m;
        }
        Member("FR-2026-00007",41,"6510a","Test One",7500,0,22000,"Paid");
        Member("FR-2026-00007",42,"6590","Test Two",7600,7600,22000,"Reversed");
        Member("FR-2026-00007",43,"6591","Test Three",6900,0,22000,"Paid");
        for(int i=0;i<10;i++)Member("LARGE-FAMILY",51+i,"S"+i,"A sibling with a longer student name "+i,1000,0,10000,"Paid");
        var history=Field<DataGridView>("recentPayments");
        Member("SIBLING-ONLY",99,"S99","Sibling alone",2800,0,2800,"Paid");
        type.GetMethod("ShowWholeReceiptHistory",flags)!.Invoke(page,null);
        var whole=history.Rows.Cast<DataGridViewRow>().Single(row=>Convert.ToString(row.Cells["ReceiptNumber"].Value)=="FR-2026-00007");
        Check(history.Rows.Count==3&&Convert.ToDecimal(whole.Cells["Amount"].Value)==22000m
            &&Convert.ToDecimal(whole.Cells["ReceiptTotal"].Value)==23500m&&Convert.ToDecimal(whole.Cells["BalanceThen"].Value)==1500m,
            "Upper history has one row per receipt with whole-receipt total, original paid and saved due, rather than the main student's share");
        Check(history.Rows.Cast<DataGridViewRow>().Any(row=>Convert.ToString(row.Cells["ReceiptNumber"].Value)=="SIBLING-ONLY"),
            "A checked sibling's individual receipt remains visible even without the main student on it");
        data.Rows[0]["BalanceRemaining"]=DBNull.Value;
        type.GetMethod("ShowWholeReceiptHistory",flags)!.Invoke(page,null);
        whole=history.Rows.Cast<DataGridViewRow>().Single(row=>Convert.ToString(row.Cells["ReceiptNumber"].Value)=="FR-2026-00007");
        Check(whole.Cells["ReceiptTotal"].Value is null&&whole.Cells["BalanceThen"].Value is null,
            "Missing historical dues remain unknown rather than being replaced by zero or today's balance");
        data.Rows[0]["BalanceRemaining"]=500m;data.Rows.Remove(data.Rows[data.Rows.Count-1]);history.Rows.Clear();
        int Add(string reference,decimal amount,decimal total)
        {int index=history.Rows.Add("27-Oct-2026",reference,"Fees","Reversed",amount,"Cash",0);history.Rows[index].Cells["Adjusted"].Value=amount;history.Rows[index].Cells["NetPaid"].Value=0m;history.Rows[index].Cells["ReceiptTotal"].Value=total;history.Rows[index].Tag=true;return index;}
        int family=Add("FR-2026-00007",7600,22000),large=Add("LARGE-FAMILY",1000,10000),single=Add("R-2026-00001",2800,0);
        history.Rows[single].Cells["ReceiptTotal"].Value=null;
        var originals=history.Rows.Cast<DataGridViewRow>().Select(row=>row.Cells.Cast<DataGridViewCell>().Select(cell=>cell.Value).ToArray()).ToArray();
        Directory.CreateDirectory("artifacts/receipt-breakdown");Exception? failure=null;
        using var timer=new System.Windows.Forms.Timer{Interval=100};
        timer.Tick+=(_,_)=>
        {
            timer.Stop();var dialog=Application.OpenForms.Cast<Form>().First(form=>form.Text.StartsWith("Recent Payments —"));
            try
            {
                foreach(var size in new[]{new Size(1180,520),new Size(720,420)})
                {
                    dialog.ClientSize=size;history.CurrentCell=history.Rows[family].Cells["ReceiptNumber"];Application.DoEvents();
                    var details=Field<TableLayoutPanel>("paymentHistoryDetails");var grid=details.Controls.OfType<DataGridView>().Single();
                    Check(grid.Rows.Count==3&&Convert.ToDecimal(grid.Rows[1].Cells["Original"].Value)==7600m&&Convert.ToDecimal(grid.Rows[1].Cells["Net"].Value)==0m,"A reversed child retains the original share beside the current zero");
                    Check(Convert.ToDecimal(grid.Rows[0].Cells["Net"].Value)==7500m&&Convert.ToDecimal(grid.Rows[2].Cells["Net"].Value)==6900m,"Unaffected siblings keep their own recorded payments");
                    Check(details.Text.Contains("22,000")&&details.Text.Contains("7,600")&&details.Text.Contains("14,400"),"The original receipt total and current family adjustments are distinct");
                    Check(grid.SelectedRows.Count==0,"The breakdown starts without an active child copy");
                    var currentGrid=grid;type.GetMethod("UpdatePaymentHistoryDetails",flags)!.Invoke(page,null);
                    Check(ReferenceEquals(currentGrid,details.Controls.OfType<DataGridView>().Single()),"Repeated selection events reuse the displayed table without flickering");
                    var reprint=Field<ModernButton>("reprintReceiptButton");
                    grid.CurrentCell=grid.Rows[2].Cells[0];
                    type.GetMethod("SelectHistoryReceipt",flags)!.Invoke(page,new object?[]{grid});
                    Check(Field<int>("historyReceiptChild")==43 && history.SelectedRows.Count==0 && reprint.Enabled,
                        "A child selection targets that saved member, clears the upper highlight and permits reprinting");
                    type.GetMethod("SelectHistoryReceipt",flags)!.Invoke(page,new object?[]{null});
                    Check(Field<int>("historyReceiptChild")==0 && grid.SelectedRows.Count==0 && history.SelectedRows.Count==1 && reprint.Enabled,
                        "Returning to the upper receipt restores the full family target and permits reprinting");
                    using(var bitmap=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save($"artifacts/receipt-breakdown/family-{size.Width}.png");}
                    Check(history.Height>70&&details.RectangleToScreen(details.ClientRectangle).Bottom<=reprint.Parent!.RectangleToScreen(reprint.Parent.ClientRectangle).Top,$"The breakdown fits beneath payments without covering actions or removing the payment list (payments {history.Height}, table {grid.Height}, window {size})");
                    Check(reprint.RectangleToScreen(reprint.ClientRectangle).Bottom<=dialog.RectangleToScreen(dialog.ClientRectangle).Bottom&&grid.RectangleToScreen(grid.ClientRectangle).Right<=dialog.RectangleToScreen(dialog.ClientRectangle).Right,"The full sibling table width and receipt actions remain inside the smaller window");
                    foreach(var member in data.AsEnumerable().Where(row=>row.Field<string>("ReceiptNumber")=="FR-2026-00007"))
                    {member["ReceiptCashReceived"]=21500m;member["ReceiptCreditApplied"]=500m;}
                    data.Rows[0]["CashApplied"]=7000m;data.Rows[0]["CreditApplied"]=500m;
                    history.CurrentCell=history.Rows[single].Cells["ReceiptNumber"];history.CurrentCell=history.Rows[family].Cells["ReceiptNumber"];Application.DoEvents();
                    Check(details.Text.Contains("21,500")&&details.Text.Contains("Advance used: Rs. 500")&&history.Height>70,
                        $"Mixed cash and credit remain distinct and fit below the payment list (payments {history.Height}, window {size})");
                    foreach(var member in data.AsEnumerable().Where(row=>row.Field<string>("ReceiptNumber")=="FR-2026-00007"))
                    {member["ReceiptCashReceived"]=22000m;member["ReceiptCreditApplied"]=0m;}
                    data.Rows[0]["CashApplied"]=7500m;data.Rows[0]["CreditApplied"]=0m;
                    history.CurrentCell=history.Rows[large].Cells["ReceiptNumber"];Application.DoEvents();
                    var largeGrid=details.Controls.OfType<DataGridView>().Single();
                    Check(largeGrid.Rows.Count==10 && largeGrid.DisplayedRowCount(false)>0 && history.Height>0,
                        $"Large-family breakdown retains every child and a readable payment list (table {largeGrid.Height}, payments {history.Height}, window {size})");
                    Check(largeGrid.DisplayedRowCount(false)>=largeGrid.Rows.Count ||
                          largeGrid.ScrollBars is ScrollBars.Vertical or ScrollBars.Both,
                        "Long family lists permit vertical scrolling when not all students fit");
                    history.CurrentCell=history.Rows[single].Cells["ReceiptNumber"];Application.DoEvents();
                    Check(!details.Controls.OfType<DataGridView>().Any()&&details.Text.Contains("2,800")&&!details.Text.Contains("22,000"),"Selecting an individual receipt clears the previous sibling table");
                }
                Check(history.Rows.Cast<DataGridViewRow>().Select((row,i)=>row.Cells.Cast<DataGridViewCell>().Select(cell=>cell.Value).SequenceEqual(originals[i])).All(value=>value),"Displaying the breakdown does not alter amounts, status or reprint eligibility in the payment list");
                history.Rows.Clear();Application.DoEvents();
                Check(Field<TableLayoutPanel>("paymentHistoryDetails").Text.StartsWith("Select a payment"),"Clearing the selection removes the old family's amounts");
            }
            catch(Exception ex){failure=ex;}
            finally{dialog.Close();}
        };
        timer.Start();type.GetMethod("OpenPaymentHistory",flags)!.Invoke(page,null);
        if(failure is not null)throw failure;
        Check(!history.IsDisposed&&Field<TableLayoutPanel?>("paymentHistoryDetails") is null,"Closing the dialog preserves the page's payment grid and releases the sibling table controls");
    }

    private static void CheckReceiptBreakdownSql()
    {
        string connectionText=Environment.GetEnvironmentVariable("FMSC_RECEIPT_READ_CONNECTION")??throw new InvalidOperationException("Set the read-only receipt connection.");
        int admission=int.Parse(Environment.GetEnvironmentVariable("FMSC_RECEIPT_READ_ADMISSION")!);
        using var connection=new SqlConnection(connectionText);connection.Open();
        using var command=new SqlCommand(ReceiptPaymentBreakdown.Sql,connection);
        command.Parameters.AddWithValue("@UserId",1);command.Parameters.AddWithValue("@AdmissionId",admission);
        using var data=new DataTable();using(var adapter=new SqlDataAdapter(command))adapter.Fill(data);
        Check(data.Rows.Count>0,"Saved sibling receipts are available for this read-only comparison");
        using var existing=new SqlCommand("GetStudentCollectionReceipts",connection){CommandType=CommandType.StoredProcedure};
        existing.Parameters.AddWithValue("@UserId",1);existing.Parameters.AddWithValue("@AdmissionId",admission);
        using var current=new DataSet();using(var adapter=new SqlDataAdapter(existing))adapter.Fill(current);
        foreach(var group in data.AsEnumerable().GroupBy(row=>row.Field<string>("ReceiptNumber")))
        {
            Check(group.Sum(row=>row.Field<decimal>("CashApplied")+row.Field<decimal>("CreditApplied"))==group.First().Field<decimal>("OriginalTotal"),"The original sibling shares reconcile exactly to the saved receipt total");
            var selected=group.Single(row=>row.Field<int>("AdmissionId")==admission);
            var old=current.Tables[0].AsEnumerable().Single(row=>row.Field<string>("ReceiptNumber")==group.Key);
            Check(selected.Field<decimal>("Adjusted")==old.Field<decimal>("Adjusted")&&selected.Field<decimal>("NetSettled")==old.Field<decimal>("NetSettled"),"The sibling table uses the existing adjustment and net-settlement figures exactly");
        }
        Console.WriteLine($"Compared {data.AsEnumerable().Select(row=>row.Field<string>("ReceiptNumber")).Distinct().Count()} saved family receipts without writes.");
    }
}
