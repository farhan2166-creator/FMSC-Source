# Communication Center — retired pending rebuild

The unused legacy Communication Center was removed from the desktop application in the schema-8 retirement branch. No messaging UI, dispatcher, external sender, automation service, webhook or contact editor is available in this intermediate version.

The former `comm` schema and `dbo.StudentGuardianCommunication` were dedicated to that unused module and are removed by `Migrations/8.sql` after empty-data checks. Existing school-management contact fields in `dbo.Students`, `dbo.Staff` and other core records are not removed. The migration refuses to proceed if old communication operational records or guardian consent records exist.

Historical migrations 2–7 stay unchanged. The source code, SQL procedures and delivery infrastructure of the old module must not be reused as a compatibility layer. Build the replacement independently with a new migration, consent design and tests.

**Release gate:** The retirement branch is not a production release. Complete the replacement, review the migration chain, and obtain Sandbox acceptance before publishing.
