using System.Security.Cryptography;
using System.Text.Json;
using FMSC_SMS.Core;
using Microsoft.Data.SqlClient;

namespace FMSC_SMS.Pages.Administration;

internal interface IAdministrationStore
{
    Task<AccessCatalog> LoadCatalogAsync();
    Task<(AccessCatalog Catalog, AccessRecord Record)> LoadAsync(int id);
    Task<AccessSaveResult> SaveAsync(AccessDraft draft, string snapshot, string catalogToken, string? password = null);
    Task<AccessSaveResult> ResetPasswordAsync(AccessRecord original, string catalogToken, string password, bool forceChange);
    Task<AccessCheckResult> CheckAsync(AccessWriteIntent intent);
}

/// <summary>All administration reads/writes use a captured actor and one coherent SQL transaction.</summary>
internal sealed class AdministrationStore(AccessEditorKind kind) : IAdministrationStore
{
    private sealed class Transaction : IAdministrativeTransaction
    {
        internal readonly SqlConnection Connection;
        internal readonly SqlTransaction Sql;
        public bool SubmissionStarted { get; private set; }
        internal Transaction()
        {
            Connection = DatabaseHelper.GetConnection();
            try { DatabaseHelper.OpenConnection(Connection); Sql = Connection.BeginTransaction(IsolationLevel.Serializable); }
            catch { Connection.Dispose(); throw; }
        }
        internal void Submitted() => SubmissionStarted = true;
        public void Commit() => Sql.Commit();
        public void Rollback() => Sql.Rollback();
        public void Dispose()
        {
            // Cleanup must not replace a confirmed commit result with an apparent failed save.
            try { Sql.Dispose(); }
            catch(Exception ex){System.Diagnostics.Trace.TraceWarning("Administration transaction cleanup: {0}",ex.GetType().Name);}
            try { Connection.Dispose(); }
            catch(Exception ex){System.Diagnostics.Trace.TraceWarning("Administration connection cleanup: {0}",ex.GetType().Name);}
        }
    }

    private static DataSet Query(Transaction transaction, string sql, params SqlParameter[] parameters)
    {
        using var command = new SqlCommand(sql, transaction.Connection, transaction.Sql) { CommandTimeout = 120 };
        command.Parameters.AddRange(parameters);
        var data = new DataSet();
        try { using var adapter = new SqlDataAdapter(command); adapter.Fill(data); return data; }
        catch { data.Dispose(); throw; }
    }
    private static int Number(DataRow row, string name) => row[name] is int value && value > 0
        ? value : throw new InvalidDataException($"Administrative data has no valid {name}.");
    private static string Text(DataRow row, string name) => row[name] is DBNull ? "" : row[name] is string value
        ? value : throw new InvalidDataException($"Administrative data has an invalid {name}.");
    private static bool Flag(DataRow row, string name) => row[name] is bool value
        ? value : throw new InvalidDataException($"Administrative data has an invalid {name}.");

