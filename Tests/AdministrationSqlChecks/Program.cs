using FMSC_SMS.Core;
using FMSC_SMS.Pages.Administration;
using Microsoft.Data.SqlClient;
using System.Security.Cryptography;

// Only an explicitly disposable database is accepted. No school auto-attach path is used.
string connectionText=Environment.GetEnvironmentVariable("FMSC_AUDIT_CONNECTION")??throw new Exception("Set FMSC_AUDIT_CONNECTION to a disposable administration clone.");
var builder=new SqlConnectionStringBuilder(connectionText);
bool loopback=!builder.IntegratedSecurity&&builder.DataSource.StartsWith("127.0.0.1,",StringComparison.Ordinal)&&builder.UserID=="sa";
if(!builder.InitialCatalog.StartsWith("FMSC_AuditChecks_",StringComparison.Ordinal)||(!builder.IntegratedSecurity&&!loopback))
    throw new Exception("Use a disposable FMSC_AuditChecks_<suffix> catalog with integrated security or isolated loopback test SQL authentication.");
DatabaseHelper.ConnectionString=connectionText;
int checks=0;
void Check(bool valid,string message){if(!valid)throw new Exception(message);checks++;Console.WriteLine("PASS: "+message);}
Check(PasswordPolicy.IsValid("x") && PasswordPolicy.IsValid(new string('x',1024)) && !PasswordPolicy.IsValid(null) && !PasswordPolicy.IsValid("") && !PasswordPolicy.IsValid(" \t"),
    "account passwords allow user-chosen lengths while rejecting blank values");
DataTable Query(string sql,params SqlParameter[] parameters)
{
    using var connection=DatabaseHelper.GetConnection();connection.Open();using var command=new SqlCommand(sql,connection){CommandTimeout=120};command.Parameters.AddRange(parameters);
    var result=new DataTable();using var adapter=new SqlDataAdapter(command);adapter.Fill(result);return result;
}
int Id(string sql)=>Convert.ToInt32(Query(sql).Rows[0][0]);
int admin=Id("SELECT TOP(1) U.UserId FROM dbo.Users U JOIN dbo.Roles R ON R.RoleId=U.RoleId WHERE R.RoleName=N'Administrator' ORDER BY U.UserId;");
CurrentUser.UserId=admin;
var users=new AdministrationStore(AccessEditorKind.Users);var roles=new AdministrationStore(AccessEditorKind.Roles);
var userCatalog=await users.LoadCatalogAsync();
int office=userCatalog.Roles.Single(role=>AdministrativeAccess.IsAccountant(role.Name)).Id;
int finance=office;
int view=userCatalog.Permissions.First(permission=>permission.Code=="Students.View").Id;
int fees=userCatalog.Permissions.First(permission=>permission.Code=="Fees.View").Id;
int adminRole=userCatalog.Roles.Single(role=>role.Protected).Id;
string prefix="admin-audit-"+Guid.NewGuid().ToString("N")[..10];
const string password="x";

