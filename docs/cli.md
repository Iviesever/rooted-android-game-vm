# JSON CLI

安装后，`RootedAndroidGameVM.Cli.exe` 与图形启动器位于同一目录。GUI 与 CLI 使用同一个当前用户协调进程；无需安装服务或配置 MCP。

```powershell
$vm = "$env:LOCALAPPDATA\Programs\RootedAndroidGameVM\RootedAndroidGameVM.Cli.exe"
& $vm status
& $vm capabilities
& $vm schema
& $vm runtime.inspect
& $vm start --wait
& $vm screen
& $vm checkpoint.create --wait
```

普通响应为一行 JSON，`schemaVersion` 为 1，`ok` 表示该层操作是否成功。长操作先返回 `jobId`；任务真正的结果在后续 `job` 响应的 `result` 内。`--wait` 持续输出 NDJSON 状态，直到任务结束。诊断文本写入 stderr。

请求可附 `requestId`（1–128个字母、数字、下划线、点、冒号或连字符），省略时生成。响应回传该ID，长任务的最终结果、任务摘要和工具记录保持原请求ID与jobId；轮询请求有自己的ID。requestId用于关联，不是自动去重键；导入续作用importId。`schema`保留旧commands列表，另提供request/response JSON Schema及jobStates。

终态为 `succeeded`、`failed`、`cancelled`、`timed_out`、`interrupted`，接受任务时不提前给终态；查询请求自身成功与被查询任务成功是两层含义。`stage`、`session`、已观察的App `pid` 和 `artifactDirectory`描述实际证据；无法观察的字段省略。`error.stage`保留原失败阶段，`evidencePath`指阶段记录，`toolEvidencePath`指工具记录。长任务文本工具的stdout/stderr分别落盘，记录工具PID、退出码、完整性、取消与回收结果；大于64KiB的进度细节也改用文件引用。普通状态/预览成功轮询不持续复制工具输出，失败时仍留诊断。

任务归属索引持久化在 `debug-runs/jobs/`。协调进程重启后可查询近期原jobId；未提交明确终态的任务报告interrupted并保留原broker身份、阶段、产物和恢复提示，**不自动重放操作**。CLI取消时有界等待任务清理；无法确认时报告cancellation_unconfirmed，不能把取消请求已发送当成清理通过。

`launch` 发送一次启动指令后，会在最多30秒内等待 PID 出现，避免较慢启动时立即 `pidof` 返回1造成误报。返回 `stage:process_observed` 与 `interactiveReady:false` 只证明当时观察到进程，页面就绪和后续是否被低内存终止须另行核验。超时返回 `app_not_running`，取消与设备错误保留原类别。

`launch`增加 `waitForActivity:true`，再有界等待前台resumed Activity；`app.observe`保存一次进程、前台、Activity观察及时间。即便activity_ready也保留interactiveReady:false，不能据此确认Unity页面可操作。启动工具非零退出时保存双流依据，再核查同一App进程；观察到进程时保留launchDiagnostic，不掩盖工具异常。

内存审计版本：完成任务的完整 `DebugReply` 存到 `debug-runs/job-results/<jobId>.json`；`jobs` 只返回摘要与路径，单个 `job` 对不超过 1 MiB 的结果保持内联。更大的结果返回 `{resultPath,resultBytes,inline:false}`，读取该本地 JSON 才是完整结果。失败仍保持 `ok:false` 与错误码。最多 16 个未完成任务、约 128 份完成任务索引；裁剪索引不删除证据文件。管道请求上限为 1 MiB，响应帧上限仍为 16 MiB。批量 `test` 的每步输出改存 `step-0000.json` 等文件，步骤索引给出 `resultPath`，避免在内存和每次响应中叠加全部输出。

即使是立即返回的诊断响应，超过16MiB管道帧时也复用相同文件引用契约，保留完整原始响应及身份，不再以断管丢失结果。工具记录的stdoutComplete/stderrComplete为false时不能当成完整输出；生命周期取消期间未收齐的流会明确标记。

