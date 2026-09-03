# 0.1.0 local release candidate

- Added double-clickable Windows GUI installer and daily launcher.
- Added rooted Android 15 AVD start/stop and health diagnostics.
- Added local APK install/update with data-preserving adb install.
- Added APK drag-and-drop, installed package discovery, launch, force-stop and confirmed uninstall.
- Added Material Files launch and Root-backed private directory export.
- Added generic Root-backed export for any validated application package and private relative path.
- Added stable SwiftShader and high-performance Host GPU profiles.
- Added product-scoped AVD storage, resumable downloads, Root repair journal and persistent cold-boot health probes.
- Isolated the product AVD under a dedicated name; install and uninstall do not access existing global Android Studio AVDs.
- Added repair-with-rollback for incomplete or wrong-revision product SDK directories and recovery from complete/HTTP-416 partial downloads.
- Added post-configuration shortcuts and three-scope interactive uninstall.
- Pinned Android SDK archives with official SHA-1 and product-side SHA-256; removed sdkmanager latest downloads.
- Added direct SDK package registration and product-scoped AVD discovery without userdata recreation.
- Replaced Magisk file-picker automation with the official boot_patch.sh command-line workflow.
- Added a persistent Root journal containing stock and patched ramdisk hashes.
- Added final-installer E2E covering installed Setup.exe, an open-source APK, private-data export, GUI windows and program uninstall.
- Generate SPDX from the dependency manifest and actual Launcher, Setup and Installer SHA-256/SHA-1 digests, with package verification code and official SPDX Tools validation.
- Added fail-closed Draft creation on a virtualization-capable signed Windows runner, provenance attestation, and a separate protected human-approved publication workflow.
- Added Windows push/PR CI and automatic discovery of the installed Windows SDK x64 signing tool.
- Added verified runtime dependency downloads, Release content audit, SPDX SBOM and SHA-256 output.

The local candidate remains intentionally unpublished until a trusted Authenticode certificate is supplied and the signed self-hosted Windows Release workflow passes.