var created=await users.SaveAsync(new(0,prefix+"-user","",true,new[]{office}),"",userCatalog.Token,password);
Check(created.Outcome==AccessSaveOutcome.Saved&&created.Id>0,"production writer creates a user with a confirmed ID");
var loaded=await users.LoadAsync(created.Id);
Check(loaded.Record.Name==prefix+"-user"&&loaded.Record.Choices.SequenceEqual(new[]{office})&&loaded.Record.ForcePasswordChange,"loaded user fields and assigned roles match the saved draft");
Check(loaded.Record.Snapshot.Length==64&&loaded.Record.CredentialProof.Length==64,"opaque snapshot and credential proof are available without returning stored credential bytes");
using(var credential=Query("SELECT PasswordHash,PasswordSalt FROM dbo.Users WHERE UserId=@Id;",DatabaseHelper.Int("@Id",created.Id)))
{
    byte[] salt=(byte[])credential.Rows[0]["PasswordSalt"],hash=(byte[])credential.Rows[0]["PasswordHash"];
    Check(salt.Length==16&&hash.Length==64&&CryptographicOperations.FixedTimeEquals(hash,Rfc2898DeriveBytes.Pbkdf2(password,salt,100_000,HashAlgorithmName.SHA512,64)),"new-user hashing matches the existing sign-in policy");
}
// Execute the actual recovery SQL writer against the owned, migrated disposable
// database before the later tests deliberately assign a second Administrator.
AdministratorRecoveryChecks.Run(Check, connectionText, admin, created.Id);
string credentialBefore=loaded.Record.CredentialProof;
var changed=await users.SaveAsync(new(created.Id,prefix+"-renamed","",false,new[]{finance}),loaded.Record.Snapshot,loaded.Catalog.Token);
Check(changed.Outcome==AccessSaveOutcome.Saved&&changed.Id==created.Id,"account fields and assigned roles save together");
var current=await users.LoadAsync(created.Id);
Check(current.Record.Name==prefix+"-renamed"&&!current.Record.ForcePasswordChange&&current.Record.Choices.SequenceEqual(new[]{finance})&&current.Record.CredentialProof==credentialBefore,"normal Save User changes the explicit role draft and keeps the password");
var stale=await users.SaveAsync(AccessDraft.From(loaded.Record) with { Name=prefix+"-stale" },loaded.Record.Snapshot,loaded.Catalog.Token);
Check(stale.Outcome==AccessSaveOutcome.Rejected&&stale.Error is AdministrativeStaleStateException,"a stale user editor is refused before its write");
Check((await users.LoadAsync(created.Id)).Record.Name==prefix+"-renamed","stale account save does not overwrite the newer account");
var reset=await users.ResetPasswordAsync(current.Record,current.Catalog.Token,password+" reset",true);
Check(reset.Outcome==AccessSaveOutcome.Saved,"password-only reset succeeds for a clean saved user");
var afterReset=await users.LoadAsync(created.Id);
Check(afterReset.Record.Name==current.Record.Name&&afterReset.Record.Choices.SequenceEqual(current.Record.Choices)&&afterReset.Record.ForcePasswordChange&&afterReset.Record.CredentialProof!=credentialBefore,"password reset preserves saved username/roles and changes only credentials/password requirement");
var verified=await users.CheckAsync(reset.Intent!);
Check(verified.Outcome==AccessCheckOutcome.Saved&&verified.Record?.Id==created.Id,"read-only reconciliation verifies the reset's exact credential proof");
var notApplied=await users.CheckAsync(new(AccessEditorKind.Users,AccessDraft.From(afterReset.Record) with { Name=prefix+"-not-applied" },OriginalSnapshot:afterReset.Record.Snapshot));
Check(notApplied.Outcome==AccessCheckOutcome.NotApplied,"read-only recovery recognizes unchanged original values and permits a deliberate retry");
var noAccess=await users.SaveAsync(AccessDraft.From(afterReset.Record) with { Choices=Array.Empty<int>() },afterReset.Record.Snapshot,afterReset.Catalog.Token);
Check(noAccess.Outcome==AccessSaveOutcome.Saved,"empty assigned-role sets remain supported");
Check(Query("EXEC dbo.GetUserPermissions @UserId;",DatabaseHelper.Int("@UserId",created.Id)).Rows.Count==0,"a legacy primary-role reference does not grant fallback access");
var zeroCatalog=await users.LoadCatalogAsync();
var zeroNew=await users.SaveAsync(new(0,prefix+"-zero","",true,Array.Empty<int>()),"",zeroCatalog.Token,password);
Check(zeroNew.Outcome==AccessSaveOutcome.Saved&&Query("EXEC dbo.GetUserPermissions @UserId;",DatabaseHelper.Int("@UserId",zeroNew.Id)).Rows.Count==0,"new accounts can deliberately start with zero effective access");
var protectedUser=await users.LoadAsync(admin);
Check(protectedUser.Record.Protected,"primary Administrator account is protected");
var protectSave=await users.SaveAsync(AccessDraft.From(protectedUser.Record) with { Name=prefix+"-bad-admin" },protectedUser.Record.Snapshot,protectedUser.Catalog.Token);
Check(protectSave.Outcome==AccessSaveOutcome.Rejected,"the normal editor writer refuses Administrator account changes");
Query("INSERT dbo.UserRoles(UserId,RoleId) VALUES(@User,@Role);",DatabaseHelper.Int("@User",zeroNew.Id),DatabaseHelper.Int("@Role",adminRole));
var memberAdmin=await users.LoadAsync(zeroNew.Id);
Check(memberAdmin.Record.Protected&&memberAdmin.Catalog.Items.Single(item=>item.Id==zeroNew.Id).Protected,"Administrator membership is protected even when the legacy primary role differs");
Check((await users.SaveAsync(AccessDraft.From(memberAdmin.Record) with { Choices=Array.Empty<int>() },memberAdmin.Record.Snapshot,memberAdmin.Catalog.Token)).Outcome==AccessSaveOutcome.Rejected,"a protected membership cannot be removed through normal user maintenance");

