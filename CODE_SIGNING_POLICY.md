# Code signing policy

Official Windows releases must be Authenticode-signed by the signing identity configured in the protected GitHub `release` environment. Unsigned local candidates are test artifacts only and must include `UNSIGNED` in their filename.

## Release controls

- Release builds run from an immutable version tag on GitHub-hosted Windows.
- The complete clean Root/AVD and final installed-package E2E gates must pass on the exact tagged commit.
- Launcher, Setup, the final installer and its embedded uninstaller are signed, timestamped and verified against the same signer before they are accepted.
- The final installer signature must be valid and match the thumbprint configured in the protected `release-publish` environment.
- GitHub build provenance, file checksums and the SPDX SBOM must identify the same installer bytes.
- Automated signing creates only a Draft. Publication requires a separate protected manual approval.

## Roles

- Committer and reviewer: repository owner and explicitly authorized collaborators.
- Signing approver: repository owner through the protected `release` environment.
- Publication approver: repository owner through the distinct protected `release-publish` environment.

No private signing key, PFX file or certificate password may be committed to the repository or included in Release assets.
