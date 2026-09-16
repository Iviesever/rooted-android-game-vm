# 2 GB 产品物理内存目标：实验记录

**2026-09-16已按用户要求结项并停工。** 2GB上限未通过，最终覆盖安装未执行；本次以现有成果收口，不再自动推进下文历史待办。保留配置、验证边界及证据入口见[结项记录](memory-optimization-closure.md)。以下为按时间保留的实验历史。

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

## 继续排查与控制进程开销

- Vulkan=true 在 1024/720p 配置下冷启动就观测到 2,095,935,488 B（尚无 GUI），不作为节省方案。
- 备份原两个空设置后，将 Android 缓存后台进程上限设为4，核验 `CUR_MAX_CACHED_PROCESSES=4`。896 MiB 配额虽能到主页，但游玩加载时再次 LOW_MEMORY，拒绝作为目标配置；峰值观测亦超2GB。
- 进程归属查询改用只读原生 API，避免常态初始化 WMI/DLR；仍验证精确路径、PID、启动时间和引用完整启动参数。命令行查询用动态链接并检查返回范围；不支持时保留 WMI 回退，拒绝无法证明归属的访问错误。
- 新增带空格/中文的真实子进程参数、父PID和启动时间回归；228 项测试通过、构建0警告错误。真实冷启动、Root和采样验证通过，broker 未加载 Microsoft.CSharp/System.Linq.Expressions，当前约78MB WS。不同运行阶段不当作严格同负载减幅。
- `MemoryProbe audit` 每100ms重新发现进程，减少5秒缓存造成的盲区；仍不承诺捕获所有瞬时尖峰。

## 进一步排除的配置（均未作为最终方案）

- software/SwiftShader：空闲且包含GUI约2.42GB，比host更高。
- 关闭 QuickbootFileBacked：确实从RAM文件映射变为匿名内存，但几乎全部1024MiB仍驻留，主页合计约2.11GB；已撤回该启动参数。
- 无原生窗口且保留音频：空闲仅比常规窗口减少有限占用，没有足够余量，且当前组合游戏低内存退出。SDK进程退出还出现非零终态，未将“无窗口”替代完整游戏使用验收。
- ASG：固定组件INI列出支持，实机 `ro.boot.hardware.gltransport=asg` 确认生效，Root/显示/启动工作；但主页带GUI及当时短命CLI约2.17GB。未证明有内存收益，已撤回产品参数扩展并将INI恢复pipe。
- 缓存进程上限4未证明改善游戏加载，原空设置已恢复，实际CUR_MAX_CACHED_PROCESSES回到32。没有卸载或清除应用数据。

保留的修复：native查询遇到已证实退出的短命工具时不丢弃其他活进程身份；PID观察等待避免低内存启动误报，明确仍未验证页面就绪。目标仍未达到，不覆盖安装。

## 保留系统与数据后的复核

用户进一步确认：保留现有 Android/Google 环境和数据，优先正常游玩；2,000,000,000 B 目标保持不变。

仅向本轮 broker/模拟器继承 `VK_DRIVER_FILES`、`VK_ICD_FILENAMES`，指向已安装 NVIDIA 清单，没有更改全局显卡设置。模拟器仍加载 Intel 与 NVIDIA 相关模块，未证明排除了未使用驱动。3986 个样本中，冷启动观测最大 **2,080,645,120 B**，系统空闲最大 **2,000,973,824 B**，游戏加载/游玩区间最大 **2,244,972,544 B**（包含 GUI 和辅助进程）。168 个样本存在读取错误，保留为缺样；阶段标签最后还覆盖了停机，低点不可当作运行节省。

原正式六轨画面已确认，但45秒帧采样包含加载，最长呈现间隔约21.1秒，不能把整个区间34.18 FPS称为稳态游戏性能。原缓存设置仍为null/null、实际上限32。试验已正常停止，带有环境变量的broker已退出；没有把这个结果集成成默认驱动设置。

## 补齐通用输出路径的上限

常规启动、安装和数据操作的 `ProcessRunner` 原来仍使用无限 `ReadToEndAsync`。现在 stdout/stderr 各最多8,388,608个UTF-16字符，内部调用可降低此上限；池化4096字符块，最终精确分配字符串。超限返回明确的 `output_limit`，停止并回收本次工具，不返回可被误解析的截断内容。标准输入写入也纳入同一取消/失败清理范围。它限制的是辅助输出缓存，不限制QEMU物理内存，也不保证大型安装日志一定能在上限内完成。

