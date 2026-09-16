# 内存审计工具

从源码运行（先构建，实测时用已构建 EXE 避免编译干扰）：

```powershell
dotnet build tools/RootedAndroidGameVM.MemoryProbe -c Release
$probe = '.\tools\RootedAndroidGameVM.MemoryProbe\bin\Release\net10.0-windows\RootedAndroidGameVM.MemoryProbe.exe'
'stopped-baseline' | Set-Content D:\audit\stage.txt
& $probe sample D:\vm-data D:\audit\memory.ndjson D:\audit\stage.txt 1800 1000 C:\path\to\installed-program
```

参数：资源根、全新输出 NDJSON、阶段文本文件、最多 7200 秒、间隔 250–10000 ms、任意多个程序目录。该工具只读取内存/进程；不启动 broker、VM 或应用。输出含单调时间、实际采样耗时、进程清单年龄和错误。清单每 5 秒或阶段变化时重查，每个样本仍校验可执行路径/PID/启动时间；短命进程可能漏采。`adb-shared` 可能被其他设备共享，`cli-or-broker` 包含临时 CLI 客户端。`observer` 指采样器本身，单列以识别测量开销。

全产品审计还包含同 SDK 下的 netsimd、crashpad 等 `emulator-helper-shared` 辅助进程，以及指定程序目录内的 Setup。优先使用原生 Toolhelp、映像路径/时间及有界命令行查询，系统不支持命令行信息类时回退 WMI；路径仍逐个精确核对。`observedWorkingSetSumBytes` 是本次观测到的进程 WS 之和，共享页可能重复计入；它不是未采样时刻的硬峰值保证，存在错误/退出或清单刷新间隙时不能宣称完整覆盖。`observedPrivateCommitSumBytes` 单独列示，不当作物理 RAM。

`audit` 与 `sample` 参数相同，但默认 100ms、最低 50ms，且每次重新发现进程，不用 5 秒清单缓存。用于最终峰值审计；仍记录实际耗时与缺失，不能把有限频率称为内核硬上限。长时间低负担观察继续使用 `sample`。

在另一个终端按实际状态更新 `stage.txt`：停机 30s、冷启动全程、系统空闲 60s、游戏主页 60s、资源加载、同一谱面游玩 60s、应用退出 60s、VM 停机后 30s。写入 `done` 或 Ctrl+C 停止并刷盘。至少三轮才分析重复趋势；阶段证据必须来自实际状态，不能仅靠写标签。宿主余量不足/保护停机时终止负载，不为凑齐样本重启。

对照保存 `runtime.inspect`、组件版本、guest `/proc/meminfo`、应用 `metrics` 和采样进程自身开销。进程生命周期峰值不能归入当前阶段；阶段峰值由同一 PID+启动时间样本计算，有限频率可能漏掉瞬时峰值。guest PSS 已在 QEMU 内，显存和驱动占用须单独分析。

`buffers <新目录>` 运行相同合成数据的旧 MemoryStream、新池化块和文件流写入基准，并比较 24 份任务结果的托管存活量。基准主动 GC 只为隔离测量；产品不调用强制 GC 或工作集修剪。分配量和托管存活量不能外推成 VM 总 RAM 节省。
