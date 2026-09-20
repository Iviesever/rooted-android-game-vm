# Review 剩余优化执行证据

目标：[review-remaining-goal.md](review-remaining-goal.md)。开始于683c443；用户的目标变更另由a51dabd提交。本文保留历史批次证据；**它们不代表2026-09-20新增通用化与文件管理要求已经通过**。

## 2026-09-20 当前状态与产品纠偏

现场复核：HEAD 为30372db；已安装程序为0.5.0.0，原生产程序备份及CI安装包散列、安装后CLI回归记录已存在（本机 `evidence/installed-050-hashes.json`、`installed-050-acceptance.json`）。GitHub `v0.5.0` Release 仍为草稿，publishedAt为空。此前段落的“尚未安装”属于对应批次当时状态。

用户指出产品应为通用安卓虚拟机，文件管理不应要求手写包名。源码核查确认专属包名、GUI卡片、命令、拖放和页面状态已耦合到核心；同时文件列表没有分页且只取4096项，GUI单选、共享根仅Download。旧安装候选尚不满足纠偏要求，不能直接发布。

已形成 [通用工作台与双向文件管理设计](generic-workstation-design.md)，同步目标与任务记录，开始分批实现。A–E整体验收、GUI/CLI实机、数据恢复及新候选覆盖安装仍待执行。内存门槛2560MiB例外保持，其余已结项边界不变。下列历史结果仍保留原证据与限制，不能外推为新设计通过。

### A1：移除核心专属流程（2026-09-20）

继设计提交7c5b579后，已移除默认包名、专属UI卡片/拖放规则、malody.*命令、导入业务及元数据规则。通用进程/Activity观察保留；app.page.annotate支持显式目标应用的调用者页面标注，保留截图、时间、PID、会话及过期依据。旧任务记录和用户文件不删除，核心不再生成旧专属导入的自动续作入口；schema v1的旧摘要字段保留为空以兼容读取。

应用操作缺少package时返回app_required，不启动工具或生成设备产物；session.summary允许不选应用，shared文件无需虚构包名。GUI取消自动选择第一项；文件/诊断页提供应用列表选择，应用切换清空旧目录，列表刷新保留有效选择，卸载/会话变化清除失效目标；旧目录请求延迟返回时不得填入新应用的文件列表。此时应用列表仍显示真实包名，名称/图标/搜索待A2实现，不能称完整易用性验收通过。

证据（本机tasks目录）：generic-core-targeted.trx通过57项；最终generic-core-final.trx通过287项非实机回归，generic-core-build-final.txt完整构建0警告0错误。generic-core-layout/保留14份离屏布局和空binding-errors.txt，证明两种尺寸的WPF绑定无错误，**不等于实际GUI可用性或实机传输验收**。旧专用核验代码及仅覆盖该代码的测试一并移出核心，因此测试总数不与旧候选作功能覆盖率对比。

A–E整项仍未勾选；应用元数据/搜索、多根与分页、多选计划、完整双向传输、实机一致性/恢复和新候选覆盖安装仍待执行。未更改内存配置、镜像、驱动或正式应用资源，未发布旧草稿。

### A2：真实应用目录、搜索与引用（2026-09-20）

应用目录通过自有短进程读取Android包管理元数据，不安装APK或修改系统镜像。新增apps.list搜索/分页/可选系统应用/48px图标产物、apps.resolve重新验证实例与安装身份；旧apps包名数组保持。GUI应用页及文件/诊断选择器共用同一清单，显示真实名称/图标和辅助包名，搜索时清空旧目标，刷新保留有效身份。GUI当前主用户0；多用户文件映射和将引用接入传输仍待B批。

本机首次启动遇到可用约804MiB低于既有2560MiB门槛，保留失败请求6a5e28a9d8d7478381c7697019000187。用户提示余量已增加后立即复核为4153MiB，按原门槛启动成功，请求2aaf13a1d8c8487e92bf91c3b784377f；没有降低门槛或关闭其他宿主应用。

- `catalog-live-acceptance.json`：5个第三方应用真实名称及PNG图标，3页无遗漏/重复；与旧apps列表一致；名称和包名搜索一致；含系统应用的246项清单通过两页读取。正常引用可解析，其他实例/改变安装修订的引用被拒绝。
- `catalog-reinstall-acceptance.json`：真实保留数据重装隔离读取夹具后，旧引用拒绝、新引用可解析；UID10213保持，6项私有/外部/共享原测试值由App自身重新读出并一致。没有重装正式应用。
- `catalog-rebuild-verified.txt`：从Java源码重新编译DEX与内置散列一致；清单同时锁定源码、Android API JAR和D8 JAR散列。CI及完整Release脚本已接入重建门禁，远端门禁尚待执行，不能以本地结果冒充。
- `catalog-full.trx`：296项非实机通过；`catalog-build-final.txt`完整构建0警告0错误；`catalog-format-final.txt`格式门禁通过。为通过既有CI格式要求，旧C#文件的空白格式另行整理，无内存策略行为变更。
- `catalog-layout-final/`：真实元数据填充的14份离屏布局无绑定错误；1100px应用页已视觉核对搜索提示、图标、名称和包名，修复横向溢出。该证据不是实际GUI点击验收，文件双向交互仍待C批。