推荐用请求文件，避免 PowerShell 引号转义：

```json
{
  "schemaVersion": 1,
  "command": "launch",
  "arguments": { "package": "test.app", "waitForActivity": true }
}
```

```powershell
& $vm --request .\request.json --wait
```

## 常用请求

下面展示 `command` 和 `arguments`。应用操作必须显式指定 package；缺少目标返回 app_required，不选择默认应用。先用 apps 发现已安装包。session.summary 不指定应用时只显示实例摘要；共享文件操作无需提供包名。

| command | arguments 示例 | 用途 |
|---|---|---|
| `status` / `capabilities` | `{}` | 状态、协议能力 |
| `session.summary` | `{"package":"test.app","refresh":true}` | GUI/CLI共用的会话、任务、核验、触点与恢复摘要 |
| `app.page.annotate` | `{"package":"test.app","observation":"截图id","page":"文档列表"}` | 将调用者观察到的页面关联到新截图 |
| `memory.snapshot` | `{}` | 宿主余量与经过路径/PID/启动时间核验的进程 WS、私有提交、历史峰值 |
| `start` / `stop` | `{}` | 启动或 sync 后停止 |
| `apps` | `{}` | 第三方应用列表 |
| `apps.list` | `{"userId":0,"query":"名称或包名","includeSystem":false,"includeIcons":true,"pageSize":100}` | 真实应用元数据、引用和分页 |
| `apps.resolve` | `{"appRef":"apps.list返回的引用"}` | 重新核对实例、用户和安装身份 |
| `users.list` | `{}` | Android用户及解锁状态 |
| `files.roots` | `{"appRef":"应用引用"}` 或 `{"userId":0}` | 应用数据根及共享卷；无appRef时仅返回共享卷 |
| `files.browse` | `{"rootRef":"根引用","relativePath":"files","pageSize":200}` | 批量结构化目录页；也可仅指定目录entryRef |
| `files.stat` | `{"entryRef":"条目引用","hash":true}` | 验证条目当前身份，可选计算普通文件SHA-256 |
| `files.transfer.plan` | `{"direction":"download","sources":[{"entryRef":"条目引用"}],"destination":{"localDirectory":"D:\\Exports"}}` | 只读扫描并保存多选传输计划 |
| `files.transfer.inspect` | `{"planId":"计划编号","offset":0,"pageSize":100}` | 分页查询持久计划，安卓停止时也可读取 |
| `apk.inspect` | `{"path":"D:\\app.apk"}` | 检查包名、版本、ABI |
| `install` | `{"path":"D:\\app.apk"}` | 保留数据安装/升级 |
| `launch` / `force-stop` | `{"package":"test.app"}` | 启动/停止指定应用 |
| `screen` / `wake` / `release` | `{}` | 截图、唤醒验证、取消并释放触点 |
| `key` | `{"key":"KEYCODE_BACK"}` | 安卓按键 |
| `clipboard` | `{"text":"测试文字"}` | 写剪贴板；不传 text 则读取 |
| `logs` | `{"package":"test.app","seconds":60}` | 持续日志；路径见任务 progress.directory |
| `metrics` | `{"package":"test.app"}` | 可用性能诊断及原始依据 |
| `record` / `trace` | `{"seconds":15}` | 无音频录像 / 系统追踪 |
| `files.list` | `{"scope":"external","package":"test.app","remote":"files"}` | 列目录 |
| `files.pull` / `files.push` | `{"scope":"external","package":"test.app","remote":"files/a.bin","local":"D:\\a.bin"}` | 双向文件传输 |
| `files.diff` / `files.sync` | `{"scope":"external","package":"test.app","remote":"files/qa","local":"D:\\qa"}` | 比较/不删除式同步 |
| `checkpoint.list` / `checkpoint.create` | `{}` | 列表 / 停机保存 |
| `checkpoint.restore` | `{"id":"检查点编号"}` | 校验、恢复与冷启动 |
| `checkpoint.recover` | `{}` | 恢复中断的磁盘切换 |
| `shell` / `root-shell` | `{"script":"id","timeoutSeconds":10}` | 显式调试 Shell |
| `jobs` / `job` / `cancel` | `{}` 或 `{"id":"任务编号"}` | 任务列表、查询、取消 |
| `quiesce` / `shutdown` | `{}` | 停止调试任务 / 安卓已停止时退出协调进程 |
| `licenses` | `{}` | 随程序附带的第三方库许可证 |

