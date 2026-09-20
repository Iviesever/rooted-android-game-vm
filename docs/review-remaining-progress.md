# Review 剩余优化执行证据

目标：[review-remaining-goal.md](review-remaining-goal.md)。开始于683c443；用户的目标变更另由a51dabd提交。本文保留历史批次证据；**它们不代表2026-09-20新增通用化与文件管理要求已经通过**。

## 2026-09-20 当前状态与产品纠偏

现场复核：HEAD 为30372db；已安装程序为0.5.0.0，原生产程序备份及CI安装包散列、安装后CLI回归记录已存在（本机 `evidence/installed-050-hashes.json`、`installed-050-acceptance.json`）。GitHub `v0.5.0` Release 仍为草稿，publishedAt为空。此前段落的“尚未安装”属于对应批次当时状态。

用户指出产品应为通用安卓虚拟机，文件管理不应要求手写包名。源码核查确认专属包名、GUI卡片、命令、拖放和页面状态已耦合到核心；同时文件列表没有分页且只取4096项，GUI单选、共享根仅Download。旧安装候选尚不满足纠偏要求，不能直接发布。

已形成 [通用工作台与双向文件管理设计](generic-workstation-design.md)，同步目标与任务记录。本次仅完成核查和设计；A–E实现、GUI/CLI实机、数据恢复及新候选覆盖安装均未验证。内存门槛2560MiB例外保持，其余已结项边界不变。下列历史结果仍保留原证据与限制，不能外推为新设计通过。

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

- `io-read-acceptance.json`：独立测试皮肤由Malody自身Game:ReadFile读取替换文件、新文件及新子目录文件，随机值逐一匹配；没有将ADB/root读取当成App读取。
- `scope-read-acceptance.json`：本机现有SDK编译的12,629字节独立APK以实际应用UID10213、PID4714，分别读取private/external/shared的新建和替换文件，六项随机值均一致。private实际10213:10213:600，external实际2000:1078:660，共享存储由Android映射为10200:1023:660；记录实际值，不以chmod请求值替代。shared夹具targetSdk28并获READ/WRITE_EXTERNAL_STORAGE，只证明允许共享读取的App，不能宣称绕过其他App的scoped-storage权限。APK不读取生产应用数据，不进入发布资产。
- `metadata-rewrite-import/resume.summary.json`：5文件中4个SHA一致，info.json单列已知元数据重写。现有正式包只读比对确认版本393216→394764，严格限定该已观察转换，其余原有字段不放行任意变化。
- `isolated-resource-negative-import.summary.json`：只修改隔离夹具runner.lua，得到import_content_mismatch及逐文件证据，未重传掩盖异常。
- `import-recovery-acceptance.json`：4秒受控超时后同一importId续作，2/2核验通过、transferCount仍为1、remote相同。超时外层丢失阶段的问题已发现，属于后续状态/协议批次，尚未宣称解决。
- `recovery-attempt1/`保留未就绪及重复曲目续作失败。修复了“有PID但后台Activity未前置”，且包被App消费后继续有界等待解包；仍不能把游戏去重、未观察到解包或未知状态报成成功。
- `batch1-full.trx`：269项非实机测试通过，覆盖未知/重复元数据字段、签名资源差异、新建替换权限及子进程提前退出/取消后的双流输出和进程回收。工具证据上下文还需下一批接入协调进程。

未完成：完整状态/协议、GUI/CLI会话摘要、六指和端到端音频/判定延迟、三轮持续性能/音频、完整恢复、打包覆盖安装与v0.5.0发布。不能用以上局部结果勾选全部目标。

## 第二批：状态、任务契约和恢复

请求ID、任务ID、已观察的session/PID、阶段、明确终态和产物目录已接入协调进程。保留schemaVersion=1和旧结果字段，schema命令增加请求/响应JSON Schema；命令行、客户端异常和CLI文档同步。大结果引用保留身份，超过64KiB的进度写文件；状态/预览正常轮询不累积双流副本。长任务工具保留stdout/stderr、实际工具PID、退出码与回收结果。超时不再覆盖原错误阶段/证据。

`debug-runs/jobs/`保存原broker身份和原任务状态。恢复不自动重放；未记录明确终态的任务是interrupted。当前实现只恢复最近128份索引，磁盘产物不因内存索引裁剪被删除。

验证：

