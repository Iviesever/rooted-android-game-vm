# 0.5.1

通用 Root 安卓工作台与双向文件管理。默认界面和接口不再包含特定游戏的专属流程，既有应用与用户数据保持独立于程序升级。

- 按真实应用名称、图标和安卓用户选择应用；同名应用显示包名消歧，文件管理无需手写包名。
- 浏览私有、设备保护、外部、OBB、媒体及共享目录；支持文件和文件夹双向复制、多选、拖放与多个应用数据根导出。
- GUI 与 AI/CLI 共用版本引用、只读计划、传输任务、逐项哈希和权限记录；冲突可跳过、保留双方或备份后覆盖。
- 取消和设备离线保留明确状态；用户可继续原任务，重新核对已完成内容、暂存与本实例的工具清理。
- 修复多批上传规划时的集合修改异常、不可访问根残留旧上传目标、缺失应用根上传、目录 Enter 导航和续作期间历史状态不同步。
- 传输预览和进度准确显示小文件字节数，历史使用本地时间；结果支持分页、失败原因、暂存及备份记录。
- 保留现有镜像、图形和运行配置；启动物理余量门槛支持 2560 MiB，运行期保护保持。

This installer is intentionally unsigned and named `UNSIGNED`. Windows may show an unknown publisher warning. Verify the SHA-256 checksum and repository build provenance before running it. No third-party APK, system image or user data is bundled.

# 0.5.0

本版改进导入、调试任务、输入归属和会话证据；实际验收与限制见docs/review-remaining-progress.md。

- 导入按传输、App/Activity、触发、解包、内容核验和待启用分阶段记录，支持原importId续作，不重复上传；并发解包枚举要求连续一致的清单。
- 新建与替换文件按private/external/shared作用域处理权限，返回实际权限；签名、脚本和资源差异严格失败，仅有限已知元数据重写单列。
- JSON请求/任务ID、阶段、终态、工具双流、产物和恢复索引统一；重启后中断任务不自动重放，大结果保留文件引用。
- GUI与CLI共用会话摘要；页面观察附原始截图和时间，过期后不当作当前事实。
- 输入发送与清理固定原VM会话，增加可选QPC绝对调度与独立测量对时信息；未确认释放不得报成功。
- 实际帧采样区分覆盖不足和跨实例变化。启动物理余量支持显式2560MiB，运行期保护独立保持。
- 延续明确标注UNSIGNED的发布政策；不捆绑第三方APK、游戏素材、用户数据或系统镜像。

# 0.4.0

统一工作台、结构化普通操作与 AI 入口；内存有界的预览和日志；可校验的硬件/显示/资源配置和实际呈现采样。当前处于本机开发验收阶段。

- 推荐安卓内存改为 3 GiB；按可用物理内存与提交空间分别检查启动余量。
- 增加启动和运行阶段的持续低内存保护，只保存并停止经过验证的产品虚拟机。
- 移除日常退出时的自动 Quick Boot 保存，减少磁盘写入；停机检查点继续保留。
- 增加目录导出、实际应用帧采样、可查询的命令参数和新版操作说明。

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