二进制预览的元数据先限制1MiB，PNG按已验证的声明长度分配、最高8MiB；不再先按通用64MiB上限分配后才检查预览大小。PNG、尺寸和会话契约保持。

新增测试覆盖文本边界/换行/UTF-16、两路输出超限、被阻塞的stdin取消、非零退出码、启动前校验和预览长度头。**242项非实机测试通过，完整构建0警告/0错误。** 这些检查不能替代全产品2GB与正常游玩验收。

## 原生分配取证与进一步排除（20:48）

用户要求保留现有 Windows 显卡驱动，NVIDIA 551.61、Intel 31.0.101.5445 均未更新。

- CDB非暂停附加获取的常规NT堆摘要只解释约158MiB提交，不能解释全部MEM_PRIVATE驻留。`!address -summary`在非暂停模式下遇到上下文读取错误，未作为完整映射结果；已有VirtualQueryEx/QueryWorkingSetEx分类继续单列。
- 另一次短时分配入口跟踪，在Malody启动期间观察到31次≥4MiB且带MEM_COMMIT的申请：14次来自NVIDIA OpenGL驱动栈，合计262,134,848B；17次来自gfxstream栈，合计401,690,504B。这是入口请求累计，既不是成功分配总额，也不是同时存活/驻留量；缺少完整私有符号，不能把最近导出符号的名字当成确切内部函数。初版断点条件有语法错误并暂停了VM，修正后重试应用；此轮不用于帧率或峰值验收。最后已清除断点并明确detach，VM会话随后仍通过status/Root核验。
- 原模拟器没有单独NVIDIA应用配置，线程优化继承值0。用户授权了仅该完整EXE路径的临时应用配置试验。普通调用及RunAs辅助程序在`NvAPI_DRS_SaveSettings`都返回-1；新会话查询确认应用配置仍不存在、继承值仍为0。因此未声称“关闭线程优化有效”，也没有修改全局配置。使用的ABI/设置ID来自[NVIDIA官方NVAPI头文件](https://github.com/NVIDIA/nvapi)。
- 将broker及模拟器子进程的CPU亲和性限制到8个逻辑处理器，实测QEMU掩码255；2592个样本中91个含读取错误，最大2,189,783,040B，尚未完成谱面游玩。无足够收益，相关进程已退出，未固化亲和性策略。
- 768MiB＋2GiB逻辑zstd交换＋swappiness180的组合已核验实际生效，但仍出现“系统界面没有响应”，即使`dumpsys activity lastanr`称没有记录。拒绝交付。原模块两个文件按备份散列恢复，再次冷启确认swappiness60、逻辑swap1572860KiB与1024MiB配置。

## ANGLE路径的现有驱动限制

现有API35镜像在`/system/lib64`自带ANGLE库，先前仅启用Vulkan的试验仍显示`OpenGL ES Translator`，不能把它当成已经验证过guest ANGLE。[上游启动属性实现](https://android.googlesource.com/platform/external/qemu/+/emu-master-dev/android/android-emu/android/userspace-boot-properties.cpp)说明了GuestAngle与Vulkan的关系；固定exe的`-help-feature`也确认环境变量覆盖入口。

随后分别测试了：

1. `GuestAngle,GuestUsesAngle,VulkanNativeSwapchain`加Vulkan；
2. 仅GuestAngle加Vulkan，显式关闭GuestUsesAngle与VulkanNativeSwapchain。

两组都在引擎兼容性检查阶段退出，原生日志明确指出NVIDIA **551.61.0低于所需553.35.0**。没有绕过版本检查，没有进入可做内存/游戏验收的阶段。首组产品启动任务还在等ADB，已明确取消该任务并确认工具退出；直接SDK诊断捕获了上述错误。

用户随后明确选择保留现有系统驱动，这两组路径当前不可继续。此前为准备可审阅的更新方案，已只读导出原驱动185文件、1,745,742,317B并记录SHA-256；官方Studio616.92笔记本包支持信息已核对，但下载返回403，未获得安装包、未安装。原驱动签名目录有效，备份不等于已经验证过回装。后续不继续推动驱动更新。

所有本轮VM、GUI、broker、采样器与调试器已停止；INI恢复1024/720p120/host/Vulkan=false/LowRam=true/heap576/4096启动门槛，原guest实验模块与8包状态维持上轮基线。源码功能仍为`1e64c7e`、242测试的版本，本轮没有集成任何未通过的运行策略。2GB目标和最终覆盖安装仍未完成；剩余允许的路线必须提供原生缓冲/图形后端或控制进程重构的数量依据，不能再用相同参数组合重复试验。