var roleCatalog=await roles.LoadCatalogAsync();
Check(roleCatalog.Roles.Length==2&&roleCatalog.Permissions.Length==31&&roleCatalog.Permissions.Length==PermissionCatalog.All.Count,"migration exposes exactly Administrator, Accountant and 31 implemented permissions");
int accountant=office;
var baselineRole=await roles.LoadAsync(accountant);
int[] originalGrants=baselineRole.Record.Choices.ToArray();
var refusedNew=await roles.SaveAsync(new(0,prefix+"-role","Must not exist",false,new[]{view}),"",roleCatalog.Token);
Check(refusedNew.Outcome==AccessSaveOutcome.Rejected&&Id("SELECT COUNT(*) FROM dbo.Roles;")==2,"new role creation cannot reintroduce removed roles");
var refusedRename=await roles.SaveAsync(AccessDraft.From(baselineRole.Record) with { Name="Other" },baselineRole.Record.Snapshot,baselineRole.Catalog.Token);
Check(refusedRename.Outcome==AccessSaveOutcome.Rejected,"Accountant cannot be renamed into another role");
var roleEdit=await roles.SaveAsync(AccessDraft.From(baselineRole.Record) with { Description=prefix+" description",Choices=new[]{fees} },baselineRole.Record.Snapshot,baselineRole.Catalog.Token);
Check(roleEdit.Outcome==AccessSaveOutcome.Saved&&roleEdit.Id==accountant,"one Save Role changes Accountant description and permissions together");
var roleEdited=await roles.LoadAsync(accountant);
Check(roleEdited.Record.Description==prefix+" description"&&roleEdited.Record.Choices.SequenceEqual(new[]{fees}),"Accountant definition and grants round-trip as one committed draft");
Check(Id("SELECT COUNT(*) FROM dbo.UserRoles WHERE UserId="+admin)==1,"role saving does not assign Accountant to Administrator automatically");
Check((await roles.SaveAsync(AccessDraft.From(baselineRole.Record),baselineRole.Record.Snapshot,baselineRole.Catalog.Token)).Error is AdministrativeStaleStateException,"stale role definitions/permissions are refused");
var driftUser=await users.LoadAsync(created.Id);
Query("UPDATE dbo.Roles SET Description=CONCAT(ISNULL(Description,N''),N' changed') WHERE RoleId=@Id;",DatabaseHelper.Int("@Id",accountant));
Check((await users.SaveAsync(AccessDraft.From(driftUser.Record) with { ForcePasswordChange=!driftUser.Record.ForcePasswordChange },driftUser.Record.Snapshot,driftUser.Catalog.Token)).Error is AdministrativeStaleStateException,"changed access definitions invalidate a user's old permission preview");
Query("CREATE TRIGGER dbo.FMSC_Admin_FailPermissionSave ON dbo.RolePermissions AFTER INSERT AS BEGIN SET NOCOUNT ON; IF EXISTS(SELECT 1 FROM inserted I JOIN dbo.Roles R ON R.RoleId=I.RoleId JOIN dbo.Permissions P ON P.PermissionId=I.PermissionId WHERE R.RoleName=N'Accountant' AND P.PermissionCode=N'Students.View') THROW 59961,'Injected second-step failure',1; END;");
try
{
    var before=await roles.LoadAsync(accountant);
    var failed=await roles.SaveAsync(AccessDraft.From(before.Record) with { Description="Must roll back",Choices=new[]{view} },before.Record.Snapshot,before.Catalog.Token);
    Check(failed.Outcome!=AccessSaveOutcome.Saved,"an injected permission-step failure never claims success");
    var after=await roles.LoadAsync(accountant);
    Check(after.Record.Snapshot==before.Record.Snapshot,"Accountant description, permissions and membership state all roll back after second-step failure");
    var checkedFailure=await roles.CheckAsync(failed.Intent!);
    Check(checkedFailure.Outcome==AccessCheckOutcome.NotApplied,"read-only reconciliation recognizes the rolled-back role save");
}
finally{Query("DROP TRIGGER dbo.FMSC_Admin_FailPermissionSave;");}
var emptyFrame=await roles.LoadAsync(accountant);
var empty=await roles.SaveAsync(AccessDraft.From(emptyFrame.Record) with { Choices=Array.Empty<int>() },emptyFrame.Record.Snapshot,emptyFrame.Catalog.Token);
Check(empty.Outcome==AccessSaveOutcome.Saved,"Accountant may deliberately have an empty permission set");
Check(Query("EXEC dbo.GetUserPermissions @UserId;",DatabaseHelper.Int("@UserId",admin)).Rows.Count==PermissionCatalog.All.Count,"Administrator retains all implemented permissions independently of Accountant grants");
Query("EXEC dbo.RequireAnyPermission @UserId,N'Unknown.Future.Action';",DatabaseHelper.Int("@UserId",zeroNew.Id));
Check(true,"Administrator membership has the same unconditional bypass in RequireAnyPermission as the primary role");
bool protectedSql=false;
try{Query("EXEC dbo.SaveUser @UserId=@Actor,@Username=N'Must not change',@RoleId=0,@TargetUserId=@Target,@RoleIdsJson=N'[]';",DatabaseHelper.Int("@Actor",admin),DatabaseHelper.Int("@Target",zeroNew.Id));}
catch(SqlException ex) when(ex.Number==57223){protectedSql=true;}
Check(protectedSql,"direct SQL account save protects Administrator membership as well as primary role");
int roleRead=roleCatalog.Permissions.Single(p=>p.Code=="System.Roles").Id;
int userRead=roleCatalog.Permissions.Single(p=>p.Code=="System.Users").Id;
int manage=roleCatalog.Permissions.Single(p=>p.Code=="System.Roles.Manage").Id;
var prerequisites=await roles.LoadAsync(accountant);
Check((await roles.SaveAsync(AccessDraft.From(prerequisites.Record) with { Choices=new[]{manage} },prerequisites.Record.Snapshot,prerequisites.Catalog.Token)).Outcome==AccessSaveOutcome.Rejected,"C# refuses management grants without the required viewing access");
var viewerGrant=await roles.SaveAsync(AccessDraft.From(prerequisites.Record) with { Choices=new[]{roleRead,userRead} },prerequisites.Record.Snapshot,prerequisites.Catalog.Token);
Check(viewerGrant.Outcome==AccessSaveOutcome.Saved,"a valid read-only administrative grant set can be saved");
var viewers=await users.LoadCatalogAsync();
var viewer=await users.SaveAsync(new(0,prefix+"-viewer","",true,new[]{accountant}),"",viewers.Token,password);
Check(viewer.Outcome==AccessSaveOutcome.Saved,"a read-only viewer fixture can be assigned Accountant");
CurrentUser.UserId=viewer.Id;
var readOnlyRole=await roles.LoadAsync(accountant);
Check((await roles.SaveAsync(AccessDraft.From(readOnlyRole.Record) with { Description="Forbidden" },readOnlyRole.Record.Snapshot,readOnlyRole.Catalog.Token)).Outcome==AccessSaveOutcome.Rejected,"read-only role viewers cannot submit management writes");
var readOnlyUser=await users.LoadAsync(created.Id);
Check((await users.SaveAsync(AccessDraft.From(readOnlyUser.Record) with { Name="Forbidden" },readOnlyUser.Record.Snapshot,readOnlyUser.Catalog.Token)).Outcome==AccessSaveOutcome.Rejected,"read-only user viewers cannot submit management writes");
bool readOnlySql=false;
try{Query("EXEC dbo.SaveRolePermissions @UserId=@Actor,@RoleId=@Role,@PermissionIdsJson=N'[]';",DatabaseHelper.Int("@Actor",viewer.Id),DatabaseHelper.Int("@Role",accountant));}
catch(SqlException ex) when(ex.Number==50003){readOnlySql=true;}
Check(readOnlySql,"the SQL permission-save procedure no longer treats System.Roles read access as management");
CurrentUser.UserId=admin;
var actorFrame=await users.LoadAsync(created.Id);
var actorSave=users.SaveAsync(AccessDraft.From(actorFrame.Record) with { Name=prefix+"-actor" },actorFrame.Record.Snapshot,actorFrame.Catalog.Token);
CurrentUser.UserId=viewer.Id;var actorResult=await actorSave;CurrentUser.UserId=admin;
Check(actorResult.Outcome==AccessSaveOutcome.Saved,"queued save captures its acting user before background execution");
// Migration 59 intentionally retired generic AuditLogs writes. The actor
// contract is enforced by the stored procedure's SQL permission check, while
// the save must use the administrator captured before the session changed.
Check(actorResult.Intent?.Values.Name==prefix+"-actor"&&
    (await users.LoadAsync(created.Id)).Record.Name==prefix+"-actor",
    "the queued administrator save commits the exact submitted user changes despite a later session change");
