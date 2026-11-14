using System.Data;
using Microsoft.Data.SqlClient;
// Pure parsing/HTTP tests fail closed if they accidentally reach school SQL.
namespace FMSC_SMS
{
    internal static class DatabaseHelper
    {
        internal static SqlParameter Int(string name,int value)=>new(name,value);
        internal static SqlParameter P(string name,object? value)=>new(name,value ?? DBNull.Value);
        internal static Task<DataTable> QuerySqlAsync(string text,params SqlParameter[] args)=>throw new InvalidOperationException("School SQL is forbidden in transport tests.");
    }
}
namespace FMSC_SMS.Core
{
    internal static class CurrentUser
    {
        internal static int UserId=>1;
        internal static bool HasPermission(string permission)=>throw new InvalidOperationException("Permission reads are not part of pure transport tests.");
    }
    internal sealed record CommunicationDeliveryOutcome(string State,string? ProviderMessageId,string? FailureCode);
}
