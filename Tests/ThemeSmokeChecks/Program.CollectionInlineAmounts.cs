using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Finance.Collection;

internal static partial class Program
{
    private static void CheckCollectionInlineAmounts()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var numbers=typeof(AdmissionNumbers).GetField("_numbers",BindingFlags.Static|BindingFlags.NonPublic)!;
        var stamp=typeof(AdmissionNumbers).GetField("_loadedAt",BindingFlags.Static|BindingFlags.NonPublic)!;
        var oldNumbers=numbers.GetValue(null);var oldStamp=stamp.GetValue(null);
        numbers.SetValue(null,new Dictionary<int,string>{{1,"2339"},{2,"1916"},{3,"2487"}});stamp.SetValue(null,DateTime.UtcNow);
        try
        {
            using var page=new FeeCollectionPage(false);
            var type=typeof(FeeCollectionPage);
            T Field<T>(string name)=>(T)type.GetField(name,flags)!.GetValue(page)!;
            void Set(string name,object value)=>type.GetField(name,flags)!.SetValue(page,value);
            void Call(string name,params object[] args)=>type.GetMethod(name,flags)!.Invoke(page,args);
            Set("admissionId",1);Set("enrollmentId",101);Set("suppressPaymentEvents",true);Set("suppressDiscountEvents",true);
            Field<Label>("primaryPayer").Text="2339 · Ahmad Nawaz\nNine - F · Current student";
            Set("suppressSiblingEvents",true);
            Call("AddSiblingChoice",2,102,"Hadia","1916 · Hadia\nNine - F · Sibling",true);
            Call("AddSiblingChoice",3,103,"Nawaz Khan Afridi","2487 · Nawaz Khan Afridi\nSix - F · Sibling",false);
            Set("suppressSiblingEvents",false);
            Field<Dictionary<int,decimal>>("siblingBaseOwed")[3]=900m;
            var grid=Field<DataGridView>("charges");
            void Bill(long id,int child,decimal amount)
            {
                var row=grid.Rows[grid.Rows.Add()];
                row.Cells["BillingChargeId"].Value=id;row.Cells["BillingChargeId"].Tag=0;
                row.Cells["AdmissionId"].Value=child;row.Cells["EnrollmentId"].Value=child+100;
                row.Cells["StudentName"].Value=child==1?"Ahmad Nawaz":"Hadia";row.Cells["StudentName"].Tag=(1,child.ToString());
                row.Cells["FeeTypeName"].Value="Tuition";row.Cells["BillingMonth"].Tag=DateTime.Today;
                row.Cells["BillingMonth"].Value=DateTime.Today.ToString("MMM yyyy");
                row.Cells["Balance"].Value=amount;row.Cells["GrossAmount"].Value=amount;row.Cells["GrossAmount"].Tag=0m;
                row.Cells["AppliedDiscount"].Value=0m;row.Cells["Amount"].Value=0m;row.Tag=false;
            }
            Bill(11,1,2400m);Bill(12,2,5200m);
            using var form=new Form{ClientSize=new Size(1620,940),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)};
            form.Controls.Add(page);form.Show();Application.DoEvents();Call("UpdateCollectionTotals");Application.DoEvents();
            UiHelper.ApplyGridPresentation(grid);UiHelper.FitColumnsToWidth(grid);Application.DoEvents();
            Check(new[]{"BillingMonth","GrossAmount","AppliedDiscount","Balance","Amount"}.All(name=>
                grid.Columns[name].HeaderCell.InheritedStyle.Alignment==DataGridViewContentAlignment.MiddleCenter
                &&grid.Rows[0].Cells[name].InheritedStyle.Alignment==DataGridViewContentAlignment.MiddleCenter),
                "Rendered collection headings and values remain centered after shared presentation and resizing");
            Check(new[]{"StudentName","FeeTypeName"}.All(name=>
                grid.Columns[name].HeaderCell.InheritedStyle.Alignment==DataGridViewContentAlignment.MiddleLeft
                &&grid.Rows[0].Cells[name].InheritedStyle.Alignment==DataGridViewContentAlignment.MiddleLeft),
                "Student and fee descriptions retain matching left-aligned headings and values");
            var list=Field<FamilyStudentList>("siblings");
            var current=Field<FamilyStudentList>("currentStudentAmounts");
            var lower=Field<DataGridView>("familyProposal");
            Check(lower.Parent is null,"Normal collection removes the obsolete lower split table");
            Check(current.Items.Count==1 &&
                (current.GetItemText(current.Items[0])??"").Contains("Ahmad Nawaz"),
                "The current student's compact summary appears above the siblings");
            Check(current.ClientSize.Height>=current.GetItemRectangle(0).Height && current.PaymentBounds(0).Bottom<=current.ClientSize.Height,
                "The current student's name, placement and amounts fit completely inside the restored row");
            Check(list.CompactRows && current.CompactRows && list.GetItemRectangle(0).Height<list.Font.Height*4+AppScale.Scale(38)
                && AppTheme.SmallFont.Size<list.Font.Size,
                "Compact student rows reserve a smaller font for total and pay labels");
            (decimal Total,decimal Pay) Info(int id)=>((decimal,decimal))type.GetMethod("InlinePaymentInfo",flags)!.Invoke(page,new object[]{id})!;
            Check(Info(2).Total==5200m && Info(2).Pay==0m,"An included sibling shows their total dues and current allocation");
            Check(Info(3).Total==900m && Info(3).Pay==0m,"Excluded siblings retain their total dues with zero allocated payment");
            Set("suppressPaymentEvents",false);
            Field<ModernTextBox>("paymentAmount").Text="3000";
            Check(Info(2).Total==5200m && Info(2).Pay==600m,
                "The existing amount-received allocation updates the sibling's read-only Pay label");
            Field<ModernTextBox>("paymentAmount").Text="4000";
            Check(Info(2).Total==5200m && Info(2).Pay==1600m,
                "Changing the received amount refreshes Pay without changing total dues");
            Check(Info(1).Total==2400m && Info(1).Pay==2400m,
                "The current student's read-only labels follow the same payment allocation");
            current.SetItemChecked(0,false);
            Check(current.GetItemChecked(0) && Info(1).Pay==2400m,
                "The current student stays included without duplicating or changing their payment");
            var bounds=list.PaymentBounds(0);var point=new Point(bounds.Left+10,bounds.Top+bounds.Height/2);
            var position=new IntPtr((point.Y<<16)|(point.X&0xffff));
            SearchInputMessage(list.Handle,0x0201,new IntPtr(1),position);
            SearchInputMessage(list.Handle,0x0202,IntPtr.Zero,position);
            Check(list.GetItemChecked(0) && !list.Controls.OfType<TextBox>().Any() && !current.Controls.OfType<TextBox>().Any(),
                "Total and Pay remain labels: clicking them opens no editor and preserves inclusion");
            Directory.CreateDirectory("artifacts/collection-inline-amounts");
            using var picture=new Bitmap(form.Width,form.Height);form.DrawToBitmap(picture,new Rectangle(Point.Empty,picture.Size));
            picture.Save("artifacts/collection-inline-amounts/inline-payments.png");
        }
        finally{numbers.SetValue(null,oldNumbers);stamp.SetValue(null,oldStamp);}
    }
}
