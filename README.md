# Rooted Android Game VM

一个面向 Windows 11 x64 的图形化安卓游戏虚拟机管理器。安装、日常启动、APK 更新、Root 诊断和私有数据导出都通过 .exe 窗口完成，普通用户无需输入终端命令。
项目不针对、不捆绑任何单一应用或游戏；所有 APK 安装、应用启动和私有数据导出都使用通用的 Android 包名与相对路径。

> 0.1.2 按项目政策明确以 unsigned 形式发布，安装器文件名包含 `UNSIGNED`，Windows 可能显示 unknown publisher（未知发布者）警告。请只从本仓库 GitHub Release 下载，并核对随 Release 发布的 SHA-256 或 GitHub provenance。

## 日常使用

1. 双击桌面的 **Rooted Android Game VM**。
2. 点击“启动虚拟机”。
3. 使用“安装或更新 APK”选择你合法取得的本机 APK。
4. 打开“应用与数据”，选择任意已安装应用及其私有目录，将数据导出到 Windows。
5. 也可以把单个 APK 直接拖到启动器窗口；应用页支持第三方包列表、启动、强停、确认卸载和任意安全相对目录导出。

## 安全与兼容边界

- Release 不包含第三方 APK、账号或应用数据、音视频、Google 系统镜像、Magisk APK 或 AVD 用户磁盘。
- 安装器只在用户接受 Android SDK 许可后，从固定 HTTPS 地址下载组件并校验 SHA-256。
- 产品使用独立的 `%LOCALAPPDATA%\RootedAndroidGameVM\runtime\avd` 和 `rooted_android_game_vm_api35`，不会接管或卸载用户已有的全局 Android Studio AVD。
- Platform Tools、Emulator 和 Android System Image 使用固定 archive URL、官方 SHA-1与产品侧 SHA-256，不跟随 sdkmanager latest。
- Root 允许访问应用私有目录，请只处理你有权访问的数据。
- Play Integrity、反模拟器、反 Root、专有 Vulkan 或特殊硬件依赖可能使个别游戏无法运行；项目不承诺兼容所有 APK。

## 开发与发布门禁

    .\build\Build-Release.ps1 -CleanInstallRoot D:\rgvm-clean-e2e -AllowUnsignedPublicRelease

发布脚本会强制运行单元测试、Core CleanE2E和最终安装包 E2E。后者会安装最终 Inno资产、运行安装后的 Setup.exe、安装固定开源测试 APK、验证 Root私有目录导出、精确检查两个 GUI窗口标题、检查桌面/开始菜单快捷方式，并执行保留 AVD 数据的程序卸载。随后从单一依赖 manifest 与实际三层 EXE生成 SPDX SBOM，调用固定版本的官方 SPDX Tools 做语义验证，并执行禁止内容、精确资产、凭据模式与 PE GUI子系统审计。

CleanInstallRoot 默认必须不存在或为空；ReuseE2EState 和 E2EDependencyCache 只用于本地迭代，正式标签工作流不复用任何状态。AllowUnsignedPublicRelease 必须显式提供，产物会强制带 `UNSIGNED` 文件名，不会伪装成已签名软件。

`.github/workflows/release.yml` 在 GitHub 托管的 `windows-2025` Runner 上响应版本标签。它从空产品目录运行全链 Root/AVD 和最终安装包 E2E，强制校验产物为明确命名的 `UNSIGNED` 文件，然后生成 SHA-256、SPDX SBOM 与 GitHub provenance，只创建 Draft。公开发布必须再由 `release-publish` 环境的审批者运行独立工作流，重新下载并校验精确资产列表、unsigned 状态、PE 版本、provenance、SHA-256 和 SBOM 后才能发布。

发布治理与安全边界见 [发布完整性与签名政策](CODE_SIGNING_POLICY.md)、[隐私说明](PRIVACY.md) 和 [安全政策](SECURITY.md)。
