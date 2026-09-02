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
