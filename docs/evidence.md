# Verification Evidence

No success claim is valid without a fresh command result. Paths and application data used during local verification are intentionally omitted from this public record.

## 2026-09-03 — Hardened local candidate

Environment:

- Windows 11 x64
- .NET SDK 10.0.400 / runtime 10.0.11
- Inno Setup 6.7.3
- Android 15 API 35, Google Play x86_64 revision 9
- Android Emulator 37.1.11, Platform Tools 37.0.1
- Magisk 30.6

Release-gate result:

- Non-integration tests: 76 passed, 0 failed.
- Core E2E: 1 passed, 0 failed, duration 6m08s.
- The isolated AVD passed SDK revision checks, acceleration checks, automated Root preparation, persistent shell policy, cold-restart Root verification, a Root read/write probe and screenshot capture.
- Final installed-package E2E passed with the built Inno artifact: installed Setup execution, pinned open-source APK installation, generic private-data export, exact Launcher/Setup window titles, Inno shortcuts, post-Setup daily shortcut and real program-only uninstall with product AVD retention.
- Official SPDX Tools Python 0.8.5 accepted the generated SPDX 2.3 document. Each actual executable has SHA-256 and SHA-1 checksums and the root package has a package verification code.
- SPDX Tools and all transitive dependencies were provisioned through a Python 3.13 x64 hash lock; signed builds permit offline reuse only.
- Both GitHub workflows passed actionlint 1.7.12.
- Release allowlist, forbidden-content, secret-pattern, size and PE GUI-subsystem audits passed.
- Existing global Android Studio AVDs and user-exported application data were not modified.

The verified artifact from that cycle was an explicitly unsigned local candidate with SHA-256 `13e299aba72dd2b85a03c1d943593f9431f9bec8685837a3df5942f2b77d0c25`. It is not a publishable Release asset and is regenerated whenever product code changes.

## Public Release gate

Public Release remains fail-closed until all of the following succeed on the exact tagged commit:

1. A clean virtualization-capable Windows runner provisions every locked dependency.
2. A trusted Authenticode certificate signs Launcher, Setup and the final installer.
3. The complete Core and final installed-package E2E gates pass against the signed asset.
4. GitHub records build provenance and creates only a Draft.
5. A separate protected human-approved workflow rechecks the expected signer, PE version, checksum, SBOM and provenance before publication.
