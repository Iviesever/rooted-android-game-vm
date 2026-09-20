# Review 剩余优化执行证据

目标：[review-remaining-goal.md](review-remaining-goal.md)。开始于683c443；用户的目标变更另由a51dabd提交。本文保留历史批次证据；**它们不代表2026-09-20新增通用化与文件管理要求已经通过**。

## 2026-09-20 A6/B5：多用户存储视图与完整后台流程

多用户实测先完成user10私有文件往返及App自身读取，外部规划却在lstat失败。现场证明维护进程的/storage/emulated属于user0的FUSE视图；Android已挂载的/mnt/user/10/emulated/10可正常访问目标用户卷。修复在逻辑卷不可访问时，核实当前挂载表的同用户同卷FUSE挂载、目录访问和无链接条件后返回accessPath；主机再次约束其用户/卷对应关系。displayPath仍为应用使用的逻辑路径，rootRef.Volume与初始化计划锚点仍保存逻辑卷身份，实际I/O使用重新解析的访问路径。没有改挂载、权限策略、内存或系统镜像，也不转到/data/media下层绕过FUSE。

multiuser-mapping-acceptance.json（run ff2934a2，最终尝试13646fe5）证明无特定游戏的QA用户10在private/external/shared三作用域上传不同内容到与user0相同的相对路径，App用户10的FileProvider准确读出预期值，下载字节一致，user0三个原SHA全部保持。private/external实际UID1010211，shared为该用户的MediaProvider UID1010200。私有部分跨修复冷启后只复核保留内容和旧引用拒绝，没有重放已完成上传。QA四包清单保持，元数据夹具不计作独立功能应用。

multiuser-boundaries-acceptance.json验证user10引用不能换成user0逻辑卷或直接指定维护访问路径。正常停止QA用户后，交互用户仍0，凭据保护目录返回data_locked；设备保护目录仍可上传及下载，UID1010211/mode600/SHA一致。设备保护项是后台文件验收，未声明停止中的App已读取。测试最初误要求stop-user打印Success，实际命令exit0但stdout为空；独立活动用户状态及users.list确认已停止后继续，没有重复停止。

保留原失败：首次多用户尝试只读清理超时并触发宿主保护正常停机；后来资源恢复后才继续。视图修复前外部计划c2c98f2925164e6bb37be545b1b46c9a以permission_denied失败且无外部写入，相关挂载/访问探针及逐项传输计划留在本机。主用户正式应用及原测试文件未因跨用户传输改变。

7项用户存储视图边界回归，共397非实机通过；完整构建零警告错误，DEX源码重建一致（de25c1801183a25b7a2e643bf465468d41be1d69814d2d455e99dc3dfd940219）。A/B列明的后台应用与用户文件流程已有直接证据；剩余D组合正在按本机remaining-backend-matrix.md补验，C真实GUI与E最终安装发布仍未完成。精确提交、CI及当前运行状态见本机continuation.md顶部。

## 2026-09-20 B4/A：应用根准备、权限与第二应用证据

files.roots为未生成、已解锁且所属卷可写的external/obb/media根提供creatable；createParents把缺失应用根与父目录纳入既有只读计划和逐项账本。原应用/用户/安装身份始终保留，内部卷锚点仅允许该应用路径及计划声明的准备祖先；执行/续作重新解析。private/device-private不自行mkdir，锁定和错误用户请求仍拒绝。CLI帮助与使用文档同步；GUI缺失根入口仍待C批，未冒充界面验收。

