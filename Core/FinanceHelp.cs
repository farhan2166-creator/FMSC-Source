using System.Runtime.CompilerServices;

namespace FMSC_SMS.Core;

/// <summary>Operator examples shared by financial screens; no calculations or transaction codes.</summary>
internal static class FinanceHelp
{
    internal enum Topic
    {
        ReceiveMoney, UseAdvance, AdvanceBalance, FamilySplit, Discount, FeeRate, FeeAgreement,
        ExistingBills, PaidReduction, PostFees, CancelCharge, CancelMonth, FutureFees, Period,
        ReducePayment, RefundPayment, ReversePayment, Deposit, RefundAdvance, TransferAdvance,
        BillingStart, PlacementStart, Review, SaveOutcome, Receipt, Closing, StopFee, TransferFees,
        ReadmissionFees, CopyRates, ApplyRates
    }

    private sealed class Session
    {
        internal readonly ToolTip Tips=new() {InitialDelay=600,ReshowDelay=200,AutoPopDelay=20000,ShowAlways=true};
        internal Session(Control owner)
        {
            owner.Disposed+=(_,_)=>{Tips.Dispose();Sessions.Remove(owner);};
        }
    }
    private static readonly ConditionalWeakTable<Control,Session> Sessions=new();

    internal static void Attach(Control owner,Control target,Topic topic)
    {
        var (effect,example)=Content(topic);
        Attach(owner,target,effect,example);
    }
    internal static void Attach(Control owner,Control target,string effect,string example)
    {
        if(owner.IsDisposed||owner.Disposing||target.IsDisposed)return;
        string text=effect+"\n\nExample: "+example;
        var session=Sessions.GetValue(owner,key=>new Session(key));
        void Apply(Control control)
        {
            ControlToolTips.SetDescription(session.Tips,control,text);
            UiAccessibility.Describe(control,null,text);
            // Wrapped inputs receive mouse events on their native child controls.
            foreach(Control child in control.Controls)Apply(child);
        }
        Apply(target);
    }
    internal static string Tooltip(Control owner,Control target)=>Sessions.TryGetValue(owner,out var session)?session.Tips.GetToolTip(target)??"":"";

