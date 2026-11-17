using FMSC_SMS.Core;

internal static class CollectionCreditScopeChecks
{
    internal static void Run(Action<bool,string> check)
    {
        // Realistic family scenario: advances remain per child, not a family wallet.
        var balances=new Dictionary<int,decimal>{{162,500m},{163,300m},{291,900m},{423,2500m}};
        var family=new[]{(1L,162,3000m),(2L,163,3500m),(3L,291,2500m),(4L,423,1800m)};
        var plan=CollectionCreditAllocation.Build(family,balances);
        check(plan.Count==4 && plan[1]==500m && plan[2]==300m &&
              plan[3]==900m && plan[4]==1800m,
            "Every selected sibling applies only their own available advance");
        check(family.Sum(x=>x.Item3)-plan.Values.Sum()==7300m &&
              balances[423]-plan[4]==700m,
            "Family needs 7300 cash and Hamid retains 700 advance without transferring it");
        check(balances[162]==500m && balances[163]==300m &&
              balances[291]==900m && balances[423]==2500m,
            "Planning never writes, spends or moves any student's ledger balance");

        // Removing a sibling from the selected charges also removes their credit.
        var selected=family.Where(x=>x.Item2 is 162 or 163).ToArray();
        plan=CollectionCreditAllocation.Build(selected,balances);
        check(plan.Count==2 && plan[1]==500m && plan[2]==300m &&
              selected.Sum(x=>x.Item3)-plan.Values.Sum()==5700m,
            "Unticked siblings are excluded from both dues and usable advance");
        plan=CollectionCreditAllocation.Build(family,balances);
        check(plan.Count==4 && plan.Values.Sum()==3500m,
            "Reticking siblings restores their independent credit allocation");

        var equal=CollectionCreditAllocation.Build(new[]{(5L,163,300m)},balances);
        check(equal[5]==300m && 300m-equal[5]==0m,
            "When a fee exactly equals its owner's advance, new cash required is zero");
        var excess=CollectionCreditAllocation.Build(new[]{(6L,423,1800m)},balances);
        check(excess[6]==1800m && balances[423]-excess[6]==700m,
            "A student with more advance than due keeps the exact surplus");

        var multiple=new[]{(10L,163,400m),(11L,163,2500m),(12L,162,400m)};
        var smallerBalances=new Dictionary<int,decimal>{{163,1000m},{162,100m}};
        plan=CollectionCreditAllocation.Build(multiple,smallerBalances);
        check(plan.Count==3 && plan[10]==400m && plan[11]==600m && plan[12]==100m,
            "Ordered charges independently cap advance for each child's account");
        check(plan.Values.Sum()==1100m && smallerBalances[163]==1000m &&
              smallerBalances[162]==100m,
            "Separate credit ledgers conserve their balances without pooling");
        plan=CollectionCreditAllocation.Build(
            new[]{(0L,163,100m),(13L,163,0m),(14L,163,-100m),(15L,999,100m)},balances);
        check(plan.Count==0,
            "Invalid charges, settled fees, negative dues and missing owners do not create credit applications");
        check(CollectionCreditAllocation.Build(multiple,
            new Dictionary<int,decimal>{{163,-5m}}).Count==0,
            "Negative ledger balances cannot be spent");
        check(CollectionCreditAllocation.Build(Array.Empty<(long,int,decimal)>(),balances).Count==0,
            "No selected charges means no advance is applied");
    }
}