本批VM已正常保存停机；未覆盖安装、未发布旧草稿。A整项仍缺无Malody环境等验收，B–E继续未完成。

A2远端结果补记：[CI 35487448022](https://github.com/Iviesever/rooted-android-game-vm/actions/runs/35487448022)在1ce2f6e上通过：独立重建DEX散列一致，296测试、构建零警告错误、format及actionlint通过。原始结果存本机catalog-ci.json和catalog-ci-summary.txt。

### B1：真实根目录、批量目录页与文件引用（2026-09-20）

新增users.list/files.roots/files.browse/files.stat。应用引用重新核对安装身份；根从包管理元数据和按用户过滤的已挂载卷推导，区分私有、设备保护、外部、OBB、媒体与共享。不存在/未解锁/缺元数据的根保留明确状态。目录元数据一次批量读取，单页1–500项、目录容量100000项；超限或目录变化明确失败，不再用无提示截断表示完整清单。新引用绑定持久实例、运行会话、用户、作用域及文件版本；SHA计算检查实际打开的描述符位于选定根内，链接仅列元数据。

首次实机发现getVolumeList要求调用包名与UID匹配，UID0不能走应用归属接口。保留browser-roots-initial-failure.json及工具双流；展开反射异常后改为只读维护卷清单getVolumes，按用户可见/已挂载过滤。没有修改系统权限策略或镜像。

- `file-browser-live-acceptance.json`：主用户解锁状态与六类根发现；私有隔离目录4107个文件分9页全部返回且无重复，UID10213、mode600。中文/空格、竖线、换行、反斜杠及emoji文件名都能定位并计算正确SHA。静态链接只列元数据，跟随链接/目录越界/伪造卷被拒；修改文件后旧entryRef与旧分页游标被拒。
- 同一记录及browser-cold-*：正常停机/冷启动后旧根引用返回stale_reference；新引用能再次读取4107项及正确散列/权限，Root可用。测试写入只用于独立夹具准备，未写正式应用文件；这不是双向传输验收。
- `file-browser-full.trx`：308项非实机通过；file-browser-build-final.txt完整构建零警告错误，file-browser-format-final.txt格式通过；file-browser-rebuild-verified.txt源码重建与18,200字节内置DEX一致。

B1仅证明上述只读后台。非主用户/额外实体卷、GUI点击、旧files.*适配、多选传输计划、大文件/目录双向传输、取消续作及最终安装仍待后续批次。VM及源码broker已正常停止，未覆盖生产程序或发布旧草稿。

B1远端CI补记：[35489301863](https://github.com/Iviesever/rooted-android-game-vm/actions/runs/35489301863)已success，不能将此结果扩大为后续执行器验收。

### B2a：持久多选传输计划（2026-09-20）

新增files.transfer.plan/inspect：批量扫描来源并落盘NDJSON，保留空目录、每个普通文件SHA/版本、权限与链接元数据；计划汇总新增/相同/合并/同名差异/类型冲突、Windows不支持名称、空间估算和需停止的应用。重复/被父目录包含的选择去重；目标碰撞明确标出。计划写本机受限任务记录，不停止App、不创建用户目标目录、不执行复制。安卓停止或broker重启后仍可分页查阅，status=planned且transferVerified=false。

- `transfer-plan-live-acceptance.json`：私有目录4109项（4107文件、所选根和另一个空目录）完整进入计划，普通文件全部有SHA，空目录保留；来源App PID前后相同，电脑目标目录未创建。上传计划识别same/different/type_conflict，安卓已有目标内容仍为old-value，新目标文件不存在。Windows不支持名称和链接在普通目录计划中标出；tar计划可保留这些条目，但**没有实际写出归档**。
- `plan-after-broker-restart.summary.json`：正常停止安卓、退出原broker后，新broker读取原planId，仍为planned/4109项/未传输。
- `transfer-plan-full.trx`：317项非实机通过；transfer-plan-build-final.txt完整构建零警告错误。Java哈希路径显式关闭自有文件描述符，避免递归扫描依赖进程退出才回收。

本批遇到一次独立启动失败：请求fdd7b0be025a4ef09f97baa1079195d6、job840ac572f8704094acb58febebdc16d0；package服务和system_server存在，但sys.boot_completed未完成，8分钟后明确timed_out并退出本次实例。plan-startup-*保留只读启动/电源/系统日志；没有擅自调内存或改镜像。确认任务终态且QEMU已退出后，同配置正常冷启动复核请求468366c0b88646dca992a910fda53250成功，Root正常。本次未定位该偶发启动延迟根因，不把复核通过写成已根治。

执行、幂等任务、覆盖备份、块传输、真实归档输出、取消/续作和GUI接入尚未实现/验收；B整项继续未完成。测试后VM和源码broker正常停止，未覆盖安装或发布。

B2a远端CI补记：[35491085130](https://github.com/Iviesever/rooted-android-game-vm/actions/runs/35491085130)已success。

### B2b：基础执行、幂等与暂存恢复（2026-09-20，部分验收）

新增files.transfer.start/resume：明确策略、应用停止核验、源/目标重验、8MiB块、前缀及最终SHA、覆盖备份、逐项追加账本与真实tar输出。同命令/计划/幂等键派生固定jobId，参数改变拒绝，broker重启不会自动重放；明确resume才核对旧状态。执行进程身份含PID/启动时间，查询可将失去归属的运行记录标为interrupted。已完成项续作重验，暂存与备份位置保留。

- `transfer-execute-live-acceptance.json`：17MiB二进制文件及中文小文件、嵌套目录、空目录的私有双向传输通过；源与下载SHA一致，目标UID10213/mode600。覆盖后原值在备份中读出。相同幂等键返回同job且目标版本未变；同键换参数被拒。实际tar经独立Python读取，Windows不兼容名称、中文及链接完整保留。
- `execute-binary-transport.json`定位并修复二进制通道：65536字节样本经shell变为65792字节，经exec-out保持65536且SHA一致。原下载因output_limit拒绝提交，失败记录保留；`execute-resume-binary-fix-acceptance.json`证明更换会话后明确续作，17MiB与空目录最终核验通过。另一次冷启动后元数据工具退出255也保留，后续明确续作成功，未把失败请求重标成功。
- `transfer-execute-full.trx`：323项非实机通过，包括幂等意图/任务身份、截断账本恢复、执行归属失效、并发修改目标拒绝和本地备份提交恢复。实际App读取、多作用域和完整故障矩阵仍需补验，不能由UID/mode或测试数量代替。

600MiB实验未完成：显式取消后按暂存前缀续传，账本到629145600字节并进入committing；随后既有运行期保护在余量1233MiB时保存停机，任务cancelled。计划802c31de47634eb893b100a254a3e883及其账本/暂存保留，**不能标记最终提交或完整大文件往返通过**。前一次保护805MiB在计划阶段取消，见execute-memory-protection.json；本次见large-memory-protection.json。构建后台正常关闭、之后构建不复用服务，未改产品内存配置或保护阈值。用户释放内存后成功做过本批基本实测，资源再次波动时保留待验状态，没有关闭用户的其他程序。

尚待：600MiB提交/下载及恢复完整核验，其他冲突策略/并发/回退/工具清理、两个应用及多作用域实读、GUI/多用户/旧命令统一、完整发布与覆盖安装。生产程序未更新，B–E仍未完成。

### C1：通用文件页与恢复入口（2026-09-20，实际GUI仍待验）

源码文件页已切换共享后台：安卓用户和应用搜索选择、真实根目录树、路径/面包屑/后退与上级导航、分页、多选、文件与文件夹上传下载、拖放、应用数据根导出、冲突与停应用预览、取消和明确续作。新增files.transfer.list读取持久计划卡片；session.summary提供最近传输和恢复请求，停止时可查看记录。GUI实际点击/键盘/选择器尚未验收；用户明确要求暂时只做后台与CLI，C整项不得勾选。

- `file-workspace-full.trx`：339项非实机通过，包含新文件页的用户/应用切换、过期回复丢弃、版本引用、多选/分页、取消归属和原计划续作，以及只读传输中断恢复的限定条件。完整构建零警告错误。离屏文件页与预览布局检查发现并修复资源模板引用及directory对象误当路径字符串的问题；离屏渲染不能替代实际GUI验收。
- `transfer-large-final-acceptance.json`：原600MiB计划802c31de47634eb893b100a254a3e883明确续作后完成提交，下载计划28a46f2321e543109ea2f587e4a142c5也通过。源/安卓/回传SHA-256均为987523e7780392e283b404990c4e84e580bc75c451138b0c86c4f81c296eeebe。保留原取消/内存保护记录，没有重建上传计划或抹去失败历史。
- `transfer-scope-read-acceptance.json`：独立测试应用的私有、外部应用数据及共享目录完成双向复制；新建/替换6值均由该App以UID10213实际读取。此结果只覆盖该测试应用，不能替代两个无关应用的完整验收。
- `transfer-policies-acceptance.json`：skip/keep-both/overwrite及文件与目录类型冲突通过；覆盖备份读出原值，父目录自动改名后子文件保持结构。源或目标在计划后变化均以plan_stale拒绝，目标未被旧计划覆盖。完整回退、空间不足和guest取消清理仍待验。
- `workspace-summary-live`、`workspace-summary-recovery-live`、`workspace-history-stopped`：实机最近传输、明确恢复请求与停机后23条历史记录可读。读取不会自动执行。

重复元数据故障保留在f3210532…、0babf4d8…请求及metadata-repeat-full-logcat中：工具255且stdout/stderr为空，guest元数据程序exit0后adbd记录write failed/offline。日志也有安卓低内存回收，但尚未证明因果关系；连续12轮独立元数据读取正常。现仅对有完整空输出/255证据的只读目录元数据读取，核验同一实例及ADB在线后重读一次，保留原工具证据并报告retry阶段；写入不自动重试，不调整内存或镜像/驱动。故障自动恢复分支已做限定条件测试，实际自然故障触发该新分支仍待观察。

B2b CI [35493701811](https://github.com/Iviesever/rooted-android-game-vm/actions/runs/35493701811)已success。C1提交130457c对应的 [CI 35496244492](https://github.com/Iviesever/rooted-android-game-vm/actions/runs/35496244492) 已completed/success（交接时复核）。旧files.*适配、第二无关应用/多用户、完整D矩阵、实际GUI及新候选E交付仍未完成；未覆盖生产程序或公开发布。

### B3：旧文件命令接入共享服务（2026-09-20，局部验收完成）

从6c012bd接续。files.list/push/pull/export/diff/sync移除独立执行路径，统一使用已发现根、持久计划、分块执行及账本。保留原name/details、remote/local/sha256/backup、directory/dataDirectory/includesPrivateData、deleted/results字段与语义；shared仍从Download开始，sync上传目录内容并保留目标额外文件，diff不写目标。source.targetName支持精确目标名；上传createParents将缺失目录按父先子后放入同一计划，执行前不创建目标。兼容结果和进度提供planId，可经原inspect/resume明确恢复。旧计划仍可读，目录提交回执丢失后续作合并已存在目录，不重放目录替换。

- `compat-live-acceptance.json`：private/external/shared三作用域的指定文件名双向传输、缺失父目录、只读diff、目录内容同步、空目录、目标额外文件保持、电脑备份和安卓覆盖备份原值读取、目录导出内容及SHA通过。私有样本仍为dev.rgvm.acceptance.readfixture，权限10213:10213:600；这批不另称App实读或两个无关应用已通过。
- `rgvm-compat-2ce8112792-all-4107.summary.json`：旧files.list完整返回既有4107项，未重建目录；`compat-list-pages-acceptance.json`补external/shared显式两页及旧字段核对。600MiB旧证据复用，未重传。
- `compat-cancel-acceptance.json`：计划220066ab689a4926bb8733691b0e2198在部分目录已提交后显式取消，任务30455db2779dd277fd073bff59492370终态cancelled；同一计划明确resume后完成，8个1MiB文件回传SHA一致，空目录保持。该证据不替代guest进程清理、断连和超时矩阵。
- `compat-final-full.trx`通过348项非实机回归；完整构建零警告错误，最终format verify与git diff检查通过。实测候选broker路径及二进制SHA留在compat-broker-final-identity/compat-tested-binaries；最后增加的目录路径拒绝和能力字段由自动检查覆盖，未将源码测试称为安装版验收。

保留失败：首次启动成功后宿主余量跌到26MiB，既有保护正常保存停机（compat-memory-stop-status）；余量恢复约4.7GiB后确认原任务终态及无QEMU，再启动成功。第一次完整旧列表请求44bdab3da9094392bda6a294f87ae047遇到工具255且双流为空，原证据保留，明确重读后4107项通过；没有扩大自动重试或宣称根治。验证后VM正常保存停机；旧生产GUI自动拉起旧broker，下一次实测仍需重新核实归属。

B整项仍缺第二无关应用等验收，A多用户/无特定游戏环境、D完整故障矩阵、C真实GUI及E新候选覆盖发布均未完成。用户只做后台CLI的限制继续有效，本批未操作GUI、未覆盖安装、未公开Release。提交及对应CI结果记录在本机同一台账，不沿用上一提交CI。

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
