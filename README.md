# Rooted Android Game VM

一个面向 Windows 11 x64 的通用 Root 安卓虚拟机与调试工作台。安装、日常启动、APK 更新、Root 诊断和私有数据导出都通过 .exe 窗口完成，普通用户无需输入终端命令。核心不包含特定游戏的专属流程。
项目不针对、不捆绑任何单一应用或游戏。GUI 按真实应用名称选择，AI 使用应用发现、版本化引用和同一套文件服务。

> 安装包按项目政策明确标注 `UNSIGNED`，Windows 可能显示 unknown publisher（未知发布者）警告。公开版本请从本仓库 GitHub Release 下载，并核对 SHA-256 或 GitHub provenance；本地候选包不等同于已公开发布的版本。

## 0.5.1 安卓工作台

统一入口按“使用安卓、应用管理、文件管理、诊断与记录、AI 与自动化、检查点、运行设置”组织。普通操作使用按钮和列表，GUI 与 JSON CLI 共用同一服务；安卓仍在独立的模拟器窗口里运行。

文件管理先选择安卓用户，再按应用名称搜索和选择，包名用于同名消歧，无需手写。左侧选择私有数据、设备保护数据、外部文件、OBB、媒体或共享存储，双击进入文件夹；可上传电脑文件或文件夹、下载多选项、导出当前目录或勾选多个应用数据根。未生成但允许创建的应用外部目录显示“上传时创建”，浏览不会提前写入。详见[双向文件操作](docs/file-workspace.md)。

默认请求硬件渲染、1920×1080、120 Hz、3 GiB 安卓内存和四个核心。120 Hz 是显示模式，应用帧率需要另测。启动前分别检查可用物理内存和提交空间，运行中监测严重内存压力；不把安卓内存配额当成模拟器总占用。

- [日常操作说明](docs/debug-workbench.md)
- [CLI 请求与输入示例](docs/cli.md)
- [AI 调试流程](docs/ai-workflow.md)
- [性能与本机验证记录](docs/validation-v0.4.0.md)

0.5.1 提供通用应用目录、可续作的双向文件传输、按作用域处理的文件权限、可恢复的请求与任务身份，以及 GUI/CLI 共用的会话摘要。文件页支持目录树、键盘导航、多选、拖放、冲突备份、应用多根导出和逐项结果；活动历史随任务状态更新。页面和文件核验保留时间与原始依据，不把进程出现当成可操作页面。实测数据、测量边界及限制见[剩余review验收记录](docs/review-remaining-progress.md)。

公开资产继续显式命名为UNSIGNED，并经过原有发布门禁；是否已公开以GitHub Release实际状态为准，本地候选不等同于公开版本。

## 日常使用

首次运行安装包时，可以在图形配置窗口选择“资源存储位置”，例如其他磁盘上的独立空文件夹。SDK、系统镜像、Root 工具、下载缓存、模拟器和安卓应用数据都会放在该目录；程序安装目录单独选择。

1. 双击桌面的 **Rooted Android Game VM**。
2. 点击“打开安卓”，等待启动并打开安卓窗口。
3. 在“应用管理”选择本机 APK，检查后安装或更新。
4. 在“文件管理”选择应用和目录，浏览、上传或导出到 Windows。
5. 在“应用管理”页拖入单个 APK 可检查后安装；在“文件管理”页拖放按普通文件上传。应用页也支持启动、停止、确认卸载和进入所选应用的文件工作台。

## 迁移资源和覆盖更新

打开“运行设置 → 资源位置与迁移”，查看当前位置与大小，选择空目标文件夹并点击“开始迁移”。迁移会关闭产品模拟器，复制并逐文件校验资源，调整 AVD/虚拟磁盘路径，再从新位置验证启动和 Root。成功后自动切换位置并清理旧副本，之后直接使用原桌面快捷方式。

复制或启动验证失败时保留原资源；界面提供恢复按钮。复制过程中可以取消，切换成功后的原副本清理会完成当前事务。占用或发生变化的旧文件会保留并提示重试清理。迁移期间不要手动移动或删除资源文件夹。

运行新版本安装包即可覆盖更新，安装标识和程序路径保持不变。已完成且与当前固定组件兼容的运行环境会被复用，不重新下载或重建安卓应用数据；需要更新组件时沿用原资源位置进行配置。迁移后的目录同样用于更新、修复、性能设置及数据访问。

卸载默认可选择仅删除程序、保留资源。选择删除运行环境或全部资源时，卸载器会读取当前资源位置，检查目录归属并关闭对应产品模拟器；不会处理个人的全局 Android Studio AVD。

## 安全与兼容边界

- Release 不包含第三方 APK、账号或应用数据、音视频、Google 系统镜像、Magisk APK 或 AVD 用户磁盘。
- 安装器只在用户接受 Android SDK 许可后，从固定 HTTPS 地址下载组件并校验 SHA-256。
- 产品使用独立的资源目录及 `rooted_android_game_vm_api35`，不会接管或卸载用户已有的全局 Android Studio AVD。未选择新位置的旧安装仍使用 `%LOCALAPPDATA%\RootedAndroidGameVM`；迁移后这里只保留很小的位置配置、操作锁及最近一次校验记录。
- Platform Tools、Emulator 和 Android System Image 使用固定 archive URL、官方 SHA-1与产品侧 SHA-256，不跟随 sdkmanager latest。
- Root 允许访问应用私有目录，请只处理你有权访问的数据。
- Play Integrity、反模拟器、反 Root、专有 Vulkan 或特殊硬件依赖可能使个别游戏无法运行；项目不承诺兼容所有 APK。

## 开发与发布门禁

    .\build\Build-Release.ps1 -CleanInstallRoot D:\rgvm-clean-e2e -AllowUnsignedPublicRelease

发布脚本会强制运行单元测试、Core CleanE2E和最终安装包 E2E。后者会安装最终 Inno资产、运行安装后的 Setup.exe、安装固定开源测试 APK、验证 Root私有目录导出、精确检查两个 GUI窗口标题、检查桌面/开始菜单快捷方式，并执行保留 AVD 数据的程序卸载。随后从单一依赖 manifest 与实际三层 EXE生成 SPDX SBOM，调用固定版本的官方 SPDX Tools 做语义验证，并执行禁止内容、精确资产、凭据模式与 PE GUI子系统审计。

CleanInstallRoot 默认必须不存在或为空；ReuseE2EState 和 E2EDependencyCache 只用于本地迭代，正式标签工作流不复用任何状态。AllowUnsignedPublicRelease 必须显式提供，产物会强制带 `UNSIGNED` 文件名，不会伪装成已签名软件。

`.github/workflows/release.yml` 在 GitHub 托管的 `windows-2025` Runner 上响应版本标签。它从空产品目录运行全链 Root/AVD 和最终安装包 E2E，强制校验产物为明确命名的 `UNSIGNED` 文件，然后生成 SHA-256、SPDX SBOM 与 GitHub provenance，只创建 Draft。公开发布必须再由 `release-publish` 环境的审批者运行独立工作流，重新下载并校验精确资产列表、unsigned 状态、PE 版本、provenance、SHA-256 和 SBOM 后才能发布。

发布治理与安全边界见 [发布完整性与签名政策](CODE_SIGNING_POLICY.md)、[隐私说明](PRIVACY.md) 和 [安全政策](SECURITY.md)。
