using Microsoft.Data.SqlClient;

namespace FMSC_SMS.Services.Backup;

// BD-29/30: reject unknown/foreign backup identity and retain safety-copy/rollback recovery.
// The owner supplies detached release data; missing development MDF/LDF files are not a deployment-policy change.
internal sealed class BackupIdentityException(string message) : Exception(message);

// A database name and valid backup checksum do not identify the school records.
// Inspect a disposable restore before the caller switches the live database out.
internal static class BackupRestoreIdentity
{
    internal static Guid ReadScope(SqlConnection connection)
    {
        using var command=new SqlCommand("SELECT TRY_CONVERT(uniqueidentifier,value) FROM sys.extended_properties WHERE class=0 AND name=N'FMSC_QuickRegistrationScope';",connection);
        if(command.ExecuteScalar() is not Guid scope || scope==Guid.Empty)
            throw new BackupIdentityException("The database recovery identity is missing. Restore was stopped; ask the administrator to verify the original school database.");
        return scope;
    }

    internal static void RequireSame(Guid expected,Guid candidate)
    {
        if(expected==Guid.Empty || candidate==Guid.Empty || expected!=candidate)
            throw new BackupIdentityException("This backup belongs to a different school database. Restore was stopped before replacing your current records.");
    }

    internal static void ValidateCandidate(string masterConnectionString,string backupPath,string scratchRoot,Guid expected,Action<SqlConnection> validate)
    {
        if(expected==Guid.Empty)throw new BackupIdentityException("The current school database identity could not be verified. Restore was stopped.");
        ValidateCore(masterConnectionString, backupPath, scratchRoot, expected, validate);
    }

    // The ONLY scope-free identity check is the explicit recovery of an ABSENT
    // school database. The caller must first authorize the new computer, verify
    // an existing school Administrator in this disposable catalog and ensure no
    // original/attached FMSC_DB exists. Ordinary restore cannot use this entry.
    internal static void ValidateCandidateForNewComputer(string masterConnectionString,
        string backupPath, string scratchRoot, Action<SqlConnection> validate)
        => ValidateCore(masterConnectionString, backupPath, scratchRoot, null, validate);

