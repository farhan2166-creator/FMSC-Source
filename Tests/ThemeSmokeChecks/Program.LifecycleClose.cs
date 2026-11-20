using System.Data;
using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Students.Admissions;
using Microsoft.Data.SqlClient;

internal static partial class Program
{
    private static void CheckDepartureLayout()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        using var dialog=new CloseAdmissionDialog(41,"Synthetic student",false,(_,_)=>throw new Exception("Layout must not query SQL"));
        T Field<T>(string name)=>(T)typeof(CloseAdmissionDialog).GetField(name,flags)!.GetValue(dialog)!;
        var root=Field<TableLayoutPanel>("root");
        var policy=Field<ModernComboBox>("monthlyPolicy");
        var month=Field<ModernDatePicker>("waiveFrom");
        var payment=Field<ModernComboBox>("paidReduction");
        using var host=new Form { AutoScaleMode=AutoScaleMode.None,ShowInTaskbar=false,
            StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000),ClientSize=new Size(820,740) };
        host.Controls.Add(root);host.Show();Application.DoEvents();
        Directory.CreateDirectory("artifacts/departure");
        foreach(int width in new[]{680,820,1100})
        foreach(int choice in new[]{0,1,2})
        {
            host.ClientSize=new Size(width,740);policy.SelectedIndex=choice;Application.DoEvents();
            Check(month.Visible==(choice==2),"Waiver month appears only for the chosen-month policy");
            Check(payment.Enabled==(choice!=1),"Retaining issued bills disables the unused payment-release choice");
            foreach(var control in new Control[]{policy,payment,Field<ModernTextBox>("reason"),Field<ModernTextBox>("impact")})
            {
                var bounds=host.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                Check(host.ClientRectangle.Contains(bounds),$"Departure {control.GetType().Name} fits at {width}px, choice {choice}");
            }
            var impact=Field<ModernTextBox>("impact");
            Check(impact.Height>=AppMetrics.TextHeight(impact.Font)+impact.Padding.Vertical,
                "Departure impact retains readable space without fixing an exact text-box height");
            Check(!Field<ModernButton>("apply").Enabled,"Changing fee choices requires a fresh review");
            if(width==820&&choice!=1)
            {
                using var bitmap=new Bitmap(root.Width,root.Height);root.DrawToBitmap(bitmap,root.ClientRectangle);
                bitmap.Save(Path.GetFullPath($"artifacts/departure/{(choice==0?"school-cutoff":"selected-month")}.png"));
            }
        }
        host.Close();
    }

    private static void CheckLifecycleCloseGuard()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (bool legacy in new[] { false, true })
        {
            var replies = new Queue<TaskCompletionSource<DataTable>>();
            var calls = new List<SqlParameter[]>();
            Task<DataTable> Query(string procedure, SqlParameter[] parameters)
            {
                calls.Add(parameters);
                var reply = new TaskCompletionSource<DataTable>(); replies.Enqueue(reply); return reply.Task;
            }
            using ApplicationWindowForm dialog = legacy ? new LegacyGoLiveDialog(Query) : new CloseAdmissionDialog(41, "Synthetic student", false, Query);
            IEnumerable<Control> Children(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Children(c)));
            var buttons = Children(dialog).OfType<ModernButton>().ToArray();
            var review = buttons.Single(b => b.Text == (legacy ? "Preview Go Live" : "Review"));
            var apply = buttons.Single(b => b.Text == (legacy ? "Confirm & Go Live" : "Apply"));
            void Click(Control button) => typeof(Control).GetMethod("OnClick", flags)!.Invoke(button, new object[] { EventArgs.Empty });
            bool RefusesClose()
            {
                var args = new FormClosingEventArgs(CloseReason.UserClosing, false);
                typeof(Form).GetMethod("OnFormClosing", flags)!.Invoke(dialog, new object[] { args }); return args.Cancel;
            }
            DataTable Plan()
            {
                var rows = new DataTable();
                foreach (string column in new[] { "Snapshot", "ChargeCount", "Outstanding", "FutureCount", "FutureAmount", "FuturePaid", "BlockedCount", "ActiveStudents", "UnverifiedStudents", "ReviewDetails" }) rows.Columns.Add(column);
                rows.Columns.Add("PaidReturnAmount", typeof(decimal)).DefaultValue = 0m;
                rows.Rows.Add("synthetic-review", "0", "0", "0", "0", "0", "0", "1", "0", "Synthetic fixture only"); return rows;
            }
            if(!legacy)
            {
                var policy=(ModernComboBox)typeof(CloseAdmissionDialog).GetField("monthlyPolicy",flags)!.GetValue(dialog)!;
                policy.SelectedIndex=1;
                Check(policy.Items.Count==3,"Departure offers school cutoff, retained bills and a chosen waiver month");
            }
            Click(review);
            if(!legacy)Check(Convert.ToString(calls[0].Single(p=>p.ParameterName=="@MonthlyFeePolicy").Value)=="KeepIssued",
                "The operator's retained-bill decision is submitted explicitly instead of a locked cutoff flag");
            Check(calls.Count == 1 && RefusesClose() && Children(dialog).OfType<ModernDatePicker>().All(d => !d.Enabled),
                "Pending lifecycle review holds all inputs and refuses title-bar/Alt+F4 close");
            Click(review); Click(apply);
            Check(calls.Count == 1, "Pending lifecycle review cannot be re-entered or applied");
            // Force an input event despite disabled UI: the old reply must be discarded.
            Children(dialog).OfType<ModernDatePicker>().First().Value = DateTime.Today.AddDays(-1);
            replies.Dequeue().SetResult(Plan());
            PumpUntil(() => review.Enabled, "Obsolete lifecycle review settles");
            Check(!apply.Enabled && !RefusesClose(), "An obsolete review is not offered for Apply and an idle dialog can be abandoned");
            Click(review); replies.Dequeue().SetResult(Plan());
            PumpUntil(() => apply.Enabled, "Current lifecycle review enables Apply");
            Click(apply);
            Check(calls.Count == 3 && RefusesClose(), "Lifecycle apply retains its modal owner until the write finishes");
            Check(Convert.ToString(calls.Last().Single(p => p.ParameterName == "@ExpectedSnapshot").Value) == "synthetic-review",
                "Apply retains the reviewed SQL snapshot contract");
            Click(apply); Check(calls.Count == 3, "Lifecycle apply refuses a second write");
            replies.Dequeue().SetException(new InvalidOperationException("Synthetic refusal"));
            PumpUntil(() => review.Enabled, "Failed lifecycle apply restores controls");
            Check(!apply.Enabled && !RefusesClose(), "A failed write requires a fresh review and releases closing safely");
            Click(review); replies.Dequeue().SetResult(Plan());
            PumpUntil(() => apply.Enabled, "Retry lifecycle review settles");
            Click(apply); replies.Dequeue().SetResult(new DataTable());
            PumpUntil(() => legacy ? ((LegacyGoLiveDialog)dialog).Changed : ((CloseAdmissionDialog)dialog).Changed,
                "Successful lifecycle write records Changed before success close");
            Check(dialog.DialogResult == DialogResult.OK, "Successful lifecycle operation returns OK for caller refresh");
        }
    }
}