var protectedRole=await roles.LoadAsync(adminRole);
Check((await roles.SaveAsync(AccessDraft.From(protectedRole.Record) with { Description="Forbidden" },protectedRole.Record.Snapshot,protectedRole.Catalog.Token)).Outcome==AccessSaveOutcome.Rejected,"Administrator role remains protected in the unified writer");
var raceFrame=await users.LoadAsync(created.Id);
var race=await Task.WhenAll(users.SaveAsync(AccessDraft.From(raceFrame.Record) with { Name=prefix+"-race-a" },raceFrame.Record.Snapshot,raceFrame.Catalog.Token),users.SaveAsync(AccessDraft.From(raceFrame.Record) with { Name=prefix+"-race-b" },raceFrame.Record.Snapshot,raceFrame.Catalog.Token));
Check(race.Count(result=>result.Outcome==AccessSaveOutcome.Saved)==1&&race.Count(result=>result.Error is AdministrativeStaleStateException)==1,"two simultaneous editors cannot silently overwrite the same user");
var winner=race.Single(result=>result.Outcome==AccessSaveOutcome.Saved);
Check((await users.LoadAsync(created.Id)).Record.Name==winner.Intent!.Values.Name,"the winning concurrent draft is stored completely");
var loginFrame=await users.LoadAsync(created.Id);
Query("UPDATE dbo.Users SET FailedLoginCount=FailedLoginCount+1 WHERE UserId=@Id;",DatabaseHelper.Int("@Id",created.Id));
Check((await users.LoadAsync(created.Id)).Record.Snapshot==loginFrame.Record.Snapshot,"unrelated login counters do not create false stale account edits");
var restore=await roles.LoadAsync(accountant);
Check((await roles.SaveAsync(AccessDraft.From(restore.Record) with { Description=baselineRole.Record.Description,Choices=originalGrants },restore.Record.Snapshot,restore.Catalog.Token)).Outcome==AccessSaveOutcome.Saved,"the fixture restores Accountant's original description and six grants");
var restored=await roles.LoadAsync(accountant);
var validUser=await users.LoadAsync(created.Id);
foreach(string payload in new[]{"null","{}","[\"1\"]","[null]","[0]","[-1]","[999999]","[1,\"bad\"]"})
{
    bool roleRefused=false,userRefused=false;
    try{Query("EXEC dbo.SaveRolePermissions @UserId=@Actor,@RoleId=@Role,@PermissionIdsJson=@Json;",DatabaseHelper.Int("@Actor",admin),DatabaseHelper.Int("@Role",accountant),DatabaseHelper.P("@Json",payload));}
    catch(SqlException ex) when(ex.Number is 57303 or 57304 or 57412){roleRefused=true;}
    try{Query("EXEC dbo.SaveUser @UserId=@Actor,@Username=@Name,@RoleId=0,@TargetUserId=@Target,@RoleIdsJson=@Json;",DatabaseHelper.Int("@Actor",admin),DatabaseHelper.P("@Name",validUser.Record.Name),DatabaseHelper.Int("@Target",created.Id),DatabaseHelper.P("@Json",payload));}
    catch(SqlException ex) when(ex.Number is 57224 or 57226 or 57413){userRefused=true;}
    Check(roleRefused&&userRefused,"malformed/object/string/null/nonpositive/unknown selections are refused by both SQL writers: "+payload);
}
bool dependenciesRefused=false;
try{Query("EXEC dbo.SaveRolePermissions @UserId=@Actor,@RoleId=@Role,@PermissionIdsJson=@Json;",DatabaseHelper.Int("@Actor",admin),DatabaseHelper.Int("@Role",accountant),DatabaseHelper.P("@Json","["+manage+"]"));}
catch(SqlException ex) when(ex.Number==57414){dependenciesRefused=true;}
Check(dependenciesRefused,"SQL enforces the same management-view prerequisites as C#");
Check((await roles.LoadAsync(accountant)).Record.Snapshot==restored.Record.Snapshot&&(await users.LoadAsync(created.Id)).Record.Snapshot==validUser.Record.Snapshot,"refused direct payloads do not change the stored account or role state");
bool creationRefused=false,renameRefused=false;
try{Query("EXEC dbo.SaveRole @UserId=@Actor,@RoleName=N'Other',@RoleId=0,@Description=N'Forbidden';",DatabaseHelper.Int("@Actor",admin));}
catch(SqlException ex) when(ex.Number==57410){creationRefused=true;}
try{Query("EXEC dbo.SaveRole @UserId=@Actor,@RoleName=N'Other',@RoleId=@Role,@Description=N'Forbidden';",DatabaseHelper.Int("@Actor",admin),DatabaseHelper.Int("@Role",accountant));}
catch(SqlException ex) when(ex.Number==57410){renameRefused=true;}
Check(creationRefused&&renameRefused,"direct SQL calls cannot create or rename roles outside the approved two-role policy");
var effective=Query("EXEC dbo.GetUserPermissions @UserId;",DatabaseHelper.Int("@UserId",viewer.Id));
Check(effective.AsEnumerable().Select(row=>(string)row["PermissionCode"]).ToHashSet().SetEquals(new[]{"Students.View","Fees.View","Fees.Collect","Reports.View","Fees.Reports.View","Fees.Statement.View"}),"a standalone Accountant receives exactly its preserved six capabilities");
foreach(string denied in new[]{"Students.Admit","Students.Administer","Fees.Adjust","Staff.Manage","System.Setup","System.Users.Manage","System.Roles.Manage"})
{
    bool refused=false;try{Query("EXEC dbo.RequirePermission @Actor,@Code;",DatabaseHelper.Int("@Actor",viewer.Id),DatabaseHelper.P("@Code",denied));}
    catch(SqlException ex) when(ex.Number==50003){refused=true;}
    Check(refused,"Accountant is denied unassigned capability "+denied);
}
Console.WriteLine($"PASS: {checks} native production administration SQL checks on disposable migrated fixture.");
