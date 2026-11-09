# FMSC — Cloud backup rollout and recovery safety

Status: **implementation in progress, NOT enabled, DO NOT merge or publish**. The existing Backup / Restore page, SQL backup and restore manager, school database, release installer, and updater remain authoritative.

## Agreed operator experience

- Extend **System Maintenance → Backup / Restore**, never introduce a second backup page.
- Keep the existing single live SQL Server database and existing manual local-backup path.
- Add optional **Google Drive** and **Dropbox** destinations, independently connectable.
- Allow manual backup, scheduled daily backup (default 18:00 local), configurable frequency, time and independent maximum retention counts. A powered-off host should retry the missed schedule on its next eligible start.
- One backup artifact is created from a checksum-verified SQL .bak and encrypted locally BEFORE uploading to either provider.
- Provider failure must never prevent school work or delete the other provider's successful copy.
- Cloud recovery list must compare the actual SQL backup header creation/completion time, not cloud upload/modified time. Group copies of identical encrypted packages as one recovery point; label unverified remote metadata accordingly.
- For cloud restore, **download the entire package to a private local temporary directory**, decrypt to a local .bak, check actual SQL header timestamp, identity, integrity, and compatibility through the existing manager, create the mandatory safety backup, then restore. Never stream a live restore from cloud.
- **Only after successful verified SQL restore** remove the downloaded cloud package and staged .bak. A failed attempt retains diagnostics/recovery staging; its cleanup is a separate protected maintenance action, and pre-restore safety backups are never part of temp cleanup.

## Current foundation in this draft

- `CloudBackupCrypto`: chunked AES-256-GCM backup package. Independent school-selected recovery passphrase; PBKDF2-SHA256 310,000 iterations; strict authentication and truncation checks. No recovery secret is embedded in GitHub, the executable, or cloud file.
- `CloudRestoreStaging`: isolated staging on SQL host, protected Windows ACL, SQL backup-header comparison, reuse of the existing validated restore manager, post-success staging cleanup, and failure preservation.
- `CloudBackupSettings`: validated scheduled time/frequency and separate provider/local retention limits; cloud settings have no OAuth tokens. Local encrypted passphrase copy is protected by Windows DPAPI (CurrentUser).
- `CloudBackupCatalog`: chronological recovery-point catalog by SQL header creation time; deduplicate identical encrypted packages; refuse retention pruning without a confirmed verified upload.
- `Tests/CloudBackupCryptoChecks`: multi-chunk round-trip, wrong-key, truncated/corrupt file rejection, creation-time ordering, duplicate grouping, and retention safety cases. Windows desktop CI includes test.

## Work required before turning on cloud backup

1. Implement an app-registered **Google desktop OAuth 2.0 client** and **Dropbox scoped app**, user-authorized PKCE sign-in, refresh tokens protected with Windows DPAPI, revocation and disconnect. The developer must register app IDs with the providers; do not ship OAuth client secrets or arbitrary plaintext personal tokens.
2. Implement Drive resumable upload / list / retrieve / delete (scope `drive.file`) and Dropbox upload sessions / list / retrieve / delete (app folder and narrowly scoped permissions). Independently verify completed uploads and record provider-specific errors/quotas.
3. Wire the existing Backup and Restore screen with connection controls, passphrase enrollment/re-entry, frequency/time/retention, cloud recovery listing and explicit preview/confirmation. Keep existing local workflows unchanged.
4. Implement an unattended Windows task owned by the authorized database-host Windows identity. Existing `DatabaseBackupManager.CreateBackup` uses `CurrentUser` and cannot simply be invoked in an unauthenticated scheduled process; establish a separately authorized machine scheduling boundary without bypassing user permissions.
5. Make remote retention depend on confirmed recoverable remote objects, preserve at least one known-good copy, never prune updater pre-migration or pre-restore safety backups. Do not interpret cloud upload times as recovery chronology.
6. Test with **throwaway** Google/Dropbox accounts, simulated expired credentials, quota exhaustion, lost internet, large uploads, interrupted transfers, and replacement-Windows-PC passphrase recovery. Test SQL restoration against **disposable** databases and historical fixtures, not actual school data.
7. Obtain all required Windows/Sandbox CI acceptance for exact source SHA before moving this PR out of draft. A crypto-only CI PASS is not a production cloud-recovery sign-off.

## Password and data-loss requirements

Cloud account authentication does **not** by itself decrypt an FMSC backup. The school must remember or independently retain the recovery passphrase; the Windows DPAPI copy is only for scheduled uploads on the enrolled PC. Losing both the computer and the recovery passphrase makes encrypted cloud backups unrecoverable. Do not enable scheduling before recovery onboarding explicitly confirms this.

Existing .bak restore remains governed by the current backup ID / compatibility rules. SQL backup header data is checked before trusting displayed recovery time. A successful cloud restore always takes a verified pre-restore safety backup; it is never auto-triggered just because a newer recovery point exists.

## External API reference

- Google Drive files.create and resumable upload: https://developers.google.com/workspace/drive/api/guides/manage-uploads
- Dropbox OAuth PKCE / offline refresh: https://docs.dropboxapi.com/dropbox-api/docs/oauth
- Dropbox upload sessions: https://docs.dropboxapi.com/dropbox-api/api-reference/user-endpoints/files/upload-session-start

No live database credentials, student records, or backup files belong in GitHub workflows or test artifacts.
