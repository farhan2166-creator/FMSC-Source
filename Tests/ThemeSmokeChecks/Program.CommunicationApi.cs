using FMSC_SMS.Core;
using FMSC_SMS.Pages.Communication;

internal static partial class Program
{
    private static void CheckCommunicationApiWorkflow()
    {
        string evidenceRoot=Path.Combine(Path.GetTempPath(),"Fmsc-Synthetic-Dispatch-"+Guid.NewGuid().ToString("N"));
        byte[] entropy=System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        try
        {
            byte[] Seal(byte[] value)=>System.Security.Cryptography.ProtectedData.Protect(value,entropy,System.Security.Cryptography.DataProtectionScope.CurrentUser);
            byte[] Open(byte[] value)=>System.Security.Cryptography.ProtectedData.Unprotect(value,entropy,System.Security.Cryptography.DataProtectionScope.CurrentUser);
            var journal=new CommunicationAttemptJournal(evidenceRoot,Seal,Open);
            var identity=new CommunicationAttemptJournal.Identity(new string('A',64),Guid.NewGuid(),"Student",42,
                new string('B',64),"Sms","Twilio",new string('C',64));
            var attempt=new CommunicationAttemptJournal.Attempt(1,Guid.NewGuid(),"Sms","Twilio");
            var claim=journal.RecordClaim(identity,attempt);
            journal.RecordResult(claim,"Accepted","SM"+new string('a',32));
            var recovered=new CommunicationAttemptJournal(evidenceRoot,Seal,Open);
            recovered.AssertConsistent(identity,new[]{attempt});
            Check(recovered.ReadResult(identity,attempt.AttemptKey)?.State=="Accepted",
                "Windows account protection preserves exact attempt and provider evidence across journal reopening");
            bool unreadable=false;
            var wrongContext=new CommunicationAttemptJournal(evidenceRoot,Seal,value=>System.Security.Cryptography.ProtectedData.Unprotect(
                value,new byte[32],System.Security.Cryptography.DataProtectionScope.CurrentUser));
            try{wrongContext.AssertConsistent(identity,new[]{attempt});}
            catch(System.Security.Cryptography.CryptographicException){unreadable=true;}
            Check(unreadable,"Windows refuses submission evidence opened with the wrong protection context");
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(entropy);
            if(Directory.Exists(evidenceRoot))Directory.Delete(evidenceRoot,true);
        }
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
            rows.Rows.Add("Synthetic first","+923001234567",state,"Sms","Twilio",DBNull.Value,"Hello Synthetic first",1L,state=="Pending" ? DBNull.Value : 1L);
            rows.Rows.Add("Synthetic second","+923011234567","Pending","Sms","Twilio",DBNull.Value,"Hello Synthetic second",2L,DBNull.Value);
            return rows;
        }
        int blockedSends=0;
        DataTable RecoveryRows()
        {
            var rows=Read();rows.Columns.Add("RecoveryBlocked",typeof(bool));
            rows.Columns.Add("HoldForReview",typeof(bool));
            foreach(DataRow row in rows.Rows){row["RecoveryBlocked"]=true;row["HoldForReview"]=true;}
            return rows;
        }
        var blockedOperations=new CommunicationApiDispatchDialog.Operations(_=>Task.FromResult(RecoveryRows()),
            (_,_,_)=>{blockedSends++;throw new Exception("No restored provider request");},
            (_,_)=>throw new Exception("No automatic reconciliation"),(_,_)=>throw new Exception("No recovery retry"));
        using(var restored=new CommunicationApiDispatchDialog(Guid.NewGuid(),startConfirmed:true,operations:blockedOperations))
        {
            restored.Show();PumpUntil(()=>Field<DataGridView>(restored,"grid").DataSource is DataTable && !Field<bool>(restored,"busy"),"Read restored API hold");
            Check(blockedSends==0 && !Field<ModernButton>(restored,"send").Enabled &&
                !Field<ModernButton>(restored,"releaseHold").Enabled && Field<ModernButton>(restored,"cancelPending").Enabled,
                "Restored approvals cannot auto-send or resume; cancellation of unsent recipients remains available.");
            restored.Close();
        }
        var schoolDue=new DateTime(2035,2,3,17,45,0);
        using(var scheduled=new CommunicationApiDispatchDialog(Guid.NewGuid(),operations:blockedOperations with {Read=_=>{
            var rows=Read();rows.Columns.Add("DueUtc",typeof(DateTime));rows.Columns.Add("DueSchool",typeof(DateTime));rows.Columns.Add("SchoolTimeZone");
            foreach(DataRow row in rows.Rows){row["DueUtc"]=new DateTime(2035,2,3,12,45,0);row["DueSchool"]=schoolDue;row["SchoolTimeZone"]="Pakistan Standard Time";}
            return Task.FromResult(rows);
        }}))
        {
            scheduled.Show();PumpUntil(()=>Field<DataGridView>(scheduled,"grid").DataSource is DataTable && !Field<bool>(scheduled,"busy"),"Read school-time schedule");
            Check(Field<Label>(scheduled,"status").Text.Contains(schoolDue.ToString("dd MMM yyyy h:mm tt")) &&
                !Field<ModernButton>(scheduled,"send").Enabled && blockedSends==0,
                "A future API schedule displays the authoritative school time and cannot send early.");
            scheduled.Close();
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
        int reconciles=0;
        var recoveryOperations=operations with {Reconcile=(_,_)=>{
            reconciles++;state="Accepted";return Task.FromResult("Accepted");
        }};
        using(var recovery=new CommunicationApiDispatchDialog(batch,operations:recoveryOperations))
        {
            recovery.Show();PumpUntil(()=>Field<DataGridView>(recovery,"grid").DataSource is DataTable
                && !Field<bool>(recovery,"busy"),"Read uncertain saved API session");
            Check(Field<ModernButton>(recovery,"check").Enabled,
                "An uncertain SQL attempt can check independently retained provider evidence when its SQL reference was lost");
            Field<ModernButton>(recovery,"check").PerformClick();
            PumpUntil(()=>!Field<bool>(recovery,"busy"),"Reconcile retained provider reference");
            Check(reconciles==1 && sends==1 && Convert.ToString(((DataTable)Field<DataGridView>(recovery,"grid").DataSource!).Rows[0]["State"])=="Accepted",
                "Explicit reconciliation updates the saved outcome without invoking another send");
            recovery.Close();
        }
    }
}
