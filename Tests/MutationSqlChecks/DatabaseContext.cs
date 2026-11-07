using Microsoft.Data.SqlClient;

namespace FMSC_SMS
{
    // Test connection only; never use the production app's auto-attach path.
    public static partial class DatabaseHelper
    {
        // Required schema is generated from Installer/core.ps1 at compilation.
        internal static string ConnectionString = "";
        public static SqlConnection GetConnection()=>new(ConnectionString);
        public static void OpenConnection(SqlConnection connection)=>connection.Open();
        public static SqlParameter P(string name,object? value)=>new(name,Core.WholeRupees.ValidateParameter(name,value)??DBNull.Value);
        public static SqlParameter Int(string name,int value)=>new(name,SqlDbType.Int){Value=value};
        public static SqlParameter Bit(string name,bool value)=>new(name,SqlDbType.Bit){Value=value};
        public static SqlParameter Date(string name,DateTime value)=>new(name,SqlDbType.Date){Value=value.Date};
    }
}

namespace FMSC_SMS.Pages.Security
{
    public static class CurrentUser
    {
        public static int UserId { get; internal set; }
    }
}
