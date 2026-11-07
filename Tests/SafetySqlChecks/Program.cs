using Microsoft.Data.SqlClient;

string connection=Environment.GetEnvironmentVariable("FMSC_SAFETY_CONNECTION")??throw new Exception("Owned test connection required.");
string owner=Environment.GetEnvironmentVariable("FMSC_SAFETY_OWNER")??"";
string root=Environment.GetEnvironmentVariable("FMSC_SAFETY_ROOT")??"";
if(!Guid.TryParseExact(owner,"N",out _) || Path.GetFileName(Path.GetFullPath(root))!=owner)throw new Exception("Invalid fixture owner.");
using(var c=new SqlConnection(connection))
{
    c.Open();using var q=new SqlCommand("SELECT CONVERT(nvarchar(32),value) FROM sys.extended_properties WHERE class=0 AND name=N'FMSC_SafetyChecksOwner';",c);
    if(Convert.ToString(q.ExecuteScalar())!=owner)throw new Exception("Only the freshly owned test instance is accepted.");
}
FeeMatrixChecks.Run(connection);
RestoreChecks.Run(connection,root);
