using System.Reflection;
using FMSC_SMS.Core;

internal static partial class Program
{
    private sealed class ModalGuardFixture : Control,IBusyPage,IUnsavedChanges
    {
        public bool IsBusy{get;set;}
        public string BusyMessage=>"Operation pending";
        public bool HasUnsavedChanges{get;set;}
        public string UnsavedChangesMessage=>"Discard this draft?";
    }
    private static void CheckModalPageCloseGuard()
    {
        const BindingFlags flags=BindingFlags.Static|BindingFlags.NonPublic;
        using var dialog=new Form();using var page=new ModalGuardFixture();
        bool completed=false,discard=false;int asks=0;
        Func<bool> done=()=>completed;Func<string,bool> confirm=message=>{Check(message==page.UnsavedChangesMessage,"Modal guard names the current page draft");asks++;return discard;};
        var guard=typeof(ApplicationWindowForm).Assembly.GetType("FMSC_SMS.Core.ModalPageCloseGuard",true)!;
        guard.GetMethod("Attach",flags)!.Invoke(null,new object[]{dialog,page,done,confirm});
        bool Closing(CloseReason reason=CloseReason.UserClosing){var args=new FormClosingEventArgs(reason,false);typeof(Form).GetMethod("OnFormClosing",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(dialog,new object[]{args});return args.Cancel;}
        Check(!Closing()&&asks==0,"An untouched modal closes without a discard prompt");
        page.HasUnsavedChanges=true;Check(Closing()&&asks==1,"Stay blocks modal closure and retains its draft");
        discard=true;Check(!Closing()&&asks==2,"Deliberate discard permits a dirty modal to close");
        page.IsBusy=true;Check(Closing()&&Closing(CloseReason.ApplicationExitCall)&&asks==2,"Both user and application close are unconditionally blocked during a submitted operation");
        completed=true;Check(Closing()&&asks==2,"Completion cannot bypass an operation that is still running");
        page.IsBusy=false;Check(!Closing()&&asks==2,"A confirmed successful workflow closes without another draft-discard question");
    }
    private static void CheckDailyClosingLeaveContract()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        using var page=new FMSC_SMS.Pages.Finance.Collection.DailyCollectionClosingPage();using var owner=new Form();
        int asks=0;Func<string,bool> discard=_=>{asks++;return true;};
        var guard=typeof(ApplicationWindowForm).Assembly.GetType("FMSC_SMS.Core.ModalPageCloseGuard",true)!;
        guard.GetMethod("Attach",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object?[]{owner,page,null,discard});
        bool Closing(){var e=new FormClosingEventArgs(CloseReason.UserClosing,false);typeof(Form).GetMethod("OnFormClosing",flags)!.Invoke(owner,new object[]{e});return e.Cancel;}
        var busy=page.GetType().GetField("busy",flags)!;var dirty=page.GetType().GetField("dirty",flags)!;
        busy.SetValue(page,true);
        Check(((IBusyPage)page).IsBusy&&!page.HasUnsavedChanges&&Closing()&&asks==0,"A submitted daily closing is blocked rather than offered as a discardable draft");
        dirty.SetValue(page,true);
        Check(Closing()&&asks==0,"Actual unsaved notes cannot make an active closing write confirmable");
        busy.SetValue(page,false);
        Check(!Closing()&&asks==1&&page.HasUnsavedChanges,"Unsubmitted closing notes retain normal deliberate-discard behavior after the operation settles");
        dirty.SetValue(page,false);Check(!Closing()&&asks==1,"A clean settled closing leaves without prompting");
        Check(!page.IsHandleCreated,"Daily closing guard fixture never starts SQL reads or writes");
    }

}
