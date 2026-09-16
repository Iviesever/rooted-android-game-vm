# Review 剩余优化执行证据

目标：[review-remaining-goal.md](review-remaining-goal.md)。开始于683c443；用户的目标变更另由a51dabd提交。本文是执行中记录，**不是v0.5.0整体验收通过或已安装/发布的声明**。

本机完整台账：`tasks/20260916-211326-review-remaining/`，含四文档、脚本及evidence（原始实机证据仅保存在本机，不进入公开发布资产）。内存优化仍结项；唯一后续例外是用户明确要求启动物理余量门槛改成2.5GiB。d534f9d实现2560MiB下限；前后profile逐字段核对仅startAvailableMb改变，运行期保护未改。

## 第一批：导入、作用域权限和故障证据

- 旧行为复现：`baseline-cold-import.summary.json`返回ok:true但0/2文件、awaiting_app_confirmation；`baseline-cold-late-verification.summary.json`稍后确认两个文件实际落盘。
- 新导入记录区分传输、App进程/Activity、Intent、解包核验和等待启用。原始Activity、Intent、逐文件散列和阶段记录持久化；源包快照限制与散列固定，续作复用同一importId和远端路径。未观察到实际启用/游玩时字段保持false。
- 既有文件不再一律按原600权限替换；外部文件660，私有按App归属和合理读写位，共享目录保留合理读写位；实际权限返回，不能将传输自身标成App读取通过。

固定现场：现有1280×720、160dpi、120Hz请求、host、4核、1024MiB/lowRam、VM堆576MiB，系统镜像与驱动不变。冷启动指每轮先force-stop Malody；此前及后续VM本身也有正常停机/启动，但这六轮不声称六次VM冷启动。

| 模式 | 轮次 | 秒 | 请求ID | 传输/触发次数 | 实际解包 |
|---|---:|---:|---|---|---|
| App冷启动 | 1 | 19.00 | 0c09f3e8d1ae42c0bafa7219aacae93c | 1/2 | 2/2散列一致 |
| App冷启动 | 2 | 25.63 | 5ac0634f9e0048ad95becb26dd0a8fc9 | 1/2 | 2/2散列一致 |
| App冷启动 | 3 | 22.52 | 8d6c6d4a1641458ea6280887fdbbffbe | 1/2 | 2/2散列一致 |
| App已运行 | 1 | 6.74 | fa091e2acb494db4b7fcdb912d26cc24 | 1/1 | 2/2散列一致 |
| App已运行 | 2 | 3.69 | 21fe550d712d424d89bed39f040844a7 | 1/1 | 2/2散列一致 |
| App已运行 | 3 | 3.18 | d0b077c2d38d43f887b29642528e3f82 | 1/1 | 2/2散列一致 |

冷3/3、热3/3；详见`evidence/import-rounds.json`，逐请求NDJSON、目录和散列均可追溯。每包修改合成曲目标题以避免游戏自身去重。不能外推大包或任意资源的成功率。

其他实机证据：

- `io-read-acceptance.json`：独立测试皮肤由Malody自身Game:ReadFile读取替换文件、新文件及新子目录文件，随机值逐一匹配；没有将ADB/root读取当成App读取。当前只实测external作用域，private/shared实际App读取仍待验证。
- `metadata-rewrite-import/resume.summary.json`：5文件中4个SHA一致，info.json单列已知元数据重写。现有正式包只读比对确认版本393216→394764，严格限定该已观察转换，其余原有字段不放行任意变化。
- `isolated-resource-negative-import.summary.json`：只修改隔离夹具runner.lua，得到import_content_mismatch及逐文件证据，未重传掩盖异常。
- `import-recovery-acceptance.json`：4秒受控超时后同一importId续作，2/2核验通过、transferCount仍为1、remote相同。超时外层丢失阶段的问题已发现，属于后续状态/协议批次，尚未宣称解决。
- `recovery-attempt1/`保留未就绪及重复曲目续作失败。修复了“有PID但后台Activity未前置”，且包被App消费后继续有界等待解包；仍不能把游戏去重、未观察到解包或未知状态报成成功。
- `batch1-full.trx`：269项非实机测试通过，覆盖未知/重复元数据字段、签名资源差异、新建替换权限及子进程提前退出/取消后的双流输出和进程回收。工具证据上下文还需下一批接入协调进程。

未完成：private/shared实机读取、完整状态/协议、GUI/CLI会话摘要、六指和端到端音频/判定延迟、三轮持续性能/音频、完整恢复、打包覆盖安装与v0.5.0发布。不能用以上局部结果勾选全部目标。
