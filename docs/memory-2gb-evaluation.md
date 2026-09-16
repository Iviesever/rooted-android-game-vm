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

## 后续实验

- `-prop ro.config.low_ram=true` 在固定镜像上没有生效；已撤回该命令行改动。运行期 resetprop 加框架重启可令 low-ram 生效，但游戏仍被 LOW_MEMORY 终止，不作为交付方案。
- 保存原启用状态后，实验性暂停 8 个已测到驻留的预装搜索/智能/照片/消息/健康/壁纸/日历包；未卸载、未清数据，GMS/WebView/账号/Root 保留。仅此项及 720p/120Hz 仍不足以运行游戏。
- 添加独立可禁用的 Magisk 实验模块，冷启动设置 low-ram、使用 zstd 和 1.5 GiB **逻辑压缩交换容量**。这不是增加宿主 RAM 或主机磁盘交换。guest 仍分配 1024 MiB。原 vendor 镜像未改写，原 fstab 和模块归属记录已保存。
- 此组合已让原正式六轨皮肤进入游玩，稳定阶段约 30 秒观测 98.53 FPS，P95 17.39ms、P99 25.95ms；加载区间有约 16 秒无新帧，不混入稳态帧率结论。
- **仍未达标**：包含 netsimd/crashpad/broker/ADB 的一次游玩快照为 **2,160,308,224 B**，且 GUI 尚未计入。仅游戏保持运行和后续工作集回落不能代替 2 GB 峰值目标。
- 已发现原采样漏列模拟器辅助进程，现扩展清单并合并 WMI 查询，提供观测 WS 与私有提交的独立总和；共享/缺样限制继续显式报告。

## GUI 与更小配额试验

真实 GUI 也必须计入。默认 WPF GPU 绘制下，GUI 本机工作集约 214 MiB。控制界面主要是静态控件和低帧率预览，因此低内存模式改为 WPF 软件绘制，Android 游戏的 host GPU 设置保持独立。改后观测约 108 MiB，窗口响应和正常关闭通过，构建无警告错误；这两点样本不是完整峰值保证。

768 MiB guest +720p120+上述压缩/后台配置虽能冷启动且观测总量低于 2 GB，但实际截图出现“系统界面没有响应”，拒绝作为最终配置。对应 `dumpsys activity lastanr` 却报告无记录，因此保留视觉失败证据，不让单一结构化查询覆盖实际异常。

继续方向：保留已验证的 GUI 减负，回到游戏能运行的 1024 MiB guest，比较图形路径与加载峰值；仍须包含 GUI、辅助进程、加载/重复加载和安装后验证，达到目标之前不覆盖现有生产安装。
