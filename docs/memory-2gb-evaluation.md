# 2 GB 产品物理内存目标：实验记录

2026-09-16 用户目标：整个产品宿主物理内存最多 2,000,000,000 字节；提交内存另列。QEMU、模拟器父进程、broker、GUI、产品 CLI 与 ADB 均计入；共享 ADB 单列归属。每批通过检查后提交，最终覆盖安装。尚未达标时不把 guest 配额当结果。

## 第一组：普通 1536 MiB 请求

固定 Emulator 37.1.11、API 35，1080p120/host GPU。普通 `-memory 1536` 实际生成的 `hardware-qemu.ini` 是 `hw.ramSize=2560`，guest MemTotal=2532432 KiB。QEMU 驻留超过 3 GiB。因此引擎自动调整是关键影响因素。

上游 [main-common.c](https://android.googlesource.com/platform/external/qemu/+/refs/heads/emu-master-dev/android/android-emu/android/main-common.c) 包含按 API/屏幕调整 RAM，以及显式 `-lowram` 解除下限的逻辑。实际固定版本的 `emulator -help-lowram` 也确认支持；不单凭可能变化的主线源码推断实际配额。

## 第二组：显式低内存 1024 MiB

新增 `lowRam` 与 `vmHeapMb` 配置贯穿存储、GUI 保存、启动命令和准入。普通模式仍保持原默认；小配额必须显式启用。`runtime.inspect.observedMemory` 报告实际分配和 guest 可见内存，防止静默升配。

以 `-lowram -memory 1024`、堆上限 576 MiB 和原 1080p120 冷启动成功：实际分配 1024 MiB，guest MemTotal=988760 KiB，Root 正常。某些启动阶段产品进程 WS 合计约 1.8–1.9 GB，尚未计入 GUI 的完整运行验收。

**游戏未通过**：Malody PID 4877 在 2026-09-16 09:02:39 UTC 被 Android 以 `LOW_MEMORY` 终止。guest zram 约 741564 KiB，接近耗尽，存在频繁换入换出和预装后台重启。`-lowram` 本身没有令 `ro.config.low_ram` 为 true。不能把启动成功或游戏消失后低占用称作达标。

该批 226 项非实机测试通过、全方案构建无警告/错误；配额/启动参数、实际配额观察、GUI 往返及独立提交保护已验证。后续仍需 Android 低内存策略与后台驻留、图形路径、整个产品运行峰值、多轮游戏验证。没有工作集强制修剪或停止无关宿主进程。

本机详细原始证据在外层任务的 `evidence/limit-2gb/`；本文件只记录可供源码审阅的结论，不携带应用私有内容。
