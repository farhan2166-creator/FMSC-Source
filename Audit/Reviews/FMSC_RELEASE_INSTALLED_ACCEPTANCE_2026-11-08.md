# Audit Issue 04 — Exact publication and installed-app acceptance (2026-11-08)

**Owner approval: YES.** Verify release and installed executable independently. Approval does **not** authorize a release, reinstall, migration, or any write to the live school database.

## Verified GitHub source and publication evidence

| Evidence | Finding |
| --- | --- |
| Current audited Source/main | `c49619e6e762cc47ca9e400a5d1b2577aeb4ef32` (after merged Audit Issues 01–03) |
| Current Source installer version/schema | `Installer/core.ps1`: **1.0.16 / schema 10** |
| Last successful manual Workflow 04 | [Run 42490053615](https://github.com/farhan2166-creator/FMSC-Source/actions/runs/42490053615), **success** |
| Exact published Source SHA | `4abe2d037f9ce51c0922875e7c0241cb112ac88d` |
| Published private Sandbox attestation | [Run 42490079315](https://github.com/farhan2166-creator/FMSC-Sandbox/actions/runs/42490079315), **success**, exact Source SHA above; source status `fmsc/sandbox-mdf-ldf/v2` |
| Private update channel | `FMSC-Updates/channel/stable.json`: **latest 1.0.16 / previous 1.0.15** |
| Published update manifest | `FMSC-Updates/versions/1.0.16/update-manifest.json`: **schema 10**, **734 files**, full archive **198073194 bytes**, SHA-256 `d19a23dce0fad7cf4ed3caa67aa9658e9e9309c4b661e7e1a4e5b4a22d3c12ed` |
| Published `FMSC_SMS.exe` | SHA-256 **`4c5b5201851b202e2b7ff33c26cabc20bcd00b9c4486be5e99df22de819c8990`**, **306688 bytes** |
| Workflow 04 artifact | `FMSC_SMS-Windows-x64-app-only-38`, artifact ID `13621601721`, SHA-256 `e8a770872d6d7c3c26d2fd30c8bdb6c8d9eba2f51154cabef2f06e5bd0c24c85`; retention expires **2026-11-22** |

**Important:** The successful published **1.0.16** was built **before** Audit Issues 01–03 merged. The published version number therefore **does not prove those fixes are installed**, and there is **no verified publication of current `c49619e6e762cc47ca9e400a5d1b2577aeb4ef32`**. Do not treat the absence of a new GitHub *Release* object as proof that the **private Updates repository** has no release; Workflow 04 publishes privately there. The owner must use the normal exact-commit Workflow 04 mechanism for a later update, rather than blindly reusing this 1.0.16 attestation.

## Owner-side read-only verification

Open PowerShell **on the actual school Windows account** with FMSC running, inside a local checkout containing this helper:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Tools\VerifyInstalledFmscRelease.ps1
```

If FMSC is closed, pass its exact known executable location instead of guessing:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Tools\VerifyInstalledFmscRelease.ps1 -ExecutablePath 'C:\path\to\installed\FMSC_SMS.exe'
```

The helper reads executable hash, file version and optional published BUILD_INFO integrity; queries **only the version** from the current user's `(localdb)\MSSQLLocalDB / FMSC_DB`; and, **only for the exact known published executable** and schema 10, invokes its production read-only `--verify-installation` SQL structural check. It sends no network messages, changes no settings, installs nothing, and records no student/guardian/finance data. Output is displayed locally only. A hash mismatch is **not** proof of malicious corruption or an older version: it means the executable's provenance is **unverified against this specific 1.0.16 publication**.

A successful automated check verifies the **published 1.0.16 binary**, its schema version and the **published build's** structural checker. It does **not** verify the newer Issue 01 schema-10 manifest because that code was merged afterward. Run a later exact-head release acceptance for those source changes.

## Required native UI evidence (manual, not available from GitHub)

1. Confirm the exact **running process** path matches the probed executable; log its local hash and ProductVersion privately. A disconnected project build or a migrated schema alone is not proof.
2. Open the actual installed dashboard's **Communication Center** with `Communication.View`. Confirm **WhatsApp Business (manual)** is available and **SMS (unavailable)** cannot send; **History**, **Settings**, **Preview**, and **Open WhatsApp Chats** appear in the appropriate workflow.
3. As an administrator with `Communication.Configure`, check **Use Free WhatsApp**, **Pause WhatsApp/Resume Free WhatsApp**, and disabled Email/direct providers. Do not toggle settings on the real school system solely for an audit.
4. For full functional acceptance, use a **disposable SQL copy and synthetic guardian destinations**; verify no automatic send, opt-out/changed-consent refusal, one chat at a time, a resumable handoff and **operator-reported** status distinct from Delivered. Follow [manual WhatsApp acceptance](../../Docs/COMMUNICATION_MANUAL_WHATSAPP_ACCEPTANCE.md).
5. Record the Windows-account local result and operator approval; do not publish a claim of acceptance without the user's actual results.

## Acceptance status as of this audit

- **PASS — Published 1.0.16 source/Workflow 04/private feed provenance and exact published executable reference hash.**
- **PASS — Static source inspection: newer Source HEAD differs from the published Source SHA.**
- **PENDING — Actual PC executable hash, Windows running process, live LocalDB version/runtime compatibility.** User previously reported successful schema-10 migration; it has **not** been freshly queried in this verification.
- **PENDING — Actual installed Communication Center UI and synthetic end-to-end operator acceptance.**
- **NOT ACCEPTED — Current Source/main as a published or installed release.** That requires fresh exact-head approval, package publication, hash comparison and local acceptance.

No school database, credentials, live messages, or actual Windows installation was accessed or changed during GitHub source audit.
