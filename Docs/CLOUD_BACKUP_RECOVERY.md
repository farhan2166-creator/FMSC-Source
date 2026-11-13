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

## OAuth publisher configuration (required before any school can connect)

`CloudBackupProviders.json` contains the publisher's public application IDs. School
passwords, refresh tokens and account credentials never belong in that file or Git.

**Google Drive:** Enable Drive API and register an **installed/Desktop app OAuth
client**, using the limited `drive.file` scope. Keep its public client ID in
`GoogleClientId`. Download that Desktop client's JSON as
`CloudBackupGoogleDesktop.json` beside the project before publishing. It is ignored
by Git and copied to application output/publish. Local `PortableRelease` publishing
validates both the project input and actual published output; a configured Google
client cannot silently produce an installer without its matching registration.
Existing installations need this file beside the executable as well.

To include it in a private complete installer, store the JSON in the
`FMSC_GOOGLE_DESKTOP_JSON` Actions secret in **FMSC-Sandbox**. A Sandbox workflow
can call the Source installer workflow and pass the secret explicitly. Replace
`SOURCE_COMMIT_SHA` in both places with the same full commit SHA that includes
the matching public client ID and the installer workflow support:

```yaml
name: Package private installer with Google Drive
on: workflow_dispatch
permissions:
  contents: read
jobs:
  installer:
    uses: farhan2166-creator/FMSC-Source/.github/workflows/fmsc-complete-installer.yml@SOURCE_COMMIT_SHA
    with:
      source_sha: SOURCE_COMMIT_SHA
    secrets:
      FMSC_GOOGLE_DESKTOP_JSON: ${{ secrets.FMSC_GOOGLE_DESKTOP_JSON }}
```

Save the caller under `.github/workflows/` in FMSC-Sandbox and run it from
Sandbox's main branch. The downloaded private installer contains the registration
beside the application. The public Source installer and Workflow 04 candidate
artifact do not receive this secret.

**Private updates (Workflow 04):** also add `FMSC_GOOGLE_DESKTOP_JSON` to
**FMSC-Source → Settings → Secrets and variables → Actions**. Use the same downloaded
Desktop JSON matching the pinned public client ID. GitHub secrets are scoped to a
repository: the Sandbox secret is not automatically available to Source. Do not
paste the JSON into source, issues or chat.

Workflow 04 checks this configuration before bumping a version or dispatching
Sandbox acceptance. After the exact candidate, Source SHA and Sandbox acceptance
checks, the private publisher makes a separate working copy and adds the validated
registration there. The sealed public candidate stays unchanged. The private copy
gets a complete inventory, and both full and incremental packages include the
registration under their normal hash verification. Nothing containing it is
uploaded as a public Source artifact. Missing, web-type or mismatched metadata
stops private publication; there is no silent omission.

For local update packaging, include `CloudBackupGoogleDesktop.json` in the published
application, or prepare a private copy with the packager's
`--prepare-google-registration <application-directory>` command and the
`FMSC_GOOGLE_DESKTOP_JSON` environment variable. The normal package command refuses
configured Google updates without valid metadata. No installed refresh tokens or
school credentials are copied into update packages.

An old installed copy alone is insufficient: updates replace the application tree
with the manifest's exact contents. Publish a newer fixed update to restore a
missing registration. If immediate recovery is needed, place the matching Desktop
JSON beside the installed executable; the next correctly published update will
include it. Do not copy files into a downloaded, hash-verified updater package or
edit its manifest.

The parser accepts Google's `installed` envelope, rejects `web` credentials and
requires the ID to match `GoogleClientId`. Google Desktop token exchange and refresh
require the native `client_secret` metadata, alongside PKCE for sign-in. Native app
metadata is not a confidential server credential and cannot replace PKCE or user
consent. No web client secret or additional server is supported. Missing metadata
fails with a publisher-configuration diagnostic before opening browser sign-in.
Refresh tokens remain protected by Windows DPAPI.

**Dropbox:** Create a **Scoped App** restricted to the app's own folder.
Enable `files.metadata.read`, `files.content.read`, and
`files.content.write`. Under OAuth settings register the exact callback
`http://127.0.0.1:41847/fmsc-backup/`. Put its public app key in
`DropboxAppKey`. The app uses PKCE, offline refresh, and app-folder isolation.

Once both registrations and the Desktop metadata are supplied, the owner must validate browser login,
refresh, logout/revoke, large encrypted upload, provider listing, manual
download, daily scheduled operation and SQL restoration using disposable
test credentials/databases **before shipping**. Blank IDs fail closed and
do not allow a school to connect an unregistered service.

## Operational constraints requiring final Windows acceptance

- The scheduled task uses the SAME enrolled Windows account and SQL LocalDB
  identity. It can run with the FMSC window closed while that user is logged in;
  do not claim fully logged-out unattended operation until separately tested.
- The SQL backup runs only when the local database path, identity and schema
  are compatible; an active installer or pending update recovery prevents it.
- A configured backup time is the earliest eligible time. The periodic task
  polls approximately every 30 minutes, so completion may follow the selected
  time rather than occurring at the exact minute.
- The staging folder and pre-restore safety backup folder MUST be distinct.
  Cloud restore history uses a `cloud://` record rather than the deleted
  temporary .bak path. Temporary cleanup errors produce a warning but never
  invalidate a successfully verified restore.
- The existing regular Restore workflow intentionally rejects a foreign
  school identity. Recovery on an entirely new PC or fresh unrelated database
  requires a separately validated identity/bootstrap path; the current cloud
  restore integration must not bypass that protection.

This draft cannot be certified as production ready without real provider
registrations and Windows SQL restore tests, even if all source CI is green.

Google Drive uploads reserve and journal their remote file ID before creating a
file. Retries verify that exact remote file's identity, size and server checksum;
a lost completion response cannot cause a second file to be created. Older queued
packages are first reconciled against matching existing files. Size parsing accepts
Drive's string-encoded int64 metadata as well as Dropbox's numeric metadata.

## Delivery and recovery failure handling — 12 November 2026

The scheduled command returns **4** while selected cloud uploads remain pending,
including retries on a run where no new local backup is due. It returns 0 for a
healthy completed/no-due run; existing setup/recovery refusal and fatal-error
codes remain distinct. Local backup success still advances only the local
schedule timestamp, and failed cloud copies remain queued for retry.

A sanitized `last-run.json` in the per-user FMSC CloudBackups directory records
checked time and delivery status. Backup / Restore displays this status on entry
and Refresh History. This does not replace the SQL backup history or prove that
any untested cloud copy is recoverable.

Cloud recovery listing identifies accounts that could not be checked. Available
copies from another provider remain selectable with a warning; all-provider
failure is distinguished from an empty account. Download streaming has a
**two-minute inactivity deadline** per read/write. Timeout or cancellation
removes the partial package before SQL restore. Once SQL work begins, its existing
restore/rollback completion rules still apply.
