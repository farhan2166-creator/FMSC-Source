# One-place application versioning

The **only application version to edit** for an approved new release is the
`$AppVersion` assignment in [`Installer/core.ps1`](../Installer/core.ps1).

For an application-only update from 1.0.0 to 1.0.1, change:

```powershell
$AppVersion = '1.0.1'
$DatabaseVersion = '1'
```

`FMSC_SMS.csproj` reads the application version directly from the installer
script when MSBuild evaluates the project. Its three release properties are
derived, not separately maintained:

- `Version` — `1.0.1` (private update package and About metadata).
- `AssemblyVersion` — `1.0.1.0` (.NET assembly identity).
- `FileVersion` — `1.0.1.0` (Windows file metadata).

The existing publisher compares the built DLL, the bundled installer and its
update manifest, then verifies the signed-off package before advancing the
private stable channel. Publish from `main` using the owner-initiated
**04 - Build and Publish Private Update** workflow. A previously published
version cannot be republished; increase `$AppVersion` before the next
approved update.

## Database versions are independent

Do **not** increase `$DatabaseVersion` for UI or application-only changes.
Database schema is bumped only when an approved numbered SQL migration is
added. Edit **only** `$DatabaseVersion` in `Installer/core.ps1`, then add
`Migrations/N.sql` (and consecutive intermediate scripts if necessary).
`Directory.Build.targets` automatically generates the C# constant
`DatabaseHelper.RequiredDatabaseSchemaVersion` from that same installer
assignment at compilation. Runtime compatibility and private Sandbox
acceptance both use this exact schema. Never manually edit a second C# value.
Workflow 04 automatically bumps `$AppVersion` on owner-triggered publication
and refuses incomplete migration chains.

**Published 1.0.0 is immutable.** This versioning convenience makes no release
or database change by itself. Test and approve every new version before
publishing it.

## Planned major/minor application-version transitions

Workflow 04 preserves one-click automatic patch increments on the currently
published release line. An intentionally prepared `$AppVersion` ahead on that
same patch line is reused without republishing an older version.

A **new minor or major release line** is never inferred from the source alone:

1. Change the single `$AppVersion` in `Installer/core.ps1` to the next minor
   version with patch zero (for example, `1.4.0` after `1.3.x`), or to the
   next major version with minor and patch zero (for example, `2.0.0` after `1.x`).
2. When deliberately running owner-only Workflow 04, enter that **exact same
   version** in the `approved_version_transition` dispatch input. Leave the
   input blank for ordinary patch releases.
3. A downgrade, missing/mismatched approval, nonzero patch on a new line, or
   skipped minor/major release line is refused before any Source version commit,
   private Sandbox dispatch or publication. Every release still requires the
   current Source SHA and fresh private SQL, installer and recovery acceptance.

This authorization does not approve a framework or database-schema migration;
those require their own review and evidence. Workflow 04 does not run merely
because these settings or files were changed.
