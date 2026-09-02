# Rooted Android Game VM

一个面向 Windows 11 x64 的图形化安卓游戏虚拟机管理器。安装、日常启动、APK 更新、Root 诊断和私有数据导出都通过 .exe 窗口完成，普通用户无需输入终端命令。

> 当前版本为本地开发候选 0.1.0。在干净 Windows 11 端到端验收通过前，不创建公开 GitHub Release。

## 日常使用

1. 双击桌面的 **Rooted Android Game VM**。
2. 点击“启动虚拟机”。
3. 使用“安装或更新 APK”选择你合法取得的本机 APK。
4. 打开“应用与数据”，可以启动质感文件，或把应用私有目录导出到 Windows。
5. 也可以把单个 APK 直接拖到启动器窗口；应用页支持第三方包列表、启动、强停、确认卸载和任意安全相对目录导出。

### Arcaea 7.x 谱面

Arcaea 7.0.255c 下载内容位于：

    /data/data/moe.low.arc/files/dl

7.x 的 AFF 谱面仍是可读的 AudioOffset / timing(...) / arc(...) 文本，但文件不再使用 .aff 扩展名。常见文件名是 歌曲名_0、歌曲名_1、歌曲名_2 等。启动器的“导出 Arcaea 谱面”会完整复制 dl 目录，不会擅自重命名文件。

## 安全与兼容边界

- Release 不包含游戏 APK、账号数据、谱面、音视频、Google 系统镜像、Magisk APK 或 AVD 用户磁盘。
- 安装器只在用户接受 Android SDK 许可后，从固定 HTTPS 地址下载组件并校验 SHA-256。
- Root 允许访问应用私有目录，请只处理你有权访问的数据。
- Play Integrity、反模拟器、反 Root、专有 Vulkan 或特殊硬件依赖可能使个别游戏无法运行；项目不承诺兼容所有 APK。

## 开发与发布门禁

    .\build\Build-Release.ps1 -CleanInstallRoot D:\rgvm-clean-e2e -AllowUnsignedLocalCandidate

发布脚本会强制运行单元测试和真实 CleanE2E，然后生成两个 .NET 10 自包含 GUI EXE、编译 Inno Setup 单文件安装包，并执行禁止内容、精确资产、凭据模式、SBOM 与 PE GUI 子系统审计。AllowUnsignedLocalCandidate 只允许生成不可公开的本地候选；公开 Release 必须提供受信任代码签名证书的 SigningCertificateThumbprint，否则门禁失败。