- `protocol-offline-acceptance.json`：真实CLI的设备离线、1秒超时；VM停机下运行1000个只读schema步骤，仅写本次产物，按路径/PID/启动时间核验后中断本次broker。新broker查询原ID得到interrupted、原阶段、原请求ID，未重放、VM保持停机。不是在唯一guest磁盘事务中断电。
- `protocol-live-acceptance.json`：实际ADB shell退出7，stdout和stderr均保留；1秒超时与显式取消分别得到timed_out/cancelled。宿主ADB进程回收，两个guest shell PID对应/proc目录均不存在。150万字节stdout使用文件引用，原请求/任务ID保持。已安装0.4.0 CLI可以查询新broker的status。
- `protocol-malody-activity.summary.json`、`protocol-app-observed.summary.json`：真实冷启动Malody，PID5371与前台resumed Activity有对应dump和时间；interactiveReady仍为false，未将Activity当成页面验收。
- `protocol-final-full.trx`：277项非实机通过；`protocol-final-build.txt`：完整构建0警告0错误。最终代码再次通过离线协议和中断恢复流程。纯逻辑回归覆盖延迟PID、终态区分、大进度/大结果身份、旧请求兼容及原始工具输出。
- `protocol-preserved-formal-acceptance.json`：历经正常停机/冷启动后，正式皮肤与登记共1995个文件散列全部保持；不将其扩大成整个App数据库每字节不变的证明。

状态/错误中与输入释放相关的实际回归仍待第5项完成；GUI/CLI会话摘要、实机多指/端到端延迟、持续流畅性、完整恢复、最终安装发布仍未完成。

## 第三批：会话摘要与输入归属防护

新增session.summary，GUI直接绑定后端summary.text。实例、App/PID、任务、文件核验、释放依据、产物、恢复点和下一步使用同一数据；启动/停机等独占阶段返回OperationInProgress，ADB离线但进程仍在不再报Stopped。Malody页面通过带真实截图、时间、进程及会话的调用者标注提供，过期/操作后/进程或前台变化失效；不会把PID或Activity当作Unity页面识别。

已验证：

- `session-live-layout/session-cli-snapshot.json`与`session-text.txt`逐字相同：真实Malody PID3571、真实VM会话、已观察首页的截图和时间进入WPF绑定。两种窗口尺寸渲染无绑定错误；最小布局已视觉检查。这里是WPF使用真实CLI快照的离屏验证，最终安装后的实际GUI仍待后续验收。
- `session-page-home-observed.summary.json`引用真实首页截图；`session-page-dark-unverified.summary.json`对启动时暗屏保持unknown；`session-page-stale-rejected.summary.json`拒绝旧截图标注，`session-page-expired.summary.json`标superseded。
- `session-during-stop.summary.json`在真实停机中返回OperationInProgress，实测约0.090秒；随后stop明确完成，未把处理中状态报成已停机。
- `session-full.trx`：288项非实机通过；`session-final-build-fixed.txt`：完整构建0警告0错误。新增末次委托签名编译错误已修正，保留失败日志不冒充通过。

附带补缺：生命周期ProcessRunner也保存双流与完整性标记；输入每次RPC和清理固定原VM会话，截图前后核对PID、前台与旋转，释放未确认保留错误，新broker重连先清理。上述输入防护目前只有相关逻辑回归，真实六指/取消/断连/跨会话验收仍未完成，不能因本节GUI通过而勾选第5项。

本批最终验证：`session-delivery-full.trx`为292项非实机通过，`session-delivery-build.txt`完整构建0警告0错误。新增大于16MiB的立即响应保存为完整文件引用的边界回归，保留普通小响应形状；gRPC取消/超时/断连分别分类且不输出认证详情。最终停机摘要同时验证已停止实例的输入状态和独立App的最近文件传输核验。依然未执行最终覆盖安装或发布。

## 第四批：真实输入、延迟、三轮性能及数据保持

日期：2026-09-17。软件仍使用原现场配置，启动物理余量仅按用户要求为2560MiB。图形驱动、现有系统镜像、内存实验配置和正式游戏资源未改。

大包补验暴露并修复了并发解包时目录枚举重复：相同路径/相同散列去重；同路径不同散列是未稳定采样，要求连续两份相同清单后才核验。已存在解包目录时继续等待，不再次触发。`controlled-qa-resume.summary.json`对原importId续作，仍只上传一次，1988/1989严格一致，info.json单列已知重写。额外为启用该独立QA副本向同一已传文件发送过VIEW，见`qa-retrigger-existing-to-enable`；没有新上传或新建第二个目录。

真实Malody六轨：原D6-Controlled-QA-vm-3.msp（SHA-256 `949ea42251293f20f1651b2cfb0f7e34dee2ce05442f205226dccdf6fef9dacf`）作为隔离副本运行；正式皮肤目录没有改写。`measured-input-first/acceptance.json`显示六指共同按住、六轨效果、独立释放、三组短按和最终清零均通过，实际游戏分数截图为3,389,639。后续真实菜单重试及三轮输入再次进入同一场景并计分。