    private static (string Effect,string Example) Content(Topic topic)=>topic switch
    {
        Topic.ReceiveMoney=>("Enter only the new money received in this transaction. Existing advance money is separate.",
            "Rs. 1,000 is due. Receive Rs. 700 now and leave Rs. 300 outstanding, unless advance money also covers it."),
        Topic.UseAdvance=>("Use each selected student's own advance against that student's fees, only when collecting today. Unselected siblings' credit is excluded. This is not new cash.",
            "For a collection dated today, Rs. 1,000 due can use Rs. 300 advance and Rs. 700 new money."),
        Topic.AdvanceBalance=>("Money held for this student until an operator chooses to use it in Fee Collection. Posting fees does not spend it.",
            "An advance of Rs. 500 can pay Rs. 200 during collection, leaving Rs. 300 available."),
        Topic.FamilySplit=>("The suggested split finishes junior classes first, then moves up. Within each child, monthly fees are oldest first, followed by other fees from smallest remaining balance to largest. Same-class siblings use the lower admission number first. You can change the split before saving.",
            "If the junior owes Rs. 600 and the next child owes Rs. 700, a Rs. 1,000 payment proposes Rs. 600 and Rs. 400 respectively."),
        Topic.Discount=>("Enter the amount to subtract from the fee, rather than the amount the student should pay.",
            "A Rs. 1,000 fee with a Rs. 200 discount leaves Rs. 800 to pay."),
        Topic.FeeRate=>("The full fee before any discount. Changing a rate does not record a payment.",
            "A fee rate of Rs. 1,000 with a Rs. 200 discount makes the student's fee Rs. 800."),
        Topic.FeeAgreement=>("The student's saved fee plan controls later fee posting. Review the existing-bills option separately.",
            "Set tuition to Rs. 500 from November. Check the review to see whether any already issued bills also change."),
        Topic.ExistingBills=>("Review changes to fees already added to the student's account for the selected period.",
            "Changing an October bill from Rs. 1,000 to Rs. 800 reduces its fee by Rs. 200; a paid bill also needs a decision about the excess payment."),
        Topic.PaidReduction=>("Choose what happens to money already paid when the fee is reduced: hold the excess as advance, or record a refund.",
            "Rs. 1,000 was paid. Reduce the fee to Rs. 800: the excess Rs. 200 becomes advance money or is refunded, according to your choice."),
        Topic.PostFees=>("Add the reviewed fees to what the student owes. This does not collect money.",
            "Adding Rs. 400 tuition creates Rs. 400 due. Record the parent's payment separately in Fee Collection."),
        Topic.CancelCharge=>("Cancel an eligible unpaid fee so it is no longer due. This does not refund money. Payment history can block cancellation.",
            "Cancel an unpaid Rs. 400 charge with no payment history: outstanding falls by Rs. 400."),
        Topic.CancelMonth=>("Cancel the latest eligible monthly posting for the whole school. Individual students cannot be excluded; payment history can block it.",
            "If November was posted by mistake, review every affected charge before cancelling that whole posting."),
        Topic.FutureFees=>("Review fees for coming months. A preview is not a payment, and only the confirmed posting adds fees due.",
            "Adding Rs. 400 tuition for November and December creates Rs. 800 in future fees; no money is received."),
        Topic.Period=>("Both the first and last selected months are included. Check the month range before saving.",
            "October through December includes October, November and December."),
        Topic.ReducePayment=>("Enter the amount to keep recorded as paid. The reduction is added back to outstanding dues; this action does not record a cash refund.",
            "Rs. 1,000 is recorded as paid. Enter 800 to keep Rs. 800 paid and add Rs. 200 back to outstanding dues."),
        Topic.RefundPayment=>("Enter the part of the recorded payment being returned. This reduces the collection and restores the corresponding dues.",
            "Refund Rs. 200 from a Rs. 1,000 payment: Rs. 800 remains collected and Rs. 200 becomes due again."),
        Topic.ReversePayment=>("Undo the whole eligible payment. It restores the amount to outstanding dues and is different from cancelling the fee itself.",
            "Reverse a Rs. 1,000 payment: the fee remains, and Rs. 1,000 becomes due again."),
        Topic.Deposit=>("Record new money received and held as the student's advance balance. It is separate from collecting against selected bills.",
            "Receive Rs. 1,000 for later fees: the student's advance balance increases by Rs. 1,000."),
        Topic.RefundAdvance=>("Return unused advance money to the payer. This is separate from refunding a payment already used against a bill.",
            "Return Rs. 200 from an unused Rs. 500 advance, leaving Rs. 300 available."),
        Topic.TransferAdvance=>("Move available advance money to a linked sibling. No new money is received.",
            "Move Rs. 200 from a child's Rs. 500 advance: that child keeps Rs. 300 and the sibling gains Rs. 200."),
        Topic.BillingStart=>("This date controls billing independently of the class placement date. The school cutoff determines the first monthly fee period.",
            "With cutoff 16, an April class placement and a 20 October billing start produce November monthly fees."),
        Topic.PlacementStart=>("Record when the student joined this class. Choose the billing start separately for legacy setup.",
            "A student joined in April, but system billing is being started in October. Keep April as the placement start."),
        Topic.Review=>("Review the financial effect first. The review itself does not save the change.",
            "A preview can show that outstanding will fall by Rs. 200; the change takes effect only after confirmation."),
        Topic.SaveOutcome=>("Check whether the earlier save succeeded before trying again when its result is uncertain.",
            "If a payment save loses its connection, check that transaction's outcome rather than collecting the same payment again."),
        Topic.Receipt=>("Print the recorded receipt. Reprinting does not receive money again or make another payment.",
            "Reprint a Rs. 200 receipt: the recorded payment stays Rs. 200."),
        Topic.Closing=>("Compare recorded money with the amounts actually counted or confirmed. Advance money used to pay fees is not new cash received.",
            "Fees of Rs. 1,000 settled with Rs. 700 cash and Rs. 300 existing advance add Rs. 700 new cash."),
        Topic.StopFee=>("Choose the stopping month under the School Profile transport cutoff. Wholly unpaid posted transport fees from that month are cancelled; months with any payment remain charged.",
            "Stopping a transport plan does not by itself refund an earlier transport payment."),
        Topic.TransferFees=>("Review when the new class fees begin and the effect on bills already issued. A class transfer does not erase earlier dues.",
            "The old class fee was Rs. 400 and the new fee is Rs. 500. Review the Rs. 100 difference and its starting month."),
        Topic.ReadmissionFees=>("Choose the new monthly starting period and whether yearly fees should be charged for the returning placement. Earlier dues remain.",
            "A student returns with Rs. 500 old dues. Starting new fees does not clear that Rs. 500."),
        Topic.CopyRates=>("Copy class fee settings to the destination you select. This does not record student payments.",
            "Copy the Rs. 400 tuition setting to another class, then review separately how it will apply to students."),
        Topic.ApplyRates=>("Apply saved class rates from the chosen date. Review existing bills separately before changing amounts already billed.",
            "Apply a Rs. 500 rate from November; use the existing-bill review if October's issued bill also needs a change."),
        _=>throw new ArgumentOutOfRangeException(nameof(topic))
    };
}
