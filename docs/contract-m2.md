# PACT Contract — M2 Public Release Hardening

## Goal

Eliminate public-release blockers while preserving a fail-closed, explicitly unsigned integrity gate.

## Acceptance

- A single dependency manifest records immutable HTTPS sources, revisions and upstream checksums.
- Runtime code, tests, Release audit and SBOM consume or verify the same manifest.
- The release script requires an explicit `AllowUnsignedPublicRelease` choice and labels every unsigned installer `UNSIGNED`.
- CleanE2E requires an absent or empty product root by default.
- Post-package E2E installs the final Inno artifact, executes the installed Setup binary, installs a pinned open-source APK and verifies generic private-data export.
- SPDX output is generated from actual publish inputs, includes SHA-256/SHA-1 digests and passes the pinned official SPDX Tools 2.3 semantic validator.
- Inno observes Setup success/failure and removes product-created shortcuts during uninstall.
- Normal install and uninstall paths never adopt, modify or delete a global legacy AVD.
- Broken product SDK components can be replaced from verified archives with rollback on failure.
- Tagged automation requires the installer to verify as `NotSigned`, rejects ambiguously named unsigned artifacts, and creates only a provenance-attested Draft.
- Publication requires a separate protected manual approval and revalidation of the exact asset allowlist, unsigned state, PE version, provenance, checksum and SBOM.

## Non-goals

- Purchasing or requesting a paid code-signing certificate, or presenting a self-signed certificate as public trust.
- Publishing GitHub assets before all public gates pass.
- Bundling Android SDK images, Magisk, third-party APKs or user data.

## Constraints

- End users interact only with Windows GUI executables.
- CI/test-only flags must be explicit and unavailable through normal GUI navigation.
- Existing global Android Studio AVDs and user-exported data remain untouched.
