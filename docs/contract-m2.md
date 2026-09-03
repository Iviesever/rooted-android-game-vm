# PACT Contract — M2 Public Release Hardening

## Goal

Eliminate locally solvable public-release blockers while preserving a fail-closed gate for trusted Authenticode signing.

## Acceptance

- A single dependency manifest records immutable HTTPS sources, revisions and upstream checksums.
- Runtime code, tests, Release audit and SBOM consume or verify the same manifest.
- The release script distinguishes unsigned local candidates from signed public assets.
- CleanE2E requires an absent or empty product root by default.
- Post-package E2E installs the final Inno artifact, executes the installed Setup binary, installs a pinned open-source APK and verifies generic private-data export.
- SPDX output is generated from actual publish inputs, includes SHA-256/SHA-1 digests and passes the pinned official SPDX Tools 2.3 semantic validator.
- Inno observes Setup success/failure and removes product-created shortcuts during uninstall.
- Normal install and uninstall paths never adopt, modify or delete a global legacy AVD.
- Broken product SDK components can be replaced from verified archives with rollback on failure.
- A trusted certificate is required for a public-named artifact.
- Signed automation creates only a provenance-attested Draft; publication requires a separate protected manual approval and exact signer check.

## Non-goals

- Purchasing or requesting a code-signing certificate.
- Publishing GitHub assets before all public gates pass.
- Bundling Android SDK images, Magisk, third-party APKs or user data.

## Constraints

- End users interact only with Windows GUI executables.
- CI/test-only flags must be explicit and unavailable through normal GUI navigation.
- Existing global Android Studio AVDs and user-exported data remain untouched.