所有 ADB 操作都固定到经过检查的产品实例。没有“自动选第一个设备”的逻辑，也不调用全局 `adb kill-server`。

`apps.list` 返回 `instanceId/session/userId/locale/observedAt/entries/total/snapshotId`，有后续页时返回 `nextCursor`；下一请求保持同一查询条件并传入 `cursor`。`pageSize`为1–200，缓存最多30秒，`refresh:true`重新读取并令旧分页游标失效。条目包含真实`name/nameSource`、`package`、`appRef`、UID、版本、安装修订、进程观察及数据根。`includeIcons:true`把48px PNG保存为本机`iconPath`，不在结果中返回大段图片编码；名称不可用时保留包名，进程不可观测时给出`runningStateError`。

应用引用绑定持久实例、Android用户和安装修订；更新/重装后旧引用会在`apps.resolve`返回`stale_reference`，卸载返回`app_not_found`。它不是权限凭证。旧`apps`保持包名数组；目前旧`launch/files.*`仍接收显式package，新文件计划接口将继续接入应用引用，不能将本批引用解析误认为所有旧命令已经校验引用。GUI本批列出主用户0的应用，多用户文件映射仍属后续批次。

`files.roots`分别给出private、device-private、external、obb、media和shared的实际位置、存在/访问/写入/锁定状态及原因，不把未生成的目录当作空目录。根引用由当前应用安装身份、Android用户和实际可见存储卷重新解析，不接受任意绝对路径作为根。

`files.browse`返回`directory/entries/total/nextCursor/snapshot/observedAt`，每个条目包含名称、相对路径、类型、大小、时间、UID/GID、mode、版本及entryRef。pageSize为1–500，单目录上限100000项；超限明确失败，不静默截断。使用nextCursor时保持同一目录，变更返回stale_cursor；entryRef对应文件被替换/修改，或虚拟机重启后使用旧根，返回stale_reference。rootRef加relativePath可请求重新观察当前路径。链接只返回元数据，不跟随链接浏览或计算散列。

`files.transfer.plan`支持多选文件/文件夹，下载来源可使用entryRef或rootRef+relativePath；上传来源使用绝对localPath，上传目标使用目录entryRef或rootRef+relativePath。默认保留所选顶层文件夹名称与空目录，contentsOnly:true仅复制其内容。计划保存到debug-runs/transfers/<planId>/plan.json；包含源SHA/版本/权限、目标观察、冲突、空间估算和需停止的应用。相同文件计为same，目录合并为merge，同名差异与类型冲突分别为different/type_conflict；源间目标重名也会阻止执行，不依赖选择顺序覆盖。

Windows无法原样落地的名称会在directory计划中列出问题。下载format:tar的计划保留这些名称和链接元数据，不跟随链接读取；实际归档写出仍待执行器实现。私有写入要求consistency:stopped-app；私有导出默认该模式，可显式选择live并保持一致性未验证。生成计划不停止应用、不创建目标目录、不复制任何目标内容；status:planned和transferVerified:false必须与传输成功区分。inspect的pageSize为1–500，nextOffset为空表示结束。

当前尚未提供files.transfer.start或resume；执行、GUI对新根/多用户的完整接入及旧文件命令适配仍待后续批次，不能据此认为双向文件管理已全部交付。

`session.summary`返回runtime与summary，`summary.text`就是GUI展开“会话摘要”显示的同一份文字。它列出实例、App/PID、近期任务阶段、最近传输核验、触点释放依据、产物目录、可恢复点与下一步。启动/停机/恢复期间仍可返回任务进度，安卓状态标为OperationInProgress，不等独占操作结束才显示。ADB不可用但产品进程仍在时为Unreachable，不能误当已停机。

