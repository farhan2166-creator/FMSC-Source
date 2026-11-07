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