    private static void ValidateCore(string masterConnectionString,string backupPath,string scratchRoot,
        Guid? expected,Action<SqlConnection> validate)
    {
        string owner=Guid.NewGuid().ToString("N"),catalog="FMSC_RestoreCheck_"+owner;
        string root=Path.GetFullPath(scratchRoot),folder=Path.GetFullPath(Path.Combine(root,owner));
        if(!folder.StartsWith(Path.TrimEndingDirectorySeparator(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new BackupIdentityException("The backup verification folder is invalid. Restore was stopped.");
        if(Directory.Exists(folder))throw new BackupIdentityException("The temporary backup verification folder already exists. Restore was stopped.");
        Directory.CreateDirectory(folder);
        var masterBuilder=new SqlConnectionStringBuilder(masterConnectionString){InitialCatalog="master",Pooling=false};
        using var master=new SqlConnection(masterBuilder.ConnectionString);
        bool reserved=false;
        try
        {
            master.Open();
            using(var exists=new SqlCommand("SELECT COUNT(*) FROM sys.databases WHERE name=@Name;",master))
            {
                exists.Parameters.AddWithValue("@Name",catalog);
                if(Convert.ToInt32(exists.ExecuteScalar())!=0)throw new BackupIdentityException("The temporary backup verification name is already in use. Restore was stopped.");
            }
            // Reserve ownership before RESTORE: a failed restore can leave a
            // partial catalog whose files still need ownership-checked cleanup.
            reserved=true;
            using var files=new DataTable();
            using(var list=new SqlCommand("RESTORE FILELISTONLY FROM DISK=@Path WITH FILE=1;",master){CommandTimeout=0})
            {
                list.Parameters.Add("@Path",SqlDbType.NVarChar,1000).Value=backupPath;
                using var adapter=new SqlDataAdapter(list);adapter.Fill(files);
            }
            if(files.Rows.Count==0 || !files.AsEnumerable().Any(r=>Convert.ToString(r["Type"])=="D") || !files.AsEnumerable().Any(r=>Convert.ToString(r["Type"])=="L"))
                throw new BackupIdentityException("The selected backup does not contain a complete database file pair. Restore was stopped.");
            using(var restore=new SqlCommand{Connection=master,CommandTimeout=0})
            {
                restore.Parameters.Add("@Path",SqlDbType.NVarChar,1000).Value=backupPath;
                var moves=new List<string>();int index=0;
                foreach(DataRow row in files.Rows)
                {
                    string type=Convert.ToString(row["Type"])??"";
                    if(type is not ("D" or "L"))throw new BackupIdentityException("This backup uses unsupported file storage. Ask the administrator to verify it before restoring.");
                    string file=Path.Combine(folder,"file-"+index+(type=="L"?".ldf":".mdf"));
                    moves.Add($"MOVE @Logical{index} TO @File{index}");
                    restore.Parameters.Add("@Logical"+index,SqlDbType.NVarChar,128).Value=Convert.ToString(row["LogicalName"]);
                    restore.Parameters.Add("@File"+index,SqlDbType.NVarChar,1000).Value=file;
                    index++;
                }
                // Every file is redirected. Never use the original backup's
                // physical paths, and never use REPLACE for this fresh catalog.
                restore.CommandText=$"RESTORE DATABASE [{catalog}] FROM DISK=@Path WITH FILE=1, RECOVERY, CHECKSUM, "+string.Join(", ",moves)+";";
                restore.ExecuteNonQuery();
            }
            var candidateBuilder=new SqlConnectionStringBuilder(masterBuilder.ConnectionString){InitialCatalog=catalog,Pooling=false};
            using var candidate=new SqlConnection(candidateBuilder.ConnectionString);candidate.Open();
            Guid candidateScope = ReadScope(candidate);
            if (expected is Guid actualScope) RequireSame(actualScope, candidateScope);
            validate(candidate);
        }
        finally
        {
            if(reserved)DropOwnedCandidate(master,catalog,folder);
            // Drop must finish first. A failed cleanup retains its files instead
            // of deleting files belonging to an attached or unexpected catalog.
            if(Directory.Exists(folder))Directory.Delete(folder,recursive:true);
        }
    }

    private static void DropOwnedCandidate(SqlConnection master,string catalog,string folder)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(catalog,"^FMSC_RestoreCheck_[0-9a-f]{32}$") || Path.GetFileName(folder)!=catalog[18..])
            throw new BackupIdentityException("Temporary backup verification ownership could not be checked. Its files were retained for review.");
        using var query=new SqlCommand("SELECT physical_name FROM sys.master_files WHERE database_id=DB_ID(@Name);",master);
        query.Parameters.Add("@Name",SqlDbType.NVarChar,128).Value=catalog;
        var paths=new List<string>();using(var reader=query.ExecuteReader()){while(reader.Read())paths.Add(reader.GetString(0));}
        if(paths.Count==0)
        {
            using var exists=new SqlCommand("SELECT COUNT(*) FROM sys.databases WHERE name=@Name;",master);exists.Parameters.AddWithValue("@Name",catalog);
            if(Convert.ToInt32(exists.ExecuteScalar())!=0)throw new BackupIdentityException("Temporary backup verification files could not be identified. The catalog was retained for review.");
            return;
        }
        string prefix=Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))+Path.DirectorySeparatorChar;
        if(paths.Any(path=>!Path.GetFullPath(path).StartsWith(prefix,StringComparison.OrdinalIgnoreCase)))
            throw new BackupIdentityException("Temporary backup verification found an unexpected file location. The catalog was retained for review.");
        using var drop=new SqlCommand($"IF EXISTS(SELECT 1 FROM sys.databases WHERE name=N'{catalog}' AND state=0) ALTER DATABASE [{catalog}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{catalog}];",master){CommandTimeout=30};
        drop.ExecuteNonQuery();
    }
}