释放证据来自独立Android Activity真实MotionEvent，不拿发送账本代替接收：`input-faults/acceptance.json`覆盖六指/逐指释放、取消、3秒超时、CLI断连和broker中断重连。CLI断连约5.60秒后按租约清零，重连约1.15秒后清零，原任务恢复为interrupted。`input-stale-session-rejection.summary.json`拒绝旧VM观察，新实例Activity记录0个输入事件。

端到端测量边界与误差：

- 发送端为实际gRPC调用时的Windows QPC；新增可选startAtQpc绝对调度，过期序列拒绝补发。sentMs/deviationMs只用于调度审计，未当作响应延迟。
- 游戏侧用实际QA按键状态和帧更新日志。18次带QPC前后界限的时钟读取建立区间映射，含时钟文件陈旧区间、下一次Update和1ms取整；映射区间宽约62.66ms。只报告区间，未声称精确单一判定时刻（该精度仍未验证）。
- 独立30Hz framebuffer观察：一次无新音符起点的短按，在发送后118.41ms首次取得明确可见反馈；上一未变观察为86.56ms，采样间隔31.85ms，该次RPC耗时12.76ms。此值包含图像采集传输，不能冒充Android处理函数耗时。原始均值帧及时间戳在`measured-input-first/visual.*`；RGBA行序经PNG对照确认，本引擎实际为top-down。
- VM进程树WASAPI回环PCM实际捕获到5组对应声音，发送至可测PCM起点为约553.83、446.65、460.28、431.02、487.29ms，48kHz、16bit、5ms检测窗、噪声阈值8（前一秒背景峰值1）。同宿主校准音三次write→捕获起点35.43–39.01ms；相对playback位置推算，观测链路约16.52–24.38ms。Android AudioTrack夹具约180.01ms，说明游戏/音频库/资源路径还有额外延迟，不能全部归于产品。详细证据为`audio-calibration.json`、`input-faults/android-tone-latency.json`及`measured-input-first/acceptance.json`。
- 依据：[微软进程回环说明](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/)与[GetBuffer时钟定义](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer)。本次维持用户主输出静音，验收边界是VM输出PCM；**扬声器声波传播、主观听感和精确声学延迟未验证**，不包含在上述通过项中。

三轮固定配置、原六轨场景的独立稳态统计（`performance-acceptance.json`）：

| 轮次 | 实际稳态覆盖秒 | FPS | P95 ms | P99 ms | 最慢帧 ms | 历史缺样 | 音频包/断续 |
|---|---:|---:|---:|---:|---:|---:|---|
| 1 | 69.50 | 94.17 | 18.19 | 41.32 | 499.50 | 0 | 7500 / 0 |
| 2 | 69.09 | 94.06 | 18.04 | 41.35 | 467.18 | 0 | 7500 / 0 |
| 3 | 69.67 | 93.30 | 18.06 | 42.39 | 341.08 | 0 | 7500 / 0 |

三轮各捕获75秒PCM，无timestamp-error，相邻包时钟无缺口，均有持续有效音频。预期静默和素材包络不当成掉音；低幅片段不作无依据归因。加载另列：首次可观察游戏Update约14.37、1.01、1.42秒，后两轮为场景重试。加载边界按真实发送和新游戏时钟读取定义，含400ms轮询间隔；通过主机查询界限与相邻present时间建立约40–55ms的帧时钟区间，只统计完整落在加载窗口内的帧。首次加载含约13.40秒无新present帧，绝不混入稳态P99。暖加载样本少，其P99接近最慢帧，不能当作大样本推断。

宿主整体CPU均值约0.83–1.17%，最大约2.70–3.70%，但这不能排除单核/GPU限制。大长帧不都发生在同一产品操作附近；已知场景含解密、纹理和QA日志工作，目前没有足够证据将全部长帧定为可修改的产品瓶颈。因此没有更换驱动、镜像、调内存或修改正式资源，也不声称提高了游戏帧率。若继续优化该延迟/长帧，需要另行缩小到游戏/引擎各级路径；现有统计与归因限制完整保留。

恢复/保持：`data-retention-acceptance.json`确认多次正常sync停机、冷启动、受控取消、中断及同ID续作后，Root正常，正式皮肤/登记1995个文件与初始锚点一致；独立App六项私有/外部/共享测试值在更新及冷启动后仍由App读出。原曲目可见，安装前原曲目文件基线单独保存。中断只作用于隔离产物或独立输入夹具，未断电破坏唯一数据，未故意耗尽宿主内存。正常会话计数、日志和测试成绩变化不被虚称为整库逐字节不变。

第四批候选：`release-candidate-full.trx`为295项非实机通过，完整构建0警告0错误。发布runner能力探针已通过：[run 35128870486](https://github.com/Iviesever/rooted-android-game-vm/actions/runs/35128870486)。最终安装、安装后回归、公开v0.5.0发布仍待完成。
