using System.Data;
using FMSC_SMS.Core;

internal static class AdministrativeAccessChecks
{
    private sealed class Transaction : IAdministrativeTransaction
    {
        public bool SubmissionStarted { get; set; }
        internal bool FailCommit, FailRollback;
        internal int Commits, Rollbacks;
        public void Commit(){Commits++;if(FailCommit)throw new IOException("Lost commit reply");}
        public void Rollback(){Rollbacks++;if(FailRollback)throw new IOException("Rollback unavailable");}
        public void Dispose(){ }
    }
    internal static int Run()
    {
        int checks=0;
        void Check(bool valid,string message){if(!valid)throw new Exception(message);checks++;}
        var permissions=new[]{new AccessPermission(10,"Students.View","View students",""),new AccessPermission(11,"Students.Admit","Admit students",""),
            new AccessPermission(20,"Fees.View","View fees",""),new AccessPermission(30,"System.Users.Manage","Manage users",""),
            new AccessPermission(31,"System.Users","View users",""),new AccessPermission(32,"System.Roles","View roles",""),
            new AccessPermission(33,"System.Roles.Manage","Manage roles",""),new AccessPermission(34,"System.Administration","Administration","")};
        var roles=new[]{new AccessRole(1,"Administrator","Protected",permissions.Select(permission=>permission.Id).ToArray()),
            new AccessRole(2,"Accountant","Office duties",new[]{10,11}),new AccessRole(3,"Finance","Finance duties",new[]{10,20})};
        var users=new[]{new AccessListItem(7,"Ali","1 role",false),new AccessListItem(8,"Bilal","1 role",false)};
        var catalog=new AccessCatalog(roles,permissions,users,AdministrativeAccess.CatalogToken(roles,permissions));
        var roleCatalog=catalog with { Items=roles.Select(role=>new AccessListItem(role.Id,role.Name,"",role.Protected)).ToArray() };
        AccessRules Rules(params string[] grants)=>AccessRules.For(code=>grants.Contains(code,StringComparer.OrdinalIgnoreCase));
        Check(Rules() is { CanReadUsers:false,CanManageUsers:false,CanReadRoles:false,CanManageRoles:false },"No permissions imply no maintenance access");
        Check(Rules("System.Users.Manage","System.Roles.Manage") is { CanManageUsers:false,CanManageRoles:false },"Management screens require the data-view permissions used by their SQL reads");
        Check(Rules("System.Users","System.Roles") is { CanReadUsers:true,CanManageUsers:false,CanReadRoles:true,CanManageRoles:false },"Read-only viewers do not acquire management access");
        Check(Rules("System.Users","System.Roles","System.Users.Manage") is { CanManageUsers:true,CanManageRoles:false },"User management does not silently grant role management");
        Check(Rules("System.Administration") is { CanReadUsers:true,CanManageUsers:true,CanReadRoles:true,CanManageRoles:true },"Established broad administration permission matches the SQL guards");
        Check(AdministrativeAccess.IsAdministrator(" administrator ")&&!AdministrativeAccess.IsAdministrator("Office"),"Administrator protection is independent of display case/spacing");
        Check(AdministrativeAccess.EffectivePermissions(catalog,new[]{2,3}).SetEquals(new[]{10,11,20}),"Multiple assigned roles produce a deduplicated permission union");
        Check(AdministrativeAccess.EffectivePermissions(catalog,Array.Empty<int>()).Count==0,"Empty membership never falls back to a primary role");
        var draft=new AccessDraft(7,"Ali","",true,new[]{2,3});
        Check(draft.SameValues(draft with { Choices=new[]{3,2,2} }),"Dirty detection treats membership as a set");
        foreach(var changed in new[]{draft with { Name="Bilal" },draft with { ForcePasswordChange=false },draft with { Choices=new[]{2} },draft with { Id=8 }})
            Check(!draft.SameValues(changed),"Account field, identity or membership changes count as a draft");
        Check((draft with { Name=" Ali ",Description=" notes ",Choices=new[]{3,2,2} }).Clean() is { Name:"Ali",Description:"notes" } clean&&clean.Choices.SequenceEqual(new[]{2,3}),"Save preparation trims text and sorts/deduplicates IDs");
        Check(AdministrativeAccess.Validate(AccessEditorKind.Users,new(0,"New","",true,Array.Empty<int>()),catalog) is null,"A new normal user may deliberately have zero roles");
        Check(AdministrativeAccess.Validate(AccessEditorKind.Users,draft with { Name="bilal" },catalog) is not null,"Existing names are rejected without relying on a save failure");
        Check(AdministrativeAccess.Validate(AccessEditorKind.Users,draft with { Choices=new[]{1} },catalog) is not null,"Normal users cannot receive the reserved Administrator role");
        foreach(int id in new[]{0,-1,99})Check(AdministrativeAccess.Validate(AccessEditorKind.Users,draft with { Choices=new[]{id} },catalog) is not null,"Missing/invalid role IDs cannot enter a save");
        Check(AdministrativeAccess.Validate(AccessEditorKind.Users,new(0,"New","",true,Array.Empty<int>()),catalog with { Roles=new[]{roles[0]} }) is not null,"New-user creation preserves the legacy non-Administrator primary-reference prerequisite");
        Check(AdministrativeAccess.Validate(AccessEditorKind.Roles,new(0,"New role","",false,Array.Empty<int>()),roleCatalog) is not null,"The two-role policy refuses new role creation");
        Check(AdministrativeAccess.Validate(AccessEditorKind.Roles,new(2,"administrator","",false,new[]{10}),roleCatalog) is not null,"Reserved role names cannot be introduced through renaming");
        Check(AdministrativeAccess.Validate(AccessEditorKind.Roles,new(2,"Accountant",new string('x',251),false,new[]{10}),roleCatalog) is not null,"Description length is checked before writes");
        Check(AdministrativeAccess.Validate(AccessEditorKind.Roles,new(2,new string('x',51),"",false,new[]{10}),roleCatalog) is not null,"Role name length is checked before writes");
        Check(AdministrativeAccess.Validate(AccessEditorKind.Roles,new(2,"Accountant","",false,new[]{999}),roleCatalog) is not null,"Unknown permission IDs are refused");
        Check(AdministrativeAccess.Hints(catalog,new[]{30}).Length==1&&AdministrativeAccess.Hints(catalog,new[]{30,31,32}).Length==0,"User-management dependency hints inform without silently granting access");
        Check(AdministrativeAccess.Hints(catalog,new[]{33}).Length==1&&AdministrativeAccess.Hints(catalog,new[]{33,32}).Length==0,"Role-management hints match the screen's read requirements");
        Check(AdministrativeAccess.Hints(catalog,new[]{11}).Single().Contains("Quick Registration")&&
              AdministrativeAccess.Hints(catalog,new[]{11}).Single().Contains("Direct Admissions")&&
              AdministrativeAccess.Hints(catalog,new[]{11,10}).Length==0,
            "Admit-only role warns about both student-search-dependent admission flows");
        var feeCollectionCatalog=catalog with { Permissions=permissions.Append(new AccessPermission(21,"Fees.Collect","Collect fees","")).ToArray() };
        Check(AdministrativeAccess.Hints(feeCollectionCatalog,new[]{21}).Any(hint=>hint.Contains("Fee Collection")&&hint.Contains("View Fees")),
            "Collection-only permissions explain that Fee Collection also requires fee viewing.");
        Check(AdministrativeAccess.Hints(feeCollectionCatalog,new[]{21}).Any(hint=>hint.Contains("Fee Collection")&&hint.Contains("View Students")),
            "Collection-only roles explain the student-search dependency.");
        Check(AdministrativeAccess.Hints(feeCollectionCatalog,new[]{20,21}).Any(hint=>hint.Contains("Fee Collection")&&hint.Contains("View Students")),
            "Fee viewing without student viewing still warns for collection.");
        Check(!AdministrativeAccess.Hints(feeCollectionCatalog,new[]{10,20,21}).Any(hint=>hint.Contains("Fee Collection")),
            "Complete collection, fee-view and student-view permissions show no dependency warning.");
        Check(AdministrativeAccess.Hints(feeCollectionCatalog,new[]{21}).Any(hint=>hint.Contains("Student Purchases")&&hint.Contains("View Fees")),
            "Collection-only roles receive the purchase fee-view dependency.");
        Check(AdministrativeAccess.Hints(feeCollectionCatalog,new[]{21}).Any(hint=>hint.Contains("Student Purchases")&&hint.Contains("View Students")),
            "Collection-only roles receive the purchase student-view dependency.");
        Check(!AdministrativeAccess.Hints(feeCollectionCatalog,new[]{10,21}).Any(hint=>hint.Contains("Student Purchases")&&hint.Contains("View Students")),
            "Student viewing removes only the matching purchase dependency.");
        Check(!AdministrativeAccess.Hints(feeCollectionCatalog,new[]{20,21}).Any(hint=>hint.Contains("Student Purchases")&&hint.Contains("View Fees")),
            "Fee viewing removes only the matching purchase dependency.");
        Check(!AdministrativeAccess.Hints(feeCollectionCatalog,new[]{10,20,21}).Any(hint=>hint.Contains("Student Purchases")),
            "Complete purchase permissions do not show false missing-access warnings.");
        var accountCatalog=feeCollectionCatalog with { Permissions=feeCollectionCatalog.Permissions.Append(
            new AccessPermission(22,"Reports.View","View reports","")).ToArray() };
        Check(AdministrativeAccess.Hints(accountCatalog,new[]{22}).Any(h=>h.Contains("Student Fee Account")&&h.Contains("View Students")),
            "Report-only roles explain the student-search requirement for Student Fee Account.");
        Check(AdministrativeAccess.Hints(accountCatalog,new[]{20,21}).Any(h=>h.Contains("Student Fee Account")&&h.Contains("View Students")),
            "Collection without student viewing warns for Student Fee Account.");
        Check(!AdministrativeAccess.Hints(accountCatalog,new[]{10,22}).Any(h=>h.Contains("Student Fee Account")) &&
              !AdministrativeAccess.Hints(accountCatalog,new[]{10,21}).Any(h=>h.Contains("Student Fee Account")),
            "Either account entry authority with student viewing avoids a spurious warning.");
        var certificateCatalog=catalog with { Permissions=permissions.Concat(new[]{
            new AccessPermission(40,"Certificates.Create","Create certificates",""),
            new AccessPermission(41,"Students.Leave","Leave student","") }).ToArray() };
        foreach(int chosen in new[]{40,41}){
            Check(AdministrativeAccess.Hints(certificateCatalog,new[]{chosen}).Any(h=>h.Contains("Student certificates")&&h.Contains("View Students")),
                "Certificate-only permissions warn that student viewing is also required.");
            Check(!AdministrativeAccess.Hints(certificateCatalog,new[]{chosen,10}).Any(h=>h.Contains("Student certificates")),
                "Full student and certificate permission has no spurious warning.");
        }
        Check(AdministrativeAccess.Hints(catalog,new[]{30,33,34}).Length==0,"Broad administration rights satisfy the known management dependencies");
        Check(catalog.Token==AdministrativeAccess.CatalogToken(roles.Reverse(),permissions.Reverse()),"Catalog fingerprints are independent of SQL row ordering");
        Check(catalog.Token!=AdministrativeAccess.CatalogToken(roles.Select(role=>role.Id==2?role with { Permissions=new[]{10} }:role),permissions),"Changed permission definitions invalidate a displayed access preview");
        Check(catalog.Token!=AdministrativeAccess.CatalogToken(roles.Select(role=>role.Id==2?role with { Description="changed" }:role),permissions),"Role-definition changes invalidate the catalog token");
        Check(catalog.Token!=AdministrativeAccess.CatalogToken(roles,permissions.Select(permission=>permission.Id==10?permission with { Code="Other.View" }:permission)),"Permission identity/meaning changes invalidate the catalog token");
        var record=new AccessRecord(7,"Ali","",true,false,new[]{2,3},"original","credential");
        var intent=new AccessWriteIntent(AccessEditorKind.Users,draft,"credential","original");
        Check(intent.Matches(record)&&!intent.Matches(record with { CredentialProof="old" }),"Recovery must verify an exact credential proof for password changes");
        foreach(var changed in new[]{record with { Id=8 },record with { Name="Other" },record with { ForcePasswordChange=false },record with { Choices=new[]{2} }})
            Check(!intent.Matches(changed),"Recovery cannot claim a different identity/account/access state");
        Check((intent with { Values=draft with { Id=0 } }).Matches(record),"A new user's recovered ID can be matched to its captured values/proof");
        using(var table=new DataTable())
        {
            table.Columns.Add("Name");table.Columns.Add("Code");table.Rows.Add("A[%*]'X","Students.View");table.Rows.Add("Other","Fees.View");
            foreach(string term in new[]{"[%*]","'X","A["})Check(table.Select(AdministrativeAccess.Filter(term,"Name","Code")).Length==1,"Search treats wildcard/bracket/quote input literally");
            Check(AdministrativeAccess.Filter(" ","Name")=="","Blank filters do not restrict the cached list");
        }
        var writeIntent=new AccessWriteIntent(AccessEditorKind.Roles,new(0,"New","",false,new[]{10}));
        using(var transaction=new Transaction())
        {
            int writes=0;
            var result=AdministrativeSaveCoordinator.Execute(transaction,writeIntent,()=>{transaction.SubmissionStarted=true;writes++;return 4;});
            Check(result is { Outcome:AccessSaveOutcome.Saved,Id:4 }&&result.Intent?.Values.Id==4&&writes==1&&transaction.Commits==1&&transaction.Rollbacks==0,"Only a confirmed commit reports one successful save");
        }
        using(var transaction=new Transaction())
        {
            var result=AdministrativeSaveCoordinator.Execute(transaction,writeIntent,()=>{transaction.SubmissionStarted=true;throw new IOException("Second role step failed");});
            Check(result.Outcome==AccessSaveOutcome.Rejected&&transaction.Commits==0&&transaction.Rollbacks==1,"Failure before commit rolls back the combined role operation");
        }
        using(var transaction=new Transaction{FailCommit=true})
        {
            int writes=0;var result=AdministrativeSaveCoordinator.Execute(transaction,writeIntent,()=>{transaction.SubmissionStarted=true;writes++;return 4;});
            Check(result is { Outcome:AccessSaveOutcome.Unconfirmed,Id:4 }&&writes==1&&transaction.Commits==1,"A lost commit reply retains its candidate ID and never retries the write");
        }
        using(var transaction=new Transaction{FailRollback=true})
        {
            var result=AdministrativeSaveCoordinator.Execute(transaction,writeIntent,()=>{transaction.SubmissionStarted=true;throw new IOException("Connection lost");});
            Check(result.Outcome==AccessSaveOutcome.Unconfirmed&&transaction.Commits==0,"A submitted write without confirmed rollback remains uncertain");
        }
        using(var transaction=new Transaction{FailRollback=true})
        {
            var result=AdministrativeSaveCoordinator.Execute(transaction,writeIntent,()=>throw new AdministrativeStaleStateException("Changed before submission"));
            Check(result.Outcome==AccessSaveOutcome.Rejected,"A refused verification before submission cannot be mistaken for an uncertain write");
        }
        using(var transaction=new Transaction())
        {
            Check(AdministrativeSaveCoordinator.Execute(transaction,writeIntent,()=>0).Outcome==AccessSaveOutcome.Rejected&&transaction.Commits==0,"Malformed saved IDs are refused before commit");
        }
        using(var transaction=new Transaction{SubmissionStarted=true,FailRollback=true})
        {
            var result=AdministrativeSaveCoordinator.Execute(transaction,writeIntent with { Values=writeIntent.Values with { Id=2 } },()=>3);
            Check(result.Outcome==AccessSaveOutcome.Unconfirmed&&result.Intent?.Values.Id==2&&transaction.Commits==0,"A wrong returned ID cannot redirect an existing-record recovery");
        }
        Check(PermissionCatalog.All.Count==31&&PermissionCatalog.All.Values.All(p=>p.Code.Length<=100&&p.Name.Length<=100&&p.Description.Length<=250),"Supported permission definitions fit SQL column bounds");
        Check(!PermissionCatalog.Contains("Students.Edit")&&!PermissionCatalog.Contains("System.Roles.Deactivate")&&!PermissionCatalog.Contains("Students.Siblings"),"Dormant and superseded permission options are removed");
        Check(PermissionCatalog.All["Reports.View"].Name.Contains("student and finance")&&PermissionCatalog.All["System.Roles"].Name.StartsWith("View"),"Permission labels describe actual access rather than claiming management");
        Check(AdministrativeAccess.Validate(AccessEditorKind.Roles,new(2,"Renamed","",false,Array.Empty<int>()),roleCatalog) is not null,"Accountant cannot be renamed into another role");
        Check(AdministrativeAccess.Validate(AccessEditorKind.Roles,new(2,"Accountant","",false,new[]{30}),roleCatalog) is not null,"Incomplete management permission sets are refused before writes");
        Check(AdministrativeAccess.Validate(AccessEditorKind.Roles,new(2,"Accountant","",false,new[]{30,31,32}),roleCatalog) is null,"Complete user management prerequisites remain usable");
        Check(AdministrativeAccess.Validate(AccessEditorKind.Roles,new(2,"Accountant","",false,new[]{30,34}),roleCatalog) is null,"Broad administration satisfies management prerequisites consistently");
        Check(AdministrativeAccess.Validate(AccessEditorKind.Users,draft with { Choices=new[]{3} },catalog) is not null,"Removed roles cannot be assigned to normal accounts");
        return checks;
    }
}
