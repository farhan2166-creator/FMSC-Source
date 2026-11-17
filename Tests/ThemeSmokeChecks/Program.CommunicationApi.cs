using FMSC_SMS.Core;
using FMSC_SMS.Pages.Communication;

internal static partial class Program
{
    private static void CheckCommunicationApiWorkflow()
    {
        var balanceTemplate=new CommunicationMetaTransport.Template("synthetic_balance","en","Balance {{1}}.","","");
        using(var mapping=new CommunicationMetaTemplateDialog(new[]{balanceTemplate},"FeeReminder",allowStudentFields:false))
        {
            mapping.Show();Application.DoEvents();
            Field<List<ModernComboBox>>(mapping,"mappings")[0].Text="[OutstandingAmount]";
            ((ModernButton)mapping.Controls.Find("communicationUseMetaTemplate",true).Single()).PerformClick();
            Check(mapping.Selection?.Fields.SequenceEqual(new[]{"[OutstandingAmount]"})==true,
                "A family fee Meta template can map its current balance without using one child's personal fields");
            mapping.Close();
        }
        var receiptTemplate=new CommunicationMetaTransport.Template("synthetic_receipt","en","Payment {{1}}, receipt {{2}}.","","");
        using(var mapping=new CommunicationMetaTemplateDialog(new[]{receiptTemplate},"Individual",allowStudentFields:false,
            contextFields:new Dictionary<string,string>{["[PaymentAmount]"]="500.00",["[ReceiptNumber]"]="R-42"}))
        {
            mapping.Show();Application.DoEvents();
            var fields=Field<List<ModernComboBox>>(mapping,"mappings");
            fields[0].Text="[PaymentAmount]";fields[1].Text="[ReceiptNumber]";
            ((ModernButton)mapping.Controls.Find("communicationUseMetaTemplate",true).Single()).PerformClick();
            Check(mapping.Selection?.Fields.SequenceEqual(new[]{"500.00","R-42"})==true
                && !CommunicationPreparedMessages.HasFields(mapping.Selection.Format()),
                "Receipt Meta fields retain the reviewed transaction values as constants instead of substituting a current fee balance");
            mapping.Close();
        }
        var schoolZone=TimeZoneInfo.FindSystemTimeZoneById("Pakistan Standard Time");
        using(var schedule=new CommunicationScheduleDialog(schoolZone))
        {
            schedule.Show();
            Application.DoEvents();
            var when=Field<DateTimePicker>(schedule,"when");
            var chosen=CommunicationSchoolTime.LocalNow(schoolZone).AddHours(2);
            when.Value=chosen;
            ((ModernButton)schedule.Controls.Find("communicationConfirmSchedule",true).Single()).PerformClick();
            Check(schedule.DialogResult==DialogResult.OK
                && schedule.Due.Offset==schoolZone.GetUtcOffset(chosen)
                && Math.Abs((schedule.Due.DateTime-chosen).TotalSeconds)<1,
                "Scheduling preserves the operator's school wall time and configured timezone independently of the PC");
            schedule.Close();
        }
        using(var page=new CommunicationCenterPage(false))
        {
            var method=Field<ModernComboBox>(page,"deliveryMethod");
            var body=Field<ModernTextBox>(page,"message");
            Check(method.Items.Count==3,"The composer offers manual WhatsApp, Meta and SMS without another sending subsystem");
            body.Text="Synthetic manual draft";method.SelectedIndex=1;
            Check(body.ReadOnly && body.Text=="" && Field<CommunicationPreview?>(page,"preview") is null
                && !Field<ModernButton>(page,"confirm").Enabled,"Changing to Meta invalidates the old review and requires an approved template");
            method.SelectedIndex=2;
            Check(!body.ReadOnly && Field<ModernComboBox>(page,"templateChoice").Items.Count>=2,
                "SMS uses the ordinary editable composer and prepared school messages");
            method.SelectedIndex=0;
            Check(!body.ReadOnly && !Field<ModernButton>(page,"confirm").Enabled,"Returning to manual cannot reuse an API confirmation");
        }
        Guid batch=Guid.NewGuid();int sends=0;string state="Pending";
        DataTable Read()
        {
            var rows=new DataTable();
            foreach(string column in new[]{"RecipientName","PhoneE164","State","Channel","ProviderCode","ProviderMessageId","RenderedBody"})rows.Columns.Add(column);
            rows.Columns.Add("RecipientId",typeof(long));rows.Columns.Add("AttemptId",typeof(long));
            rows.Rows.Add("Synthetic first","+923001234567",state,"Sms","Twilio",DBNull.Value,"Hello Synthetic first",1L,DBNull.Value);
            rows.Rows.Add("Synthetic second","+923011234567","Pending","Sms","Twilio",DBNull.Value,"Hello Synthetic second",2L,DBNull.Value);
            return rows;
        }
        var completion=new TaskCompletionSource<CommunicationDispatchStore.DispatchResult>();
        var operations=new CommunicationApiDispatchDialog.Operations(_=>Task.FromResult(Read()),(_,_,token)=>{
            sends++;
            token.Register(()=>{state="Unknown";completion.TrySetResult(new(true,1,"Unknown",null));});
            return completion.Task;
        },(_,_)=>throw new Exception("No status polling on open"),(_,_)=>throw new Exception("No retry on open"));
        using(var resume=new CommunicationApiDispatchDialog(batch,operations:operations))
        {
            resume.Show();PumpUntil(()=>Field<DataGridView>(resume,"grid").DataSource is DataTable
                && !Field<bool>(resume,"busy"),"Read saved API session");
            Check(sends==0 && Field<ModernButton>(resume,"send").Enabled,"Opening a saved API batch never dispatches before a fresh decision");
            Check(Field<ModernTextBox>(resume,"messagePreview").ReadOnly
                && Field<ModernTextBox>(resume,"messagePreview").Text=="Hello Synthetic first",
                "A resumed API session displays the exact saved message before any send decision");
            resume.Close();
        }
        using(var sending=new CommunicationApiDispatchDialog(batch,startConfirmed:true,operations:operations))
        {
            sending.Show();PumpUntil(()=>sends==1,"Start the explicitly confirmed API session");
            Check(Field<bool>(sending,"busy") && !Field<ModernButton>(sending,"send").Enabled,
                "A running API send blocks a duplicate click while remaining stoppable");
            Field<ModernButton>(sending,"stop").PerformClick();
            PumpUntil(()=>!Field<bool>(sending,"busy"),"Stop and record uncertainty");
            var grid=Field<DataGridView>(sending,"grid");
            var rows=(DataTable)grid.DataSource!;
            Check(sends==1 && Convert.ToString(rows.Rows[0]["State"])=="Unknown" && Convert.ToString(rows.Rows[1]["State"])=="Pending",
                "Stopping preserves the uncertain first attempt and does not send the remaining recipient");
            Check(!Field<ModernButton>(sending,"retry").Enabled,"An uncertain recipient is not offered a retry approval");
            sending.Close();
        }
    }
}
