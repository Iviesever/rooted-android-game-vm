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

- Non-integration tests: 77 passed, 0 failed.
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

Public Release is fail-closed until all of the following succeed on the exact tagged commit:

1. A clean virtualization-capable Windows runner provisions every locked dependency.
2. The installer is visibly named `UNSIGNED`, verifies as `NotSigned`, and the Release notes disclose the Windows unknown-publisher warning.
3. The complete Core and final installed-package E2E gates pass against the exact tagged asset.
4. GitHub records build provenance and creates only a Draft.
5. A separate protected human-approved workflow rechecks the asset allowlist, unsigned status, PE version, checksum, SBOM and provenance before publication.

## 2026-09-03 — GitHub-hosted clean Release E2E

Authoritative run:

- Commit: `f14f6907dcb7d6af1d4e2e5813e601c9c6ebbf02`
- Workflow: `Unsigned Release E2E`
- Run: https://github.com/Iviesever/rooted-android-game-vm/actions/runs/33758138818
- Result: success, 21m53s.

Verified results:

- 108 non-integration tests passed.
- Clean product root Core E2E passed in 13m43s on GitHub-hosted `windows-2025`.
- Android 15 Play Store x86_64 AVD creation, product-scoped SDK/AVD environment, Magisk 30.6 patching, persistent Root policy, cold-restart Root health, Root read/write and screenshot checks passed.
- Self-contained Launcher and Setup Windows GUI executables published successfully.
- Inno Setup 6.7.3 compiled the final installer after the upstream Inno installer's SHA-256, publisher signature and fixed GitHub Release attestation verification.
- The final installed-package E2E installed the built installer, executed installed Setup, installed the pinned open-source APK, waited for Android Package Manager readiness, exported a Root-owned private-data probe, conditionally waited for and verified exact GUI titles and nonzero handles, and completed the real program-only uninstaller while retaining AVD data.
- Official SPDX validation and the exact Release allowlist/content/PE audits passed.
- Ephemeral unsigned installer SHA-256: `e1c97476cb8e1e6b04343e0eeaf122ae51935ccb1ef121144f4f7d09293c6996`.
- Isolated 12+ GB E2E data and product processes were removed successfully after the gate.

Repository protection state:

- `release` and `release-publish` GitHub environments both require review by the repository owner.
- The immutable `v0.1.0` tag remains at commit `175fc369cc9b7cd403021c3bd84ec3eceb028602`; its Release workflow run 33772087910 failed during the duplicated Inno registration precheck before E2E, asset upload or Draft creation.
- No GitHub Release exists for `v0.1.0`.
- The immutable `v0.1.1` tag remains at commit `0bf17a5f4d4b02dc9edf23da9279db9491572545`; its Release workflow run 33773675099 failed in clean Root E2E before asset upload or Draft creation.
- No GitHub Release exists for `v0.1.1`.
- No paid or publicly trusted Authenticode identity is configured or required for version 0.1.2.

The generic clean unsigned Release pipeline was first proven by the authoritative run above and then executed against the exact version 0.1.2 tag as recorded below.

## 2026-09-04 — Public Release v0.1.2

Published Release:

- Tag commit: `d4060e779c0c28a5af8596455f0b4e2fde600634`
- Clean build, E2E, provenance and Draft run: https://github.com/Iviesever/rooted-android-game-vm/actions/runs/33776563728
- Protected revalidation and publication run: https://github.com/Iviesever/rooted-android-game-vm/actions/runs/33781122216
- Release: https://github.com/Iviesever/rooted-android-game-vm/releases/tag/v0.1.2
- Installer: `RootedAndroidGameVM-Setup-0.1.2-x64-UNSIGNED.exe`
- Installer SHA-256: `3292979276bd4c22a01b0d4040f0ec891396bb86887c309f789050d9e0c89066`

Final public-download verification:

- The Release is public, is not a prerelease, and is the repository's latest Release.
- The public Release contains exactly five allowed assets, all served from the immutable `v0.1.2` download path.
- A fresh public download matched both the accompanying checksum file and GitHub's asset digest.
- Windows reported the installer as `NotSigned`; the filename and Release notes both make that status explicit and disclose the unknown-publisher warning.
- The installer PE reports file version `0.1.2.0` and Windows GUI subsystem 2.
- Official SPDX Tools Python 0.8.5 accepted the downloaded SBOM, whose installer entry matched the downloaded installer SHA-256.
- Strict GitHub attestation verification passed for the exact tag commit, the repository Release workflow, the tag ref and GitHub-hosted runners only.
- Main-branch CI passed after the protected publication workflow was corrected to bind all GitHub CLI Release commands explicitly to this repository.

## 2026-09-04 — Local 0.2.0 storage candidate

This is local acceptance evidence, not a public Release gate result. The prior published assets were not changed.

- Added selectable first-download storage, a launcher migration window, durable location/recovery records, and compatible program-only overlay updates.
- 169 non-integration tests passed, including real Windows process/TCP-owner queries, repeated WMI property reads, ordinary/headless process identity, path and disk backing boundaries, interrupted migration recovery, and fail-closed guest filesystem synchronization.
- A fresh isolated GUI setup downloaded the locked SDK, image and Root tools to the selected D-drive root. Root checks and cold restart completed successfully. The path text field was exercised; opening the native folder picker was checked, but automated selection inside that picker was not completed.
- Real migration copied and hashed 14,235,227,624 bytes, cold-started the relocated runtime, verified Root, switched location and removed the old test resources. A pre-existing dummy private-data probe survived another cold start, APK `install -r`, and exact export. The test did not recreate the probe during retention verification.
- The GUI installer updated the existing 0.1.2 installation to 0.2.0 at the same program path/AppId. The installed executable hashes matched the candidate, and all pre-existing resource file sizes and modification times were unchanged during the program-only update.
- Installer: `RootedAndroidGameVM-Setup-0.2.0-x64-UNSIGNED.exe`, 59,950,820 bytes; SHA-256 `95afd8420802b6c783c6a8f01ce99c7e65de57273c29b671a562c94eef579a87`. Five allowed assets, PE GUI subsystem, unsigned status and official SPDX 0.8.5 validation passed.
- Detailed local TRX, screenshots, migration receipts and package audit: `D:\program\Magisk\tasks\20260904-storage-relocation`. These local machine records are not part of the installer.

The public clean-runner, provenance and protected publication gates remain required for a public release. Windows physical keyboard input in Arcaea was not evaluated by this storage task.

Actual product acceptance completed on the same date:

- The installed launcher's migration window moved 1,115 original resource files (16,164,719,483 bytes) from the legacy C-drive root to the selected D-drive directory. Every source file was hashed, the fresh target runtime passed Root verification, and all original payload files were reclaimed. No pending migration or cleanup remained.
- The C-drive root retained only the location file, operation lock and approximately 289 KiB verification receipt. Observed available C-drive space increased from 13.99 GiB to 28.95 GiB; unrelated OS activity may affect this measurement.
- After closing and reopening the installed launcher, normal windowed startup used the D-drive SDK and explicit D-drive AVD datadir on port 5554. The launcher GUI reported SDK normal, ADB connected and Root uid=0. Original Arcaea package and private directory presence were confirmed without reading its contents or touching login state.
- All 68 protected personal AVD/export files retained the same paths, lengths and modification times. No real account credential was read or captured.
- The isolated test SDK/AVD was retained because automatic approval rejected its recursive cleanup with a policy-blocked result. It is separate from the installed product data; test export and migration evidence remains available locally.