App结构化观察最多缓存10秒，返回原observedAt；refresh:true强制更新该观察，不自动截图或推断应用页面。先用screen观察，再用app.page.annotate记录1–120个可显示字符的页面说明，同时指定目标package。记录标明是调用者截图标注，附截图路径、采集时间、App PID及会话；只接受30秒内、相同目标且未执行后续操作的观察。摘要对过期、后续操作、进程/会话或前台变化标superseded；其他应用的标注不会混入当前应用摘要。

触点计数是本工具账本；acknowledged仅表示释放RPC已确认，游戏实际状态需要独立验收。释放未确认会保留unverified及错误依据；输入结束不能在清理未确认时报告成功。每次发送和释放均绑定原VM会话，不能把旧序列续发给重启后的实例；新协调进程在确认资源归属后先清理可能遗留的输入，失败时保持未验证。

通用核心已移除旧Malody专属命令、默认包名和导入恢复按钮。旧任务与本地证据保留可查，但不会自动重放或声明可续作；旧malody.*请求不再出现在capabilities/schema中。需要专属业务导入时由应用自身操作；未来如提供自动化扩展，必须通过独立插件契约。文件传输成功不等于应用完成导入或可以运行。

`files.push` 的私有文件归属目标App，默认600；应用外部目录文件660；共享下载默认644。替换私有/共享文件保留合理读写位，去除执行和全局写入位。结果列出实际 `permissions`；`applicationReadVerified:false` 表示传输本身没有代替目标App执行读取验收。

0.4.0 增加以下请求，完整参数以 `schema` 为准：

| command | arguments 示例 | 用途 |
|---|---|---|
| `runtime.inspect` | `{}` | 请求配置、实际显示、宿主内存；停机时附启动余量检查 |
| `runtime.configure` | `{"profile":{"renderer":"host","width":1920,"height":1080,"density":240,"refreshRate":120,"memoryMb":3072,"cpuCores":4,"desktopDisplay":true}}` | 停机后保存；下次启动生效 |
| `frames.sample` | `{"package":"test.app","seconds":30}` | 实际呈现帧率、间隔分布和采样覆盖 |
| `files.export` | `{"package":"test.app","scope":"private","remote":"files/qa","local":"D:\\exports"}` | 导出指定目录，核验并受控解包 |
| `uninstall` | `{"package":"test.app","confirm":true}` | 卸载普通第三方应用；删除该应用数据 |
| `window.focus` | `{}` | 打开已经验证的产品安卓窗口 |

`status.hostMemory` 报告宿主物理余量与提交余量；`memoryProtection` 在警告或自动停机时给出原因。`start` 可能返回 `host_memory_low`，请处理容量不足后重试，不循环强行启动。`preview` 是 GUI 的二进制管道协议：JSON 元数据帧后紧跟 PNG 帧；命令行取证请使用 `screen`，不要把元数据当成已经收到 PNG。

`runtime.configure.profile.startAvailableMb` 为启动物理余量门槛：省略或 `0` 保持自动估计；`4096` 表示可用物理内存至少 4 GiB。提交空间、guest 占宿主至多一半、启动期与运行期低内存保护仍独立检查。门槛不是内存配额、占用预测或节省量。运行设置界面也可修改；完整配置示例应保留原来的显示、GPU、guest 内存等字段。

自定义门槛最低支持 `2560`（2.5 GiB）。这只调整启动时的物理余量检查，不能绕过提交空间检查或运行期保护；默认 `0` 的自动策略不变。

