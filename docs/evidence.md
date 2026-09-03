# Verification Evidence

Evidence is appended after each PACT cycle. No success claim is valid without the command and its fresh output.

## 2026-09-02 — Local unsigned candidate

Environment:

- Windows 11 x64 build 22631
- .NET SDK 10.0.400 / runtime 10.0.11
- Inno Setup 6.7.3
- Android 15 API 35, Google Play x86_64 revision 9
- Android Emulator 37.1.11, Platform Tools 37.0.1
- Magisk 30.6

Command:

    .\build\Build-Release.ps1 -CleanInstallRoot D:\program\Magisk\RootedAndroidGameVM\artifacts\release-e2e -ReuseE2EState -AllowUnsignedLocalCandidate

Fresh output summary:

- Non-integration tests: 44 passed, 0 failed.
- Explicit CleanE2E gate: 1 passed, 0 failed, duration 6m21s.
- CleanE2E checks: SDK revision, WHPX, product-scoped AVD_HOME, Root repair, Magisk extra setup, permanent Shell policy, User-Independent mode, changed Android boot ID, Magisk 30.6, uid=0, Root write/read probe and non-empty screenshot.
- WPF self-contained single-file publish: succeeded for Launcher and Setup.
- Inno compilation: succeeded.
- Release allowlist, forbidden-content scan, secret-pattern scan, SBOM and PE GUI subsystem audit: passed.

Artifact:

- RootedAndroidGameVM-Setup-0.1.0-x64-UNSIGNED.exe
- SHA-256: 1dd55ca8234bed4116aec7d1d0a255d415cca55768fade404068c5d82bccefb7
- Status: local candidate only; intentionally blocked from public Release because it is not Authenticode signed.

Final package smoke check was previously run against the immediately preceding candidate with both GUI windows returning non-zero handles. The latest candidate must be re-smoke-tested after trusted signing before public Release.

## Public Release blockers

- Trusted Authenticode code-signing certificate and timestamp are not available.
- Public gate still requires an empty CleanE2E root and a signed final Setup.exe E2E run.
- SDK packages are revision-pinned and verified, but product-side immutable archive SHA-256 values are not yet recorded for sdkmanager-managed components.
- SPDX is validated against a required package set but is not yet generated from the actual embedded self-contained publish payload.

## 2026-09-03 — M2 hardened local candidate

Command:

    .\build\Build-Release.ps1 -CleanInstallRoot D:\program\Magisk\RootedAndroidGameVM\artifacts\m2-e2e-seeded -ReuseE2EState -AllowUnsignedLocalCandidate -E2EDependencyCache D:\program\Magisk\RootedAndroidGameVM\artifacts\sdk-archive-audit

Fresh output summary:

- Non-integration tests: 59 passed, 0 failed.
- Core CleanE2E: 1 passed, 0 failed, duration 2m52s.
- Fixed-archive direct SDK installation and generated Android local package metadata passed.
- Post-package E2E passed against the final Inno artifact:
  - installed RootedAndroidGameVM.Setup.exe returned success;
  - pinned Material Files 1.7.4 APK installed;
  - a Root-owned private probe was created and exported through AndroidPrivateDataService;
  - Launcher and Setup GUI processes exposed non-zero Windows handles and expected titles;
  - program-only uninstaller removed product executables and its registry entry.
- SPDX SBOM was generated from the embedded dependency manifest and actual Launcher, Setup and Installer files.
- Release allowlist, forbidden-content, secret-pattern, size and PE GUI subsystem audits passed.

Pinned Android SDK archives:

- Platform Tools 37.0.1: upstream SHA-1 e03e78b1d80b396f1c3358e31251cb31740e1110; SHA-256 45f4d63113e895ebde0c90f194099a4676b6ac653bd28d54314a9e022bbc1a99.
- Emulator 37.1.11: upstream SHA-1 54fa750822ff462d57e04fc8e98e60f08df2bb61; SHA-256 5ff441f3b12ace9b13e9cf96fb0007d233967718652a8110705e995ac47bfeb7.
- Android 15 Google Play x86_64 revision 9: upstream SHA-1 2f0054868e6aab3c098acd3decba17a82aed4176; SHA-256 1fb5e5fd1c4b1b54bfe5558f6f361d6c10e13786acff630153e0542df356dfe6.

Artifact:

- RootedAndroidGameVM-Setup-0.1.0-x64-UNSIGNED.exe
- SHA-256: ec5b329fa7fbf2ebb7473024ab72f854dff5f821c800cb38910d25d2e3a09da7
- Authenticode: NotSigned.
- Dynamic SBOM: 12 packages and 3 actual artifact files; all recorded file SHA-256 values match.

Remaining public blocker:

- No valid trusted code-signing certificate with private key is installed in CurrentUser/My. The public-named Release path remains fail-closed; only an explicitly named UNSIGNED local candidate is produced.

## 2026-09-03 — M2 review fixes and final local gate

Command:

    .\build\Build-Release.ps1 -CleanInstallRoot D:\program\Magisk\RootedAndroidGameVM\artifacts\m2-e2e-seeded -ReuseE2EState -AllowUnsignedLocalCandidate -E2EDependencyCache D:\program\Magisk\RootedAndroidGameVM\artifacts\sdk-archive-audit

Fresh output summary:

- Non-integration tests: 73 passed, 0 failed.
- Core E2E: 1 passed, 0 failed, duration 1m39s.
- Final Inno installer E2E passed: installed Setup, Root health, pinned APK installation, private-data export, exact Launcher/Setup window titles, Inno desktop/configuration shortcuts, Setup-success daily Start Menu shortcut, and real program-only uninstall with product AVD retention.
- Official SPDX Tools Python 0.8.5 accepted the generated SPDX 2.3 document; it contains 14 packages, 3 actual files, SHA-256/SHA-1 per file and a package verification code. Android SDK and Inno license declarations use the honest SPDX `NOASSERTION` value rather than incomplete custom LicenseRef text.
- SPDX Tools and all transitive Python dependencies were provisioned through a Python 3.13 x64 hash lock; the signed build path permits offline reuse only.
- GitHub workflow static validation passed with actionlint 1.7.12. Signed automation creates a provenance-attested Draft; a distinct protected workflow verifies the expected signer, PE version, checksum, SBOM and attestation before human-approved publication.
- Release allowlist, forbidden-content, secret-pattern, size and PE GUI subsystem audits passed.
- Legacy AVD before/after metadata comparison: 25 files, 10,204,377,458 bytes, 0 differences. `D:\program\Magisk\arcaea_dl` remained present.

Artifact:

- `RootedAndroidGameVM-Setup-0.1.0-x64-UNSIGNED.exe`
- SHA-256: `3b84a9846cbf16b30bd2155e4d0c259f39709656dca4bf58ef850688e06e0364`
- Authenticode: `NotSigned`; local testing only.

Public Release remains fail-closed until the repository has a trusted Authenticode PFX in protected GitHub `release` environment secrets, the expected certificate thumbprint in the protected `release-publish` environment, and both the clean signed Draft workflow and separate human approval workflow pass.