首次真实初始化发现App无法读取：新根去掉setgid后子项成了0:0。修复以当前应用UID请求新增/替换项属主，从实际父目录继承GID和目录setgid，并在chown之后设置最终mode；合并已有目录不批量改权限，共享准备祖先不归App所有。目录与文件的实际权限来自设备回执。媒体根实测归MediaProvider UID10200，App可正常读取；[AOSP FUSE实现](https://android.googlesource.com/platform/packages/providers/MediaProvider/+/refs/heads/main/jni/FuseDaemon.cpp)明确不实现普通路径的chmod/chown，因此不承诺所有卷都呈现请求的应用UID，也不改底层卷绕过其规则。

- Material Files（真实非游戏应用）private完整流程见material-private-accepted-1584dc85.json；external/shared见material-scopes-users-0-external-shared-acceptance.json，run e13b21f8。中文/空格嵌套目录、空目录、新建和覆盖、App自身FileProvider读取、原文件备份、下载SHA全部通过。外部首次根由产品计划创建，规划阶段确无写入；新外部文件UID10211/mode660。
- material-extra-roots-acceptance.json验证OBB/media首次根创建、App实读和回传；OBB UID10211/GID1079，media UID10200/GID1023，目录均2770。媒体只读查询有一次自然ADB离线，恢复同一会话后明确cleanup清理，再只读重查及下载；未重放上传。初始测试将media属主一概断言为App UID的错误假设已按实际上下层文件属性、系统包UID与App读取证据纠正。
- readfixture-external-owner-acceptance.json的独立UID10213应用复验受影响的外部覆盖、文件权限、双向字节一致及六项应用自身读取；private/shared原值保持。结合先前三作用域往返证据，覆盖第二个独立功能应用，不把复制的元数据夹具算作第二应用。
- metadata-boundary-acceptance.json仅在用户10安装labelcollision/labelfallback无代码APK；两条相同本地化名称的条目拥有不同appRef并分别正确解析，空名称回退真实包名，PNG图标48像素。主用户的包清单及安装身份前后相同；用户10四个第三方包无Malody。多用户文件传输仍未完成。

原错误计划7fe4f67431414f3d9a56675fa97e23fd的完整8项测试树经目录集合与SHA核对后保存在同父目录唯一备份，material-failed-root-preserved.json记录原值；未删除数据或手工修权限冒充验收。后续使用新计划验证修复。内存/镜像/显卡及正式游戏资源未调整；未重复600MiB传输。

8项应用根边界回归，加上既有382项共390非实机通过。完整构建零警告错误，格式及Java源码重建通过；DEX为0221876cafbe6229197ba72f6902f20f9a154c7d0585e93e0efc792d495fed51。精确提交及CI见本机continuation.md顶部。以上为A/B局部证据；QA用户锁定/解锁后的完整数据流程、D剩余组合、C真实GUI、E新候选备份覆盖/装后/发布仍待完成。

## 2026-09-20 D5：独占调度与状态观察有界化

真实停止任务c6ed142a36a34231a65e8c7b2b486c90在queued超时，请求目录只有request.json，同期状态ADB在读取getprop/dumpsys。原broker只在独占操作进入后拦截新读取，等待期间仍可被连续轮询越过。新增读/独占准入门：等待中的独占操作阻止后来的读取，已有操作退出后进入；取消等待会恢复准入，释放一次不会影响其他操作。session.summary在独占等待或运行期间都可返回OperationInProgress。

status使用10秒安卓观察预算。超时且产品进程仍存在时返回Unreachable、实例/session、观察时间、timeout和可用的原工具证据，不补造boot/root/前台状态，也不把实例当成已停止。外部调用者取消保持cancelled。status-deadline-live.txt在原迟滞guest上通过源码Core API实测10.13秒返回Unreachable；status-caller-cancellation-live.txt在0.14秒保留cancelled。这两项是源码Core探针，不是安装CLI验收。

qa-exec-normal-stop.json与qa-normal-stop-verified.json证明原guest真实sync成功后emu kill返回OK、QEMU退出，未强杀。随后核实新源码broker路径并冷启，Root正常，QA用户10保持锁定；三条旧pending明确清理为session_ended/killSent:false（qa-old-session-recovery.json）。新源码CLI在启动期间0.10秒返回OperationInProgress；两路status轮询期间stop任务fd2ecb06802943058c465e1cc090f424成功（broker-admission-live.json）。原超时/权限失败证据均保留。

4项确定性并发回归覆盖20个后续读取不能越过stop、取消等待、读取消不解除独占、重复释放不解除其他读者。完整构建零警告错误、格式验证通过；本机389项非实机通过，其中7项属于仍未提交的应用根准备代码，当前调度批次按已提交基线计算为382项。首次使用不同深度输出目录导致测试定位源码失败并误包含需显式环境的CleanE2E，门禁拒绝后没有安装；修正运行位置和过滤器后通过，失败日志保留，未修改测试来掩盖失败。

Material Files私有三层目录及特殊名称的新建/覆盖/App实读/原备份/下载SHA与空目录已通过。外部初始化plan7fe4f67431414f3d9a56675fa97e23fd虽成功提交，但Provider报Permission denied：新根0:1078:770丢失setgid，后续项0:0。缺失根与外部权限修复仍在工作树，未提交、未通过完整实机；A/B多用户、元数据、C/E及其余边界仍待完成。内存配置和正式游戏资源保持。

## 2026-09-20 A/B最新局部证据（未整体验收）

源码基线9335afa及CI35510719019已通过；安装仍是旧0.5.0.0。Material Files 1.7.4（UID10211）的私有测试文件经核心计划上传，再通过该应用自己的FileProvider读取原值，证据material-provider-probe.json。没有操作Windows GUI；一次私有文件实读不等于三作用域双向流程通过。

已创建一个QA用户10，名称RGVM QA 446f56bc。qa-user-apps.json证明仅安装Material Files与readfixture两个第三方应用，无Malody；UID分别为1010211与1010213。qa-locked-boundaries.json证明锁定私有数据返回data_locked，user10的appRef混用userId0被拒绝。后台start-user成功后users.list观察到用户10解锁，交互用户仍为0。主用户正式游戏未卸载。

Material Files的external/obb/media根真实尚未生成，不能用手工mkdir替代产品能力验收。本机工作树补了显式createParents规划：以该用户已核实的卷为内部锚点，只允许所选应用前缀及计划列明的准备目录，保留应用安装身份；private/device-private不自行创建。新增7项边界测试，总385项非实机通过、完整构建零警告错误。该代码尚未完成实机验证、未提交或安装；GUI缺失根入口也仍待C批处理。

用户10后台启动后，guest逐渐无法及时执行命令。qa-runtime-readonly.txt观察到MemAvailable为0、负载约82；这只是现场证据，未认定所有瞬时故障的根因，也未调整内存/镜像/显卡或关闭其他宿主程序。原apps.list任务a42e4e01785f403f9d1024c1dbe83099已明确cancelled，尚无本次完整传输写入。工具清理记录仍pending；停止测试用户未获确认，正常保存停止任务d6255586d0b74659b27bfc413d79d64e明确timed_out，随后一次正常Android重启请求观察超时。QEMU是否退出或guest是否重启须按本机continuation.md最新段和当前进程核实，不能从客户端超时推断。未强杀QEMU或猜测杀Android进程。

同名/空名称两个无代码APK夹具已在本机签名构建，尚未安装。A/B完整双向、多用户隔离及元数据边界、D剩余组合、C真实GUI和E最终备份覆盖安装仍未完成。CLI文档同步了B3/D1–D4已有实现与验收边界，去掉600MiB与旧接口仍未通过等过时说明。

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

### D1：文件工具归属、真实guest清理与明确恢复（2026-09-20，D整体仍未完成）

从1efea84接续。文件命令及apps.list/apps.resolve/users.list使用请求级guest租约：唯一初始环境token、root控制的启动门、持久实例/运行会话、PID及启动时间、清理回执。完成或异常时先关闭启动门，再定向TERM/KILL并复验；不能读取的进程元数据或不可用通道保持pending。文件传输仅在清理已核实时标整批成功，原错误/工具双流/逐项账本保留；错误增加guestCleanupPath，结果/执行头增加guestToolToken和清理记录路径。files.tools.list/cleanup及原计划resume提供明确恢复，其他实例拒绝，旧VM会话结束不向新会话发送kill。嵌套结束路径不会自动重复一次未核实的清理。

- `guest-tools-fault-acceptance.json`：真实文件规划任务中独立暂停所属sh/main，cancel、timeout、CLI客户端退出后明确cancel均核对guest PID/启动时间并回收；不同token的隔离进程保持存活，关闭后延迟启动被拒。客户端退出本身不自动取消有归属的后台文件任务。
- `guest-transport-recovery-acceptance.json`：仅暂停本VM adbd，由独立且核对启动时间的watchdog恢复；独立记录证实adbd确为T状态。任务结束后清理保持pending，连接恢复前不报clean，恢复同一会话后未自动重放写入；明确cleanup/resume后9MiB文件SHA一致。这是受控ADB通道不可用验证，不宣称覆盖任意网络/设备离线原因。
- `guest-transfer-rollback-acceptance.json`：9MiB二进制跨两块上传/下载、正常路径清理、旧备份实读、通过新计划回退并保留替换版本通过；UID/GID10213、mode600及app_data_file SELinux标签核对。该批未另称App实读新样本。
- `active-export-acceptance.json`：活跃App默认一致性导出被app_running拒绝且目标未创建；显式live保留原PID并标live；显式stopApplications停止后导出，不自动重启App。
- `guest-session-acceptance.json`：在本次隔离故障记录上模拟丢失清理回执，原真实回执备份保留；正常保存/冷启动后旧session报告session_ended，清理请求未产生ADB/kill工具调用；伪造其他实例记录拒绝、guest未改且历史列表不包含它。
- `guest-tools-delivery-full.trx`：363项非实机通过；完整构建零警告错误、format verify与独立源码DEX重建通过，DEX为a1e0026e76f90a17c731a2b39835072597f2bf14457f77bdf6ab6dab7fa465c5。最新源码将工具历史列为只读请求，不阻塞在写任务锁后。提交与新CI精确结果存本机台账。

保留边界及下一批：D1仅覆盖文件/应用目录元数据的guest工具；显式shell/root-shell、logs/record/trace生命周期仍待收敛，不能据本批称所有调试工具已清理。`guest-baseline-acceptance.json`保留原root-shell超时后的sh/sleep残留证据，隔离进程已定向清理。第一次通道测试未及时捕获短命工具，原任务e79c07bbb975d03cfc90e1f389fd3413已核实succeeded；优化独立观察器后新隔离任务完成受控故障，未重复启动未明任务或重放原写入。

另外已复现容量缺陷：`space-volume-baseline.json`中8MiB隔离tmpfs目标被计划错误报告为根分区2027646976字节可用，未写目标；临时挂载已umount并验证。D2须修实际目标卷及目录/skip/resume/tar空间估算，不能把“空间不足”标通过。远端文件提交回执丢失后的BackupPath存在性/权限对账，以及A的多用户/第二应用、C真实GUI、E新版本备份覆盖发布仍待完成。VM已正常保存停机；未调整内存、镜像/驱动，未操作Windows GUI、未覆盖安装或公开发布。

### D2：真实目标卷、分阶段空间预算与续作抵扣（2026-09-20）

从0d174d0接续。规划与执行共用按卷的需求模型：按分配单元计文件/目录、分别计guest和host块暂存、归档缓存与完整归档输出，合并同卷不同阶段的峰值；宿主保持512MiB余量，VM潜在数据增长纳入宿主检查，分块写入前再检查宿主容量。执行按skip/same和已核验结果重算；不把账本offset当成实际已接收字节。新增spaceChecks/StorageBindings，执行头及结果指向spaceCheckPath，规划显示创建时间。查询最近存在的分配父目录，支持同一树中的嵌套挂载；guest用st_dev+statvfs.f_fsid识别同会话卷变化，host用实际目录句柄的卷GUID，也正确归并SUBST别名。旧计划缺少新增字段仍可读取，执行时重新计算。

- `space-capacity-subset-acceptance.json`：真实8MiB tmpfs目标被正确报告为8388608字节，9MiB源需要9441280字节；insufficient_space在写入前返回，目标不存在、执行账本无完成项。skip不为被跳过的9MiB文件索取空间且旧值保持；空目录在小卷正常创建；目的地根在大卷、子目录在8MiB挂载时也按实际子卷拒绝。
- `space-live-acceptance.json`：补强后的卷变化拒绝、重新规划成功；归档缓存D盘和目标C盘分别列预算，实际9MiB tar及空目录经独立Python tar读取/散列核对。保留完整归档副本并注入提交前中断记录，明确resume复用已核验缓存；没有块传输需求，但最终归档仍保留完整空间需求，重建归档SHA一致。
- `space-prefix-acceptance.json`：在16MiB隔离目标准备真实8MiB暂存前缀并故意把故障回执offset写成9MiB。服务只抵扣实际核验的8MiB，所需增长1048576字节，补齐后9MiB SHA一致；不符前缀以staging_mismatch拒绝且目标未创建。故障回执注入明确记录，不宣称是自然断电。
- `space-final-full.trx`通过371项非实机回归，包括分卷/同卷峰值、tar缓存不能抵扣最终归档、跳过目录子树、无验证offset不获抵扣、真实本机容量/物理暂存占用及SUBST归并；完整构建零警告错误、format verify和独立DEX重建通过。DEX为fb1061270d42ef4074157da2713fd0ea15e23f76e78f1cc6ba4ee82e5155f910。

保留失败及修正依据：第一版只绑定st_dev，tmpfs重挂后设备号甚至mountinfo ID均被复用，旧计划在隔离新卷被错误接受；该挂载已卸载，原请求与失败断言保留。`space-filesystem-generation.json`独立观测相同device=71/mount ID12850而fsid不同，随后加fsid并完成真实拒绝验收。上述容量子集不重复传输；未重做600MiB。所有隔离tmpfs已卸载核实，最后VM正常保存停机；最终增加的结果路径字段和本机卷句柄解析由自动检查覆盖。

D整体仍缺显式shell/诊断命令guest生命周期、远端提交回执丢失后的完整字段对账及剩余组合；A/B的第二无关应用/多用户、C真实GUI、E新版本备份覆盖/装后/发布继续未完成。未改内存配置、镜像或驱动，未操作Windows GUI，未覆盖生产程序或公开发布。

### D3/D4：诊断与前台Shell生命周期、丢失提交回执对账（2026-09-20）

从e694675接续。logs区分时长到期、显式取消和工具提前退出，保存受限原始stdout、过滤事件和stderr/退出依据。record/trace接入同一工具租约；录屏先保存并核验本机副本才删除guest文件，冲突/未清理资源保持pending并阻止同类采集接管。trace核验内核tracing_on，已有追踪拒绝接管。tools.list/cleanup是通用入口，旧files.tools别名保留，session.summary提供精确恢复请求。部分二进制诊断输出在取消时保留，不伪装完整产物。

shell/root-shell作为有界前台请求，UID2000/UID0保持。独立会话中的监督进程保留token和组身份，清理先核验监督者PID/启动时间，再处理所属组中清空环境变量的子进程；未核实的残留保持pending。HUP忽略保证ADB客户端结束不会先移除该监督者。公开的桥接DEX只允许Root写，普通shell可读，未放宽应用数据权限。此契约不是任意恶意Root代码或主动脱离会话并抹去归属程序的沙箱，也不回滚脚本刻意改变的系统服务状态。

提交回执恢复按真实备份状态对账：新建不再返回虚构BackupPath，TemporaryPath清空，remote Permissions来自实际目标；缺失/变化/未知备份不标完整恢复。目录备份核对类型与根元数据，不能外推为任意目录深层内容永久未变。

- `diagnostics-live-acceptance.json`：日志时长结束succeeded、实际cancel为cancelled、真实logcat提前退出failed；正常录屏3.224秒及取消后1.353秒片段经ffprobe核对视频流/无音频，未输出图像。正常/取消trace都独立读到tracing_on=0，已有外部trace保持运行且新请求busy。
- `diagnostic-resource-recovery-acceptance.json`：注入本机产物同名冲突，进程已清但资源保持pending且clean=false；本机原文件与guest视频都保留，新录屏被拒；摘要指向原token清理，保留冲突文件后明确tools.cleanup成功，视频经ffprobe核对。
- `shell-supervisor-acceptance.json`：普通/root身份保持，env -i子进程真实存在于登记组；超时后核对整个组无活跃成员；重定向输出的普通后台子进程也在前台请求完成前回收。`diagnostics-final-acceptance.json`补原Shell退出7及双流原文保持、最终监督器后的logs/trace/record与文件根回归。
- `commit-receipt-acceptance.json`：在隔离记录上保留原回执并模拟丢失提交终态；新建、覆盖、备份缺失/篡改、目录类型替换、本机新建/覆盖下载七类通过。已提交目标version/SHA未变化；缺失/坏备份拒绝，恢复原备份后可明确继续；原值实读和实际权限字段核对。
- `diagnostics-delivery-full.trx`：378项非实机、完整构建零警告错误、format verify、源码DEX重建通过；DEX d9ad64dc96ef780bd8f72732887378468aac1d338e3947468ff2a6f70e1280bc。

原失败保留：首次Shell监督在挂断后失去leader，正确pending且未猜测kill；工具号cb9b31b1877c4ead8dac06caa645c336在隔离sleep自然结束后已明确cleanup核实，后续忽略HUP的新案例通过。录屏验收脚本一度把尚未登记的resources:null当数组；原60秒任务048e7d8398ce4368b11efb9fa12c66ff已核实succeeded，不是待取消任务。回执首例最终只读检查请求715a36763ddf4e978d51fb6decab0d23出现自然255空流并进入只读重试分支，但ADB在线核验失败；请求如实失败，之后同会话只读重查版本/SHA一致，没有重放写入，未宣称自然故障根治。

VM最后正常保存停机，未操作Windows GUI、未覆盖生产程序或公开发布。A/B第二无关应用、多用户及无特定游戏环境、剩余边界组合、C真实GUI、E新版本备份覆盖/装后/发布仍待完成；以上证据不等于A–E整体通过。

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
