using FMSC_SMS.Services.Backup;
using Microsoft.Data.SqlClient;

internal static class RestoreChecks
{
    internal static void Run(string master,string root)
    {
        using var c=new SqlConnection(master);c.Open();int checks=0;
        void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;}
        void Execute(string sql){using var q=new SqlCommand(sql,c){CommandTimeout=45};q.ExecuteNonQuery();}
        object? Scalar(string sql){using var q=new SqlCommand(sql,c){CommandTimeout=20};return q.ExecuteScalar();}
        string Quote(string value)=>"N'"+value.Replace("'","''")+"'";
        string PathFor(string file)=>Path.Combine(root,file);
        Guid original=Guid.NewGuid(),other=Guid.NewGuid();
        void CreateSource(Guid scope,string marker)
        {
            Execute($"CREATE DATABASE [FMSC_DB] ON PRIMARY(NAME=N'SourceData',FILENAME={Quote(PathFor("source.mdf"))}) LOG ON(NAME=N'SourceLog',FILENAME={Quote(PathFor("source.ldf"))});");
            Execute($"USE FMSC_DB; CREATE TABLE dbo.TestMarker(Value nvarchar(20)); INSERT dbo.TestMarker VALUES({Quote(marker)}); CREATE TABLE dbo.TestVersion(Value int); INSERT dbo.TestVersion VALUES(41); DECLARE @Scope uniqueidentifier='{scope:D}'; EXEC sys.sp_addextendedproperty @name=N'FMSC_QuickRegistrationScope',@value=@Scope; USE master;");
        }
        string Backup(string file)
        {
            string path=PathFor(file);Execute($"BACKUP DATABASE FMSC_DB TO DISK={Quote(path)} WITH COPY_ONLY,CHECKSUM,INIT;");FixtureFileAccess.MakeReadable(path);return path;
        }
        void Validate(SqlConnection candidate)
        {
            using var q=new SqlCommand("SELECT Value FROM dbo.TestVersion;",candidate);
            if(Convert.ToInt32(q.ExecuteScalar())!=41)throw new InvalidDataException("Synthetic incompatible schema.");
        }
        void Probe(string path)=>BackupRestoreIdentity.ValidateCandidate(master,path,PathFor("checks"),original,Validate);
        void RejectIdentity(string path,string label)
        {
            try{Probe(path);throw new Exception("Foreign or missing backup identity accepted");}
            catch(BackupIdentityException){Check(true,label);}
        }
        CreateSource(original,"Original");string good=Backup("same-school.bak");
        Execute($"RESTORE DATABASE [CurrentSchool] FROM DISK={Quote(good)} WITH MOVE N'SourceData' TO {Quote(PathFor("current.mdf"))}, MOVE N'SourceLog' TO {Quote(PathFor("current.ldf"))},CHECKSUM,RECOVERY;");
        using(var locked=new FileStream(good,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            Probe(good);Check(true,"A same-identity backup remains readable by SQL while protected against replacement");
            BackupRestoreIdentity.ValidateCandidateForNewComputer(master, good,
                PathFor("new-computer-checks"), candidate =>
                {
                    Check(BackupRestoreIdentity.ReadScope(candidate)==original,
                        "Replacement-PC isolated candidate exposes genuine school identity.");
                    Validate(candidate);
                });
            Check(true,"A replacement-PC candidate was validated in disposable SQL without touching the original.");
            if (OperatingSystem.IsWindows())
            {
                try{using var overwrite=new FileStream(good,FileMode.Open,FileAccess.Write,FileShare.ReadWrite);throw new Exception("Protected backup was writable");}
                catch(IOException){Check(true,"The protected backup cannot be overwritten during validation/restore");}
            }
            else Console.WriteLine("SKIP: Windows file sharing enforcement; covered by native Windows recovery checks.");
        }
        Execute("DROP DATABASE FMSC_DB;");CreateSource(other,"Foreign");string foreign=Backup("different-school.bak");
        using(var header=new SqlCommand($"RESTORE HEADERONLY FROM DISK={Quote(foreign)};",c))
        using(var reader=header.ExecuteReader()){reader.Read();Check(Convert.ToString(reader["DatabaseName"])=="FMSC_DB","The foreign backup has the same FMSC_DB header name as a legitimate backup");}
        RejectIdentity(foreign,"A compatible backup from another school is refused before replacing current data");
        Execute("DROP DATABASE FMSC_DB;");
        Execute($"RESTORE DATABASE [FMSC_DB] FROM DISK={Quote(good)} WITH MOVE N'SourceData' TO {Quote(PathFor("source.mdf"))}, MOVE N'SourceLog' TO {Quote(PathFor("source.ldf"))},CHECKSUM,RECOVERY;");
        Execute($"USE FMSC_DB; DECLARE @Scope uniqueidentifier='{other:D}'; EXEC sys.sp_updateextendedproperty @name=N'FMSC_QuickRegistrationScope',@value=@Scope; USE master;");
        string fork=Backup("same-family-foreign-school.bak");
        RejectIdentity(fork,"Even a copied database family is refused when its logical school identity differs");
        Execute("USE FMSC_DB; EXEC sys.sp_dropextendedproperty @name=N'FMSC_QuickRegistrationScope'; USE master;");
        string noScope=Backup("missing-identity.bak");
        RejectIdentity(noScope,"A backup without a recovery identity is refused");
        try
        {
            BackupRestoreIdentity.ValidateCandidateForNewComputer(master,noScope,
                PathFor("new-computer-checks"),_ => throw new Exception("Scope-less backup accepted"));
            throw new Exception("A replacement PC accepted a backup without school identity.");
        }
        catch(BackupIdentityException)
        {
            Check(true,"Replacement-computer preflight also rejects a missing school identity.");
        }
        Execute($"USE FMSC_DB; DECLARE @Scope uniqueidentifier='{original:D}'; EXEC sys.sp_addextendedproperty @name=N'FMSC_QuickRegistrationScope',@value=@Scope; UPDATE dbo.TestVersion SET Value=999; USE master;");
        try{Probe(Backup("incompatible-schema.bak"));throw new Exception("Incompatible candidate accepted");}
        catch(InvalidDataException){Check(true,"Matching identity does not bypass application compatibility");}
        Check(Convert.ToString(Scalar("SELECT Value FROM CurrentSchool.dbo.TestMarker;"))=="Original","Every rejected backup leaves the original working database untouched");
        Check(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM sys.databases WHERE name LIKE N'FMSC[_]RestoreCheck[_]%';"))==0,"Temporary restored catalogs are cleaned after successful and rejected validations");
        Check(!Directory.Exists(PathFor("checks"))||Directory.GetFileSystemEntries(PathFor("checks")).Length==0,"Temporary database files are cleaned after all validation outcomes");
        // Exercise the exact production SQL restore command on the owned
        // disposable LocalDB instance, never an installed school's database.
        Execute("ALTER DATABASE [FMSC_DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [FMSC_DB];");
        ReplacementSqlActivation.RestoreVerifiedBackup(c,good,
            PathFor("replacement-new.mdf"),PathFor("replacement-new.ldf"),
            verifiedVirginTemplate:false);
        Check(Convert.ToString(Scalar("SELECT Value FROM FMSC_DB.dbo.TestMarker;"))=="Original",
            "Replacement activation restored actual verified SQL backup into new school catalog.");
        bool protectedExisting=false;
        try
        {
            ReplacementSqlActivation.RestoreVerifiedBackup(c,good,
                PathFor("replacement-overwrite.mdf"),PathFor("replacement-overwrite.ldf"),
                verifiedVirginTemplate:false);
        }
        catch(InvalidOperationException){protectedExisting=true;}
        Check(protectedExisting,
            "Replacement SQL activation refused to overwrite an existing FMSC_DB database.");
        Check(Convert.ToString(Scalar("SELECT Value FROM FMSC_DB.dbo.TestMarker;"))=="Original",
            "A refused second activation did not alter the recovered school.");
        Check(Convert.ToString(Scalar("SELECT Value FROM CurrentSchool.dbo.TestMarker;"))=="Original",
            "Disposable replacement activation did not modify the preserved original school.");
        string syntheticSafety=PathFor("virgin-template-pre-recovery-safety.bak");
        Execute("UPDATE FMSC_DB.dbo.TestMarker SET Value=N'Blank';");
        Execute($"BACKUP DATABASE [FMSC_DB] TO DISK={Quote(syntheticSafety)} WITH COPY_ONLY,CHECKSUM,INIT;");
        Execute($"RESTORE VERIFYONLY FROM DISK={Quote(syntheticSafety)} WITH CHECKSUM;");
        Check(File.Exists(syntheticSafety)&&new FileInfo(syntheticSafety).Length>0,
            "A synthetic first-run template has a verified safety copy before replacement.");
        ReplacementSqlActivation.RestoreVerifiedBackup(c,good,
            PathFor("replacement-new.mdf"),PathFor("replacement-new.ldf"),
            verifiedVirginTemplate:true);
        Check(Convert.ToString(Scalar("SELECT Value FROM FMSC_DB.dbo.TestMarker;"))=="Original",
            "Verified virgin-template replacement completed and preserved the recovered SQL data.");
        Console.WriteLine($"PASS: {checks} real SQL backup-identity and replacement activation checks on the isolated instance.");
    }
}
