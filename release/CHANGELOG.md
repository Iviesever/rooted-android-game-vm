# 0.3.0

新增 GUI 与 JSON CLI 共用的安卓调试工作台、产品实例边界校验、认证 gRPC 多指输入和截图、文件传输、应用日志、Malody 可选流程和停机磁盘检查点。第三方 APK、皮肤、谱面和设备数据不随程序分发。

# 0.2.0

- Choose the resource location before the first download in graphical Setup.
- Move the runtime, download cache, AVD and Android application data from the launcher's storage window.
- Verify every copied file before switching paths; validate AVD and disk references, then verify a newly launched, process-bound emulator and Root.
- Recover interrupted migrations and retain changed or locked source files instead of deleting them.
- Upgrade program files in place using the existing installation identity and resource location, without recreating a compatible Android environment.
- Resolve startup, repair, performance settings, private data access and optional resource uninstall through the same saved location.
- Resolve Magisk's mount paths for Root commands, and verify both windowed and headless emulator processes using repeatable Windows process queries.
- Flush pending Android filesystem writes before stopping or migrating the emulator, preserving recently written application data across cold restarts.
- Keep the existing unsigned labeling and protected public Release gates. Local candidates do not imply public publication.

# 0.1.2

This Windows installer is intentionally not Authenticode-signed and its filename includes `UNSIGNED`. Windows may show an unknown publisher warning. Download only from this repository's GitHub Release page and verify the accompanying SHA-256 checksum or GitHub build provenance before running it.

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
- Added fail-closed Draft creation on a virtualization-capable GitHub-hosted Windows runner, provenance attestation, and a separate protected human-approved publication workflow.
- Added Windows push/PR CI and automatic discovery of the installed Windows SDK x64 signing tool.
- Proved the complete clean Release gate on GitHub-hosted Windows, including Root, cold restart, final installer, APK/private-data E2E and cleanup.
- Isolated Emulator SDK environment variables, normalized Android shell scripts to LF, handled slow first-boot System UI safely and waited for Package Manager readiness.
- Added verified runtime dependency downloads, Release content audit, SPDX SBOM and SHA-256 output.

The tagged Release is published only after the complete clean hosted gate creates a Draft and the separate protected publication workflow revalidates every asset.
