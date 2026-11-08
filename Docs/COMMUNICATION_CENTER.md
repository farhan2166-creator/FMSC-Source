# Communication Center — legacy retirement and preview-only replacement

The original `comm` module and `dbo.StudentGuardianCommunication` are retired by
`Migrations/8.sql`. The owner approved deletion of their **old data even when
nonempty**: contact lists, campaigns, messages, delivery attempts, historical
communication audit rows, settings, and old guardian preferences are **not
archived or migrated**. This is intentionally irreversible except by restoring
the verified pre-migration database backup.

Normal student/admission/staff/financial records are never discarded. An old
communication preference is not treated as consent under the new schema.

`Migrations/9.sql` creates a separate `msg` system with new explicit consent
on the student Admission record. Both SMS and WhatsApp are disabled at
installation. Authorized operators can open the dashboard Communication Center
to select recipients, compose and preview, inspect history, and access permitted
settings. **Confirm & Send remains disabled** until secured callbacks, a real
provider and end-to-end acceptance are complete. No automatic messaging.

Unknown legacy schema changes, external dependencies or unreviewed objects must
fail closed with rollback rather than delete unrelated data. Historical migrations
2–7 are immutable.

**Release gate:** PR #128 must not merge or publish before the user's restored
schema-7 test, SQL rollback checks and exact-commit release acceptance.
