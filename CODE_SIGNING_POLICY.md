# Release integrity and code signing policy

Version 0.1.2 is intentionally not Authenticode-signed. Its installer filename must include `UNSIGNED`, and Windows can display an unknown publisher warning. A self-signed certificate is not used because it would not establish public publisher trust.

## Release controls

- Release builds run from an immutable version tag on GitHub-hosted Windows.
- The complete clean Root/AVD and final installed-package E2E gates must pass on the exact tagged commit.
- The installer must be named `RootedAndroidGameVM-Setup-<version>-x64-UNSIGNED.exe` and verify as `NotSigned`; no ambiguously named unsigned installer is accepted.
- The official Inno Setup compiler download is still Authenticode-verified against its upstream publisher before use.
- GitHub build provenance, file checksums and the SPDX SBOM must identify the same installer bytes.
- Tagged automation creates only a Draft. Publication requires a separate protected manual approval that re-downloads and revalidates every asset.
- Users should download only from this repository's GitHub Release page and verify the published SHA-256 or GitHub provenance before running the installer.

If a safe and publicly trusted signing identity becomes available in the future, optional Authenticode support requires Launcher, Setup, the final installer and its embedded uninstaller to use and verify the same signer. That optional path is not required for version 0.1.2.

## Roles

- Committer and reviewer: repository owner and explicitly authorized collaborators.
- Release-gate approver: repository owner through the protected `release` environment.
- Publication approver: repository owner through the distinct protected `release-publish` environment.

No private signing key, PFX file or certificate password may be committed to the repository or included in Release assets.