`profile.lowRam=true` 显式启用固定模拟器的 `-lowram`，允许 768 MiB 起的实验配额；默认关闭。普通 API-35 模式会把较小请求自动提高（本机请求 1536 MiB 实际为 2560 MiB），因此单改 `memoryMb` 不能证明节省。`vmHeapMb` 保留安卓 VM 堆上限（128–576 MiB），模拟器还可能根据屏幕/API 再调整它。`runtime.inspect.observedMemory` 在运行时列出实际分配、guest 可见内存和 `allocationMatchesRequest`，均不等于产品总宿主 RAM。低内存模式是可测试配置，不自动宣称任意应用的 2 GB 总占用或性能保证。

跨起停连续审计使用 [MemoryProbe](../tools/RootedAndroidGameVM.MemoryProbe/README.md)。WS 包含共享页，私有提交不是独占物理 RAM；不要将多进程 WS、guest PSS 和整机差值混加。原始 `memory.snapshot` 无截图且不启动安卓。

## 六指同时按下，再独立松开

先执行 `screen`，取得新的 `id`、原始 PNG 尺寸和旋转。下面坐标仅为 **2400×1080 的示例**，需要按你的截图修改。

```json
{
  "command": "input",
  "arguments": {
    "observation": "替换成截图id",
    "frames": [
      {"atMs":0,"touches":[
        {"id":0,"x":300,"y":900,"pressure":1},
        {"id":1,"x":660,"y":900,"pressure":1},
        {"id":2,"x":1020,"y":900,"pressure":1},
        {"id":3,"x":1380,"y":900,"pressure":1},
        {"id":4,"x":1740,"y":900,"pressure":1},
        {"id":5,"x":2100,"y":900,"pressure":1}]},
      {"atMs":500,"touches":[{"id":0,"x":300,"y":900,"pressure":0}]},
      {"atMs":650,"touches":[{"id":1,"x":660,"y":900,"pressure":0}]},
      {"atMs":800,"touches":[{"id":2,"x":1020,"y":900,"pressure":0}]},
      {"atMs":950,"touches":[{"id":3,"x":1380,"y":900,"pressure":0}]},
      {"atMs":1100,"touches":[{"id":4,"x":1740,"y":900,"pressure":0}]},
      {"atMs":1250,"touches":[{"id":5,"x":2100,"y":900,"pressure":0}]}
    ]
  }
}
```

用 `--wait` 运行这个请求。每个触点更新位置而保持 `pressure:1` 就是移动；不要创建新的 id 来代替同一个手指。序列最多 120 秒、10000 帧；最多十个触点。任务结束、取消或输入客户端租约过期会释放触点。协调进程异常退出时，重新连接后的首个输入也会清理所有产品触点；底层使用有限过期时间，不使用永不过期事件。

可选 `arguments.startAtQpc` 为同一Windows宿主的绝对QPC时间戳，最多提前120秒；省略时保持就绪检查后立即开始的旧行为。准备完成时已经错过起点的序列返回input_schedule_missed，不补发。结果附startTimestamp、clockFrequency及每帧sentTimestamp/acknowledgedTimestamp，用于与独立游戏响应、图像或音频采集对时；这些发送/确认时戳本身不等于端到端延迟。

`frames.sample`增加sameInstance、historyGaps、historyContinuous、requestedDurationCovered和主机采样时钟。检查summary.observedSpanSeconds才是实际呈现覆盖时长；历史环形缓冲缺乏重叠、App/VM变更或覆盖不足时，不能将统计当作完整稳态验收。

## 可复现测试步骤

`test` 接受 `steps` 数组，每项是同样的请求结构。禁止递归 test 和在测试内部恢复检查点。结果记录每步时间、输出、失败和生成的文件目录。例如：

```json
{"command":"test","arguments":{"steps":[
  {"command":"launch","arguments":{"package":"test.app"}},
  {"command":"screen"},
  {"command":"metrics","arguments":{"package":"test.app"}}
]}}
```

错误通过 `error.code` 区分，例如 `device_offline`、`instance_mismatch`、`port_conflict`、`permission_denied`、`app_exited`、`stale_observation`、`screen_not_ready`、`disk_full`、`timeout` 和 `cancelled`。命令返回“已启动”不等于游戏功能全部正常；脚本验收要结合截图、日志和实际游玩结果。
