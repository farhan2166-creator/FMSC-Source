using Microsoft.Data.SqlClient;

string connection=Environment.GetEnvironmentVariable("FMSC_SAFETY_CONNECTION")??throw new Exception("Owned test connection required.");
string owner=Environment.GetEnvironmentVariable("FMSC_SAFETY_OWNER")??"";
string root=Environment.GetEnvironmentVariable("FMSC_SAFETY_ROOT")??"";
if(!Guid.TryParseExact(owner,"N",out _) || Path.GetFileName(Path.GetFullPath(root))!=owner)throw new Exception("Invalid fixture owner.");
var target = new SqlConnectionStringBuilder(connection);
if (!System.Text.RegularExpressions.Regex.IsMatch(target.DataSource, @"^127\.0\.0\.1,[1-9][0-9]{0,4}$") ||
    target.InitialCatalog != "master" || target.IntegratedSecurity || target.AttachDBFilename.Length != 0)
    throw new Exception("Only the marked disposable loopback instance is accepted.");
using(var c=new SqlConnection(connection))
{
    c.Open();using var q=new SqlCommand("SELECT CONVERT(nvarchar(32),value) FROM sys.extended_properties WHERE class=0 AND name=N'FMSC_SafetyChecksOwner';",c);
    if(Convert.ToString(q.ExecuteScalar())!=owner)throw new Exception("Only the freshly owned test instance is accepted.");
}
if (args.Contains("--backup-recovery")) TemplateChecks.Run(connection);
else FeeMatrixChecks.Run(connection);
RestoreChecks.Run(connection,root);
