# FMSC Administrator Recovery and Database Deployment

## Emergency Administrator login recovery (application 1.0.21 / schema 12)

On the normal login screen choose **Forgot Login Details?**. This applies
only to the protected school Administrator account; ordinary users still have
their credentials reset by an authorized administrator through Users & Roles.

Recovery requires **all** of the following:

- The FMSC SQL Server Express **LocalDB** database is hosted on this computer,
  attached as FMSC_DB at the approved local path, and matches the expected
  installed schema. A remotely connected PC or a different/local test database
  is refused.
- The current Windows identity is an **elevated** Windows Administrator. Start
  FMSC with *Run as administrator* under the **same Windows account** that owns
  the LocalDB instance and the existing update authorization.
- The computer's existing DPAPI-protected SSH deploy key can authenticate
  **online** to the private, immutable-ID-verified FMSC-Updates repository.
  A local key file alone, an offline network, or a revoked key is insufficient.
- The database has an existing recovery-host registration matching this exact
  Windows SID and the SHA-256 fingerprint of that update public key.

FMSC automatically creates the one-row registration on the LocalDB host after
a **normal successful Administrator sign-in**, if that Windows identity already
has a generated GitHub update key. No recovery key, local recovery script, or
password is saved. If the key has not yet been enrolled, use the existing
Check for Updates -> Computer access flow and sign in normally afterward.
The registration is **not** created or replaced by a recovery request.

After validation, the user confirms a new Administrator username and password.
The application updates the single existing protected Administrator account
within one serializable SQL transaction and records the action in
dbo.AdministratorRecoveryEvents. User ID, role assignments and all school
records stay unchanged. A failed operation rolls back the credential update.
A success requires exactly one protected Administrator account; ambiguity is
a refusal, not an arbitrary target selection.

**Limitations:** If the school has already forgotten its only Administrator
credentials before registration, recovery is intentionally unavailable; a
separately verified database-owner maintenance process is needed. A new or
replaced SSH key is not automatically re-trusted. If GitHub is offline or
the device authorization is revoked, automated recovery does not run. As with
any local SQL database, an actual SQL/Windows host administrator has ultimate
control over the data independently of FMSC: the UI security gates are not a
substitute for securing the Windows machine and MDF/LDF backups.

## Single-PC and shared-school database modes

**Current supported production topology: single-PC LocalDB.** FMSC pins the
database endpoint to the local Windows user's LocalDB instance and validates
the MDF file's location. Do **not** copy attached MDF/LDF files or place them
on a Windows file share to create multi-PC access.

**Future reviewed shared mode:** host one central SQL Server Express instance
(as a managed Windows service) on a designated school server PC and connect
each FMSC desktop client over an authenticated, encrypted LAN connection.
All clients operate on the same users, roles, students and fees. The
application's Users & Roles remain the school-side authorization layer, while
SQL credentials must have narrowly scoped privileges rather than blanket
owner rights. The installer, backups/restores, migration lease and update
compatibility checks must be made **server-aware** before enabling this mode.
The emergency Administrator recovery procedure must remain restricted to
the explicitly registered SQL server host, not any client PC or arbitrary
Windows Administrator.

A client-mode toggle is deliberately **not** enabled in this change; adding
only a connection textbox without the server permission and upgrade model
would create a serious credential-reset and data-integrity risk.
