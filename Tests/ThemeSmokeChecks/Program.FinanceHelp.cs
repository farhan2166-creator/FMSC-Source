using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Finance.Collection;

internal static partial class Program
{
    private static void CheckFinanceHelp()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        using(var owner=new Form())
        using(var input=HumanUi.TextBox(false,20))
        {
            owner.Controls.Add(HumanUi.Field("Discount",input));
            FinanceHelp.Attach(owner,input,FinanceHelp.Topic.Discount);
            string text=FinanceHelp.Tooltip(owner,input);
            Check(text.Contains("Example:")&&text.Contains("Rs. 200")&&text.Contains("Rs. 800"),"The discount tooltip explains amount deducted and amount due");
            Check(input.Controls.Count>0&&input.Controls.Cast<Control>().All(child=>FinanceHelp.Tooltip(owner,child)==text),"Hover examples reach native children of wrapped financial inputs");
            Check(input.AccessibleDescription==text,"Financial examples are available to assistive technology");
            using var label=new Label();owner.Controls.Add(label);
            foreach(var topic in Enum.GetValues<FinanceHelp.Topic>())
            {
                FinanceHelp.Attach(owner,label,topic);
                Check(FinanceHelp.Tooltip(owner,label).Contains("Example:")&&FinanceHelp.Tooltip(owner,label).Length<600,"Every financial help topic has a short practical example");
            }
            owner.Dispose();
            Check(FinanceHelp.Tooltip(owner,input)=="","Closing a financial screen releases its tooltip session");
            FinanceHelp.Attach(owner,input,FinanceHelp.Topic.Discount);
            Check(FinanceHelp.Tooltip(owner,input)=="","Late UI events cannot recreate help on a disposed screen");
        }
        using(var collection=new FeeCollectionPage(false))
        {
            T Field<T>(string name)=>(T)typeof(FeeCollectionPage).GetField(name,flags)!.GetValue(collection)!;
            var use=Field<CheckBox>("useAvailableCredit");
            Check(use.Checked&&use.Text.Contains("advance",StringComparison.OrdinalIgnoreCase),
                "Advance is offered by default without pinning the changing single/family caption");
            var scopeHelp=FinanceHelp.Tooltip(collection,use);
            Check(scopeHelp.Contains("selected student",StringComparison.OrdinalIgnoreCase) &&
                  scopeHelp.Contains("not new cash",StringComparison.OrdinalIgnoreCase),
                "Advance help explains selected-student ownership and distinguishes credit from newly received cash");
            Check(FinanceHelp.Tooltip(collection,Field<ModernTextBox>("paymentAmount")).Contains("only the new money"),"Amount received excludes already held advance money");
        }
        using(var credit=new StudentCreditDialog(41,false))
        {
            T Field<T>(string name)=>(T)typeof(StudentCreditDialog).GetField(name,flags)!.GetValue(credit)!;
            var action=Field<ModernComboBox>("action");var amount=Field<ModernTextBox>("amount");
            amount.Text="250";Field<ModernTextBox>("reason").Text="Preserve this draft";
            Check(action.Items.Cast<string>().SequenceEqual(new[]{"Deposit","Refund","Transfer"}),"Advance-account transaction codes and order stay unchanged");
            foreach(var choice in new[]{(Code:"Deposit",Text:"new money received"),(Code:"Refund",Text:"unused advance"),(Code:"Transfer",Text:"linked sibling")})
            {
                action.SelectedItem=choice.Code;
                Check(FinanceHelp.Tooltip(credit,amount).Contains(choice.Text),"The advance amount example follows the selected financial action");
            }
            Check(amount.Text=="250"&&Field<ModernTextBox>("reason").Text=="Preserve this draft","Changing help does not change the entered amount or reason");
        }
    }
}