    private AccessCatalog Catalog(Transaction transaction, int actor)
    {
        // Canonical lock order: role/permission catalog, then selected record/memberships.
        // This also locks catalog definitions while a save verifies its displayed access preview.
        using var data = Query(transaction, """
            EXEC dbo.RequireAnyPermission @Actor,N'System.Roles',N'System.Administration';
            SELECT RoleId,RoleName,Description FROM dbo.Roles WITH(UPDLOCK,HOLDLOCK) ORDER BY RoleId;
            SELECT PermissionId,PermissionCode,PermissionName,Description FROM dbo.Permissions WITH(UPDLOCK,HOLDLOCK) ORDER BY PermissionId;
            SELECT RoleId,PermissionId FROM dbo.RolePermissions WITH(UPDLOCK,HOLDLOCK) ORDER BY RoleId,PermissionId;
            """, DatabaseHelper.Int("@Actor", actor));
        if (data.Tables.Count != 3) throw new InvalidDataException("The access catalog is incomplete.");
        var permissions = data.Tables[1].AsEnumerable().Select(row => new AccessPermission(Number(row,"PermissionId"),
            Text(row,"PermissionCode"), Text(row,"PermissionName"), Text(row,"Description"))).ToArray();
        var mappings = data.Tables[2].AsEnumerable().Select(row => (Role: Number(row,"RoleId"), Permission: Number(row,"PermissionId"))).ToArray();
        var roles = data.Tables[0].AsEnumerable().Select(row => new AccessRole(Number(row,"RoleId"), Text(row,"RoleName"),
            Text(row,"Description"), mappings.Where(map => map.Role == Number(row,"RoleId")).Select(map => map.Permission).Distinct().Order().ToArray())).ToArray();
        if (roles.Length != 2 || !roles.Any(role => AdministrativeAccess.IsAccountant(role.Name)) ||
            !PermissionCatalog.IsSupportedCatalog(permissions.Select(permission => permission.Code)))
            throw new InvalidDataException("The database needs migration 3: Administrator, Accountant and the supported permission catalog.");
        if (roles.Count(role => role.Protected) != 1 || roles.Any(role => string.IsNullOrWhiteSpace(role.Name)) ||
            roles.Select(role => role.Id).Distinct().Count() != roles.Length ||
            roles.Select(role => role.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != roles.Length ||
            permissions.Any(permission => string.IsNullOrWhiteSpace(permission.Code)) ||
            permissions.Select(permission => permission.Id).Distinct().Count() != permissions.Length ||
            permissions.Select(permission => permission.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != permissions.Length ||
            mappings.Any(map => !roles.Any(role => role.Id == map.Role) || !permissions.Any(permission => permission.Id == map.Permission)))
            throw new InvalidDataException("The role/permission catalog is invalid. Review the database definitions before editing access.");
        AccessListItem[] items;
        if (kind == AccessEditorKind.Users)
        {
            using var users = Query(transaction, """
                EXEC dbo.RequireAnyPermission @Actor,N'System.Users',N'System.Administration';
                SELECT U.UserId,U.Username,
                    CONVERT(bit,CASE WHEN U.RoleId=@AdministratorRoleId OR EXISTS(
                        SELECT 1 FROM dbo.UserRoles UR JOIN dbo.Roles AR ON AR.RoleId=UR.RoleId
                        WHERE UR.UserId=U.UserId AND AR.RoleId=@AdministratorRoleId) THEN 1 ELSE 0 END) IsProtected,
                    (SELECT COUNT(*) FROM dbo.UserRoles UR WHERE UR.UserId=U.UserId) RoleCount
                    ,(SELECT STRING_AGG(CONVERT(nvarchar(max),RR.RoleName),N', ') WITHIN GROUP(ORDER BY RR.RoleName)
                        FROM dbo.UserRoles UR JOIN dbo.Roles RR ON RR.RoleId=UR.RoleId WHERE UR.UserId=U.UserId) RoleNames
                FROM dbo.Users U JOIN dbo.Roles R ON R.RoleId=U.RoleId ORDER BY U.Username,U.UserId;
                """, DatabaseHelper.Int("@Actor", actor),DatabaseHelper.Int("@AdministratorRoleId",roles.Single(role=>role.Protected).Id));
            items = users.Tables[0].AsEnumerable().Select(row => new AccessListItem(Number(row,"UserId"),Text(row,"Username"),
                Flag(row,"IsProtected") ? "Protected administrator" : Convert.ToInt32(row["RoleCount"])==0 ? "No roles · no access" : Text(row,"RoleNames"),Flag(row,"IsProtected"))).ToArray();
        }
        else items = roles.Select(role => new AccessListItem(role.Id,role.Name,
            role.Protected ? "Protected · all permissions" : $"{role.Permissions.Length} permission(s)"+(role.Description.Length>0?" · "+role.Description:""),role.Protected)).ToArray();
        if (items.Select(item => item.Id).Distinct().Count() != items.Length || items.Any(item => string.IsNullOrWhiteSpace(item.Name)))
            throw new InvalidDataException("The administrative list is invalid.");
        return new(roles,permissions,items,AdministrativeAccess.CatalogToken(roles,permissions));
    }

    private AccessRecord? Record(Transaction transaction, int actor, int id, AccessCatalog catalog)
    {
        string sql = kind == AccessEditorKind.Users ? """
            EXEC dbo.RequireAnyPermission @Actor,N'System.Users',N'System.Administration';
            SELECT U.UserId,U.Username,U.MustChangePassword,
                CONVERT(bit,CASE WHEN U.RoleId=@AdministratorRoleId OR EXISTS(
                    SELECT 1 FROM dbo.UserRoles UR JOIN dbo.Roles AR ON AR.RoleId=UR.RoleId
                    WHERE UR.UserId=U.UserId AND AR.RoleId=@AdministratorRoleId) THEN 1 ELSE 0 END) IsProtected,
                CONVERT(varchar(64),HASHBYTES('SHA2_256',ISNULL(U.PasswordHash,0x)+ISNULL(U.PasswordSalt,0x)),2) CredentialProof
            FROM dbo.Users U WITH(UPDLOCK,HOLDLOCK) JOIN dbo.Roles R ON R.RoleId=U.RoleId WHERE U.UserId=@Id;
            SELECT RoleId ChoiceId FROM dbo.UserRoles WITH(UPDLOCK,HOLDLOCK) WHERE UserId=@Id ORDER BY RoleId;
            SELECT CONCAT(
                (SELECT Username,MustChangePassword,RoleId,PasswordHash,PasswordSalt,UpdatedAt FROM dbo.Users WITH(UPDLOCK,HOLDLOCK) WHERE UserId=@Id FOR JSON PATH,INCLUDE_NULL_VALUES),
                (SELECT RoleId FROM dbo.UserRoles WITH(UPDLOCK,HOLDLOCK) WHERE UserId=@Id ORDER BY RoleId FOR JSON PATH)) Material;
            """ : """
            EXEC dbo.RequireAnyPermission @Actor,N'System.Roles',N'System.Administration';
            SELECT R.RoleId,R.RoleName,R.Description,
                CONVERT(bit,CASE WHEN R.RoleName=N'Administrator' THEN 1 ELSE 0 END) IsProtected,
                (SELECT COUNT(*) FROM dbo.UserRoles UR WITH(UPDLOCK,HOLDLOCK) WHERE UR.RoleId=R.RoleId) MemberCount
            FROM dbo.Roles R WITH(UPDLOCK,HOLDLOCK) WHERE R.RoleId=@Id;
            SELECT PermissionId ChoiceId FROM dbo.RolePermissions WITH(UPDLOCK,HOLDLOCK) WHERE RoleId=@Id ORDER BY PermissionId;
            SELECT CONCAT(
                (SELECT RoleId,RoleName,Description FROM dbo.Roles WITH(UPDLOCK,HOLDLOCK) WHERE RoleId=@Id FOR JSON PATH,INCLUDE_NULL_VALUES),
                (SELECT PermissionId FROM dbo.RolePermissions WITH(UPDLOCK,HOLDLOCK) WHERE RoleId=@Id ORDER BY PermissionId FOR JSON PATH),
                (SELECT UserId FROM dbo.UserRoles WITH(UPDLOCK,HOLDLOCK) WHERE RoleId=@Id ORDER BY UserId FOR JSON PATH)) Material;
            """;
        using var data = Query(transaction,sql,DatabaseHelper.Int("@Actor",actor),DatabaseHelper.Int("@Id",id),
            DatabaseHelper.Int("@AdministratorRoleId",catalog.Roles.Single(role=>role.Protected).Id));
        if (data.Tables.Count != 3) throw new InvalidDataException("The administrative record is incomplete.");
        if (data.Tables[0].Rows.Count == 0) return null;
        if (data.Tables[0].Rows.Count != 1 || data.Tables[2].Rows.Count != 1) throw new InvalidDataException("The administrative record is ambiguous.");
        var row = data.Tables[0].Rows[0];
        int returned = Number(row,kind == AccessEditorKind.Users ? "UserId" : "RoleId");
        if (returned != id) throw new InvalidDataException("The selected administrative identity could not be verified.");
        int[] choices = data.Tables[1].AsEnumerable().Select(choice => Number(choice,"ChoiceId")).Distinct().Order().ToArray();
        return new(id,Text(row,kind == AccessEditorKind.Users ? "Username" : "RoleName"),
            kind == AccessEditorKind.Roles ? Text(row,"Description") : "",kind == AccessEditorKind.Users && Flag(row,"MustChangePassword"),
            Flag(row,"IsProtected") || kind==AccessEditorKind.Roles&&AdministrativeAccess.IsAdministrator(Text(row,"RoleName")),choices,AdministrativeAccess.Token(Text(data.Tables[2].Rows[0],"Material")),
            kind == AccessEditorKind.Users ? Text(row,"CredentialProof") : "",kind == AccessEditorKind.Roles ? Convert.ToInt32(row["MemberCount"]) : 0);
    }

    public Task<AccessCatalog> LoadCatalogAsync()
    {
        int actor = CurrentUser.UserId;
        return Task.Run(() => { using var transaction = new Transaction(); var catalog = Catalog(transaction,actor); transaction.Commit(); return catalog; });
    }
    public Task<(AccessCatalog Catalog, AccessRecord Record)> LoadAsync(int id)
    {
        int actor = CurrentUser.UserId;
        return Task.Run(() => {
            using var transaction = new Transaction(); var catalog = Catalog(transaction,actor);
            var record = Record(transaction,actor,id,catalog) ?? throw new KeyNotFoundException("This record no longer exists. The current draft has been retained.");
            transaction.Commit(); return (catalog,record);
        });
    }

    public Task<AccessSaveResult> SaveAsync(AccessDraft draft, string snapshot, string catalogToken, string? password = null)
    {
        int actor = CurrentUser.UserId;
        var captured = draft.Clean();
        return Task.Run(() => Write(actor,captured,snapshot,catalogToken,password));
    }
    public Task<AccessSaveResult> ResetPasswordAsync(AccessRecord original, string catalogToken, string password, bool forceChange)
    {
        if (kind != AccessEditorKind.Users) throw new InvalidOperationException("Password reset applies to users only.");
        return SaveAsync(AccessDraft.From(original) with { ForcePasswordChange = forceChange },original.Snapshot,catalogToken,password);
    }

    private AccessSaveResult Write(int actor, AccessDraft draft, string snapshot, string catalogToken, string? password)
    {
        byte[]? salt = null, hash = null;
        try
        {
            if (password is not null)
            {
                if (kind != AccessEditorKind.Users || !PasswordPolicy.IsValid(password)) throw new ArgumentException(PasswordPolicy.Requirement);
                salt = RandomNumberGenerator.GetBytes(16);
                hash = Rfc2898DeriveBytes.Pbkdf2(password,salt,100_000,HashAlgorithmName.SHA512,64);
            }
            if (kind == AccessEditorKind.Users && draft.Id == 0 && hash is null) throw new ArgumentException("Enter a password for the new user.");
            string proof = hash is null ? "" : Convert.ToHexString(SHA256.HashData(hash.Concat(salt!).ToArray()));
            var intent = new AccessWriteIntent(kind,draft,proof,snapshot);
            using var transaction = new Transaction();
            return AdministrativeSaveCoordinator.Execute(transaction,intent,() => {
                using (Query(transaction,kind == AccessEditorKind.Users
                    ? "EXEC dbo.RequireAnyPermission @Actor,N'System.Users.Manage',N'System.Administration';"
                    : "EXEC dbo.RequireAnyPermission @Actor,N'System.Roles.Manage',N'System.Administration';",DatabaseHelper.Int("@Actor",actor))) { }
                var catalog = Catalog(transaction,actor);
                if (string.IsNullOrWhiteSpace(catalogToken) || catalog.Token != catalogToken)
                    throw new AdministrativeStaleStateException("Access definitions changed since this screen loaded. Your draft is retained; reload and review before saving.");
                if (draft.Id > 0)
                {
                    var current = Record(transaction,actor,draft.Id,catalog);
                    if (current is null || string.IsNullOrWhiteSpace(snapshot) || current.Snapshot != snapshot)
                        throw new AdministrativeStaleStateException("This record changed since it was opened. Your draft is retained; reload and review before saving.");
                    if (current.Protected) throw new InvalidOperationException("The Administrator account/role is protected.");
                }
                if (AdministrativeAccess.Validate(kind,draft,catalog) is { } problem) throw new ArgumentException(problem);
                transaction.Submitted();
                if (kind == AccessEditorKind.Users)
                {
                    using var saved = Procedure(transaction,"dbo.SaveUser",actor,
                        DatabaseHelper.P("@Username",draft.Name),DatabaseHelper.Int("@RoleId",0),DatabaseHelper.Int("@TargetUserId",draft.Id),
                        DatabaseHelper.Bit("@MustChangePassword",draft.ForcePasswordChange),DatabaseHelper.P("@RoleIdsJson",JsonSerializer.Serialize(draft.Choices)),
                        new SqlParameter("@PasswordHash",SqlDbType.VarBinary,64){Value=hash ?? (object)DBNull.Value},
                        new SqlParameter("@PasswordSalt",SqlDbType.VarBinary,16){Value=salt ?? (object)DBNull.Value});
                    return SavedId(saved,"UserId");
                }
                using var role = Procedure(transaction,"dbo.SaveRole",actor,DatabaseHelper.P("@RoleName",draft.Name),
                    DatabaseHelper.P("@Description",draft.Description),DatabaseHelper.Int("@RoleId",draft.Id));
                int id = SavedId(role,"RoleId");
                using var permissions = Procedure(transaction,"dbo.SaveRolePermissions",actor,DatabaseHelper.Int("@RoleId",id),
                    DatabaseHelper.P("@PermissionIdsJson",JsonSerializer.Serialize(draft.Choices)));
                if (SavedId(permissions,"RoleId") != id) throw new InvalidDataException("The permission save returned a different role.");
                return id;
            });
        }
        catch (Exception ex) { return new(AccessSaveOutcome.Rejected,Error:ex); }
        finally { if (hash is not null) CryptographicOperations.ZeroMemory(hash); if (salt is not null) CryptographicOperations.ZeroMemory(salt); }
    }

    private static DataSet Procedure(Transaction transaction, string name, int actor, params SqlParameter[] parameters)
    {
        using var command = new SqlCommand(name,transaction.Connection,transaction.Sql){CommandType=CommandType.StoredProcedure,CommandTimeout=120};
        command.Parameters.AddRange(parameters); command.Parameters.Add(DatabaseHelper.Int("@UserId",actor));
        var data = new DataSet();
        try
        {
            using (var adapter = new SqlDataAdapter(command)) adapter.Fill(data);
            using var check = new SqlCommand("SELECT @@TRANCOUNT;",transaction.Connection,transaction.Sql);
            if (Convert.ToInt32(check.ExecuteScalar()) != 1) throw new InvalidOperationException("The procedure did not preserve its enclosing transaction.");
            return data;
        }
        catch { data.Dispose(); throw; }
    }
    private static int SavedId(DataSet data, string column)
    {
        var matches = data.Tables.Cast<DataTable>().Where(table => table.Columns.Contains(column)).ToArray();
        if (matches.Length != 1 || matches[0].Rows.Count != 1) throw new InvalidDataException("The database save response is incomplete or ambiguous.");
        return Number(matches[0].Rows[0],column);
    }

    public Task<AccessCheckResult> CheckAsync(AccessWriteIntent intent)
    {
        int actor = CurrentUser.UserId;
        return Task.Run(() => {
            if (intent.Kind != kind) throw new InvalidOperationException("The requested save belongs to another editor.");
            using var transaction = new Transaction(); var catalog = Catalog(transaction,actor);
            int id = intent.Values.Id > 0 ? intent.Values.Id
                : catalog.Items.SingleOrDefault(item => string.Equals(item.Name,intent.Values.Name,StringComparison.OrdinalIgnoreCase))?.Id ?? 0;
            var record = id > 0 ? Record(transaction,actor,id,catalog) : null;
            var outcome = record is not null && intent.Matches(record) ? AccessCheckOutcome.Saved
                : record is not null && intent.OriginalSnapshot.Length > 0 && record.Snapshot == intent.OriginalSnapshot ||
                    record is null && intent.OriginalSnapshot.Length == 0 && !catalog.Items.Any(item => string.Equals(item.Name,intent.Values.Name,StringComparison.OrdinalIgnoreCase))
                    ? AccessCheckOutcome.NotApplied : AccessCheckOutcome.Changed;
            transaction.Commit(); return new AccessCheckResult(outcome,catalog,record);
        });
    }
}
