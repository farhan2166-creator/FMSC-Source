using Microsoft.Data.SqlClient;

namespace FMSC_SMS.Core;

/// <summary>Shared startup/restore contract for the current application schema.</summary>
// BD-29/30/31: keep version, structure and recovery guards; a DACPAC is not a replacement for school data.
// Baseline labels never bypass the complete structural compatibility contract.
internal static class DatabaseCompatibility
{
    private static readonly Lazy<string[]> Requirements = new(() => new[]
    {
        LoadRequirements("FMSC_SMS.Core.DatabaseCompatibilityRequirements.json"),
        LoadRequirements("FMSC_SMS.Core.DatabaseCompatibilitySchema10Requirements.json"),
        LoadRequirements("FMSC_SMS.Core.DatabaseCompatibilitySchema12Requirements.json")
    });

    private static string LoadRequirements(string resourceName)
    {
        using var stream = typeof(DatabaseCompatibility).Assembly.GetManifestResourceStream(
            resourceName)
            ?? throw new DatabaseCompatibilityException(new(DatabaseCompatibilityIssue.ApplicationDefinition,
                DatabaseHelper.RequiredDatabaseSchemaVersion, Details: "The application's database compatibility definition is missing."));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Preserve the original one-argument method group used by restore verification.
    internal static void Validate(SqlConnection connection) => Validate(connection, null);

    internal static void Validate(SqlConnection connection, SqlTransaction? inspectionTransaction)
    {
        using (var capability = new SqlCommand("SELECT compatibility_level FROM sys.databases WHERE database_id=DB_ID();", connection, inspectionTransaction))
        {
            int level = Convert.ToInt32(capability.ExecuteScalar());
            if (level < 130)
                throw new DatabaseCompatibilityException(new(DatabaseCompatibilityIssue.SqlCompatibility,
                    DatabaseHelper.RequiredDatabaseSchemaVersion, SqlCompatibilityLevel: level));
        }
        using var versionCommand = new SqlCommand("""
            IF OBJECT_ID(N'dbo.DatabaseVersion',N'U') IS NULL
                SELECT CAST(NULL AS nvarchar(50));
            ELSE
                SELECT TOP(1) CONVERT(nvarchar(50),VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC;
            """, connection, inspectionTransaction) { CommandTimeout = 30 };
        string? version;
        try { version = Convert.ToString(versionCommand.ExecuteScalar())?.Trim(); }
        catch (SqlException ex) when (ex.Number is 207 or 208)
        {
            throw new DatabaseCompatibilityException(new(DatabaseCompatibilityIssue.SchemaVersion,
                DatabaseHelper.RequiredDatabaseSchemaVersion, Details: "The database version table or its required columns are missing. " + ex.Message));
        }
        // Report older/newer versions before querying the current release's structural manifest.
        if (!string.Equals(version, DatabaseHelper.RequiredDatabaseSchemaVersion, StringComparison.OrdinalIgnoreCase))
            throw new DatabaseCompatibilityException(new(DatabaseCompatibilityIssue.SchemaVersion,
                DatabaseHelper.RequiredDatabaseSchemaVersion, version,
                "The latest dbo.DatabaseVersion entry does not match the required schema version."));
        using var command = new SqlCommand("""
            SET NOCOUNT ON;
            DECLARE @Problems TABLE (Problem nvarchar(400));
            INSERT @Problems
            SELECT CONCAT('Missing ', R.[type], ': ', R.[schema], '.', R.name)
            FROM OPENJSON(@Requirements,'$.objects') WITH
                ([schema] nvarchar(128),name nvarchar(128),[type] varchar(2)) R
            WHERE OBJECT_ID(QUOTENAME(R.[schema])+'.'+QUOTENAME(R.name),R.[type]) IS NULL;
            INSERT @Problems
            SELECT CONCAT('Missing/incompatible trigger safeguard: ',R.[schema],'.',R.name)
            FROM OPENJSON(@Requirements,'$.triggers') WITH
                ([schema] nvarchar(128),name nvarchar(128),parentSchema nvarchar(128),parentName nvarchar(128),
                 events nvarchar(50),insteadOf bit) R
            WHERE NOT EXISTS
                (SELECT 1 FROM sys.triggers T
                 WHERE T.object_id=OBJECT_ID(QUOTENAME(R.[schema])+'.'+QUOTENAME(R.name),'TR')
                   AND T.parent_id=OBJECT_ID(QUOTENAME(R.parentSchema)+'.'+QUOTENAME(R.parentName))
                   AND T.is_disabled=0 AND T.is_instead_of_trigger=R.insteadOf
                   AND (SELECT STRING_AGG(CONVERT(nvarchar(max),E.type_desc),',') WITHIN GROUP(ORDER BY E.type_desc)
                        FROM sys.trigger_events E WHERE E.object_id=T.object_id)=R.events);
            INSERT @Problems
            SELECT CONCAT('Missing/incompatible check constraint: ',R.[schema],'.',R.objectName,' ',
                CASE WHEN R.isSystemNamed=1 THEN R.definition ELSE R.name END)
            FROM OPENJSON(@Requirements,'$.checkConstraints') WITH
                ([schema] nvarchar(128),objectName nvarchar(128),name nvarchar(128),isSystemNamed bit,definition nvarchar(max)) R
            WHERE NOT EXISTS
                (SELECT 1 FROM sys.check_constraints C
                 WHERE C.parent_object_id=OBJECT_ID(QUOTENAME(R.[schema])+'.'+QUOTENAME(R.objectName))
                   AND C.is_disabled=0 AND C.is_not_trusted=0
                   AND ((ISNULL(R.isSystemNamed,0)=0 AND C.name=R.name)
                     OR (R.isSystemNamed=1 AND C.is_system_named=1
                       AND (C.definition COLLATE Latin1_General_100_BIN2=R.definition COLLATE Latin1_General_100_BIN2
                         OR C.definition COLLATE Latin1_General_100_BIN2=CONCAT('(',R.definition,')') COLLATE Latin1_General_100_BIN2))));
            INSERT @Problems
            SELECT CONCAT('Missing/incompatible foreign key: ',R.[schema],'.',R.objectName,' (',R.columns,')')
            FROM OPENJSON(@Requirements,'$.foreignKeys') WITH
                ([schema] nvarchar(128),objectName nvarchar(128),referencedSchema nvarchar(128),referencedObject nvarchar(128),
                 columns nvarchar(max),referencedColumns nvarchar(max),deleteAction int,updateAction int) R
            WHERE NOT EXISTS
                (SELECT 1 FROM sys.foreign_keys F
                 WHERE F.parent_object_id=OBJECT_ID(QUOTENAME(R.[schema])+'.'+QUOTENAME(R.objectName))
                   AND F.referenced_object_id=OBJECT_ID(QUOTENAME(R.referencedSchema)+'.'+QUOTENAME(R.referencedObject))
                   AND F.is_disabled=0 AND F.is_not_trusted=0
                   AND F.delete_referential_action=R.deleteAction AND F.update_referential_action=R.updateAction
                   AND (SELECT STRING_AGG(CONVERT(nvarchar(max),C.name),',') WITHIN GROUP(ORDER BY K.constraint_column_id)
                        FROM sys.foreign_key_columns K JOIN sys.columns C
                          ON C.object_id=K.parent_object_id AND C.column_id=K.parent_column_id
                        WHERE K.constraint_object_id=F.object_id)=R.columns
                   AND (SELECT STRING_AGG(CONVERT(nvarchar(max),C.name),',') WITHIN GROUP(ORDER BY K.constraint_column_id)
                        FROM sys.foreign_key_columns K JOIN sys.columns C
                          ON C.object_id=K.referenced_object_id AND C.column_id=K.referenced_column_id
                        WHERE K.constraint_object_id=F.object_id)=R.referencedColumns);
            INSERT @Problems
            SELECT CONCAT('Missing/incompatible column: ',R.[schema],'.',R.objectName,'.',R.name)
            FROM OPENJSON(@Requirements,'$.columns') WITH
                ([schema] nvarchar(128),objectName nvarchar(128),name nvarchar(128),
                 [type] nvarchar(128),maxLength int,[precision] int,scale int,isNullable bit,isIdentity bit,isComputed bit) R
            LEFT JOIN sys.columns C ON C.object_id=OBJECT_ID(QUOTENAME(R.[schema])+'.'+QUOTENAME(R.objectName)) AND C.name=R.name
            WHERE C.column_id IS NULL OR TYPE_NAME(C.user_type_id)<>R.[type]
                OR C.max_length<>R.maxLength OR C.precision<>R.[precision] OR C.scale<>R.scale
                OR C.is_nullable<>R.isNullable OR C.is_identity<>R.isIdentity OR C.is_computed<>R.isComputed;
            INSERT @Problems
            SELECT CONCAT('Missing/incompatible database recovery identity: ',R.name)
            FROM OPENJSON(@Requirements,'$.databaseProperties') WITH(name nvarchar(128),[type] nvarchar(128)) R
            WHERE NOT EXISTS(SELECT 1 FROM sys.extended_properties E WHERE E.class=0 AND E.major_id=0 AND E.minor_id=0
                AND E.name=R.name AND SQL_VARIANT_PROPERTY(E.value,'BaseType')=R.[type]
                AND TRY_CONVERT(uniqueidentifier,E.value) IS NOT NULL
                AND TRY_CONVERT(uniqueidentifier,E.value)<>'00000000-0000-0000-0000-000000000000');
            INSERT @Problems
            SELECT CONCAT('Missing/incompatible parameter: ',R.[schema],'.',R.objectName,' ',R.name)
            FROM OPENJSON(@Requirements,'$.parameters') WITH
                ([schema] nvarchar(128),objectName nvarchar(128),name nvarchar(128),
                 [type] nvarchar(128),maxLength int,[precision] int,scale int,isOutput bit) R
            LEFT JOIN sys.parameters P ON P.object_id=OBJECT_ID(QUOTENAME(R.[schema])+'.'+QUOTENAME(R.objectName)) AND P.name=R.name
            WHERE P.parameter_id IS NULL OR TYPE_NAME(P.user_type_id)<>R.[type]
                OR P.max_length<>R.maxLength OR P.precision<>R.[precision] OR P.scale<>R.scale OR P.is_output<>R.isOutput;
            INSERT @Problems
            SELECT CONCAT('Missing/incompatible table-valued parameter column: ',R.[schema],'.',R.objectName,'.',R.name)
            FROM OPENJSON(@Requirements,'$.tableTypes') WITH
                ([schema] nvarchar(128),objectName nvarchar(128),name nvarchar(128),
                 [type] nvarchar(128),maxLength int,[precision] int,scale int,isNullable bit,ordinal int) R
            LEFT JOIN sys.table_types T ON T.schema_id=SCHEMA_ID(R.[schema]) AND T.name=R.objectName
            LEFT JOIN sys.columns C ON C.object_id=T.type_table_object_id AND C.name=R.name
            WHERE C.column_id IS NULL OR TYPE_NAME(C.user_type_id)<>R.[type]
                OR C.max_length<>R.maxLength OR C.precision<>R.[precision] OR C.scale<>R.scale OR C.is_nullable<>R.isNullable OR C.column_id<>R.ordinal;
            INSERT @Problems
            SELECT CONCAT('Missing/incompatible uniqueness safeguard: ',R.[schema],'.',R.objectName,' ',R.name)
            FROM OPENJSON(@Requirements,'$.uniqueIndexes') WITH
                ([schema] nvarchar(128),objectName nvarchar(128),name nvarchar(128),[filter] nvarchar(max),keys nvarchar(max)) R
            WHERE NOT EXISTS
                (SELECT 1 FROM sys.indexes I
                 WHERE I.object_id=OBJECT_ID(QUOTENAME(R.[schema])+'.'+QUOTENAME(R.objectName))
                   AND I.is_unique=1 AND I.is_disabled=0 AND I.is_hypothetical=0
                   AND ISNULL(I.filter_definition,'')=ISNULL(R.[filter],'')
                   AND (SELECT STRING_AGG(CONVERT(nvarchar(max),C.name),',') WITHIN GROUP(ORDER BY IC.key_ordinal)
                        FROM sys.index_columns IC JOIN sys.columns C ON C.object_id=IC.object_id AND C.column_id=IC.column_id
                        WHERE IC.object_id=I.object_id AND IC.index_id=I.index_id AND IC.key_ordinal>0)=R.keys);
            IF NOT EXISTS
                (SELECT 1 FROM sys.indexes I JOIN sys.index_columns IC ON IC.object_id=I.object_id AND IC.index_id=I.index_id
                 JOIN sys.columns C ON C.object_id=IC.object_id AND C.column_id=IC.column_id
                 WHERE I.object_id=OBJECT_ID('dbo.Students') AND I.is_unique=1 AND I.is_disabled=0
                   AND I.has_filter=1 AND I.filter_definition='([AdmissionNumber] IS NOT NULL)' AND IC.key_ordinal=1 AND C.name='AdmissionNumber'
                   AND NOT EXISTS(SELECT 1 FROM sys.index_columns K WHERE K.object_id=I.object_id AND K.index_id=I.index_id AND K.key_ordinal>1))
                INSERT @Problems VALUES(N'Missing unique admission-number constraint/index.');
            SELECT COUNT(*) AS ProblemCount FROM @Problems;
            SELECT TOP(12) Problem FROM @Problems ORDER BY Problem;
            """, connection, inspectionTransaction) { CommandTimeout = 30 };
        // Preserve the baseline, schema-10 messaging and schema-11/12
        // certificate signature / recovery contracts. This is read-only; no migration or repair is attempted.
        command.Parameters.Add("@Requirements", SqlDbType.NVarChar, -1);
        foreach (string contract in Requirements.Value)
        {
            command.Parameters["@Requirements"].Value = contract;
            using var reader = command.ExecuteReader();
            reader.Read();
            int count = reader.GetInt32(0);
            reader.NextResult();
            var problems = new List<string>();
            while (reader.Read()) problems.Add(reader.GetString(0));
            if (count > 0)
                throw new DatabaseCompatibilityException(new(DatabaseCompatibilityIssue.SchemaStructure,
                    DatabaseHelper.RequiredDatabaseSchemaVersion, version,
                    string.Join("\n", problems) + (count > problems.Count ? $"\n({count - problems.Count} more)" : "")));
        }
    }
}
