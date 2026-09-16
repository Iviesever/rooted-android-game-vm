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

推荐用请求文件，避免 PowerShell 引号转义：

```json
{
  "schemaVersion": 1,
  "command": "malody.reload",
  "arguments": { "path": "D:\\my-files\\my-skin.msp" }
}
```

```powershell
& $vm --request .\request.json --wait
```

## 常用请求

下面展示 `command` 和 `arguments`。省略包名时，Malody 模板使用 `me.mugzone.emiria`；通用应用操作可以指定其他合法包名。

| command | arguments 示例 | 用途 |
|---|---|---|
| `status` / `capabilities` | `{}` | 状态、协议能力 |
| `start` / `stop` | `{}` | 启动或 sync 后停止 |
| `apps` | `{}` | 第三方应用列表 |
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
| `malody.import` / `malody.reload` | `{"path":"D:\\chart.mcz"}` | 导入 / 重启后导入并验证解包 |
| `checkpoint.list` / `checkpoint.create` | `{}` | 列表 / 停机保存 |
| `checkpoint.restore` | `{"id":"检查点编号"}` | 校验、恢复与冷启动 |
| `checkpoint.recover` | `{}` | 恢复中断的磁盘切换 |
| `shell` / `root-shell` | `{"script":"id","timeoutSeconds":10}` | 显式调试 Shell |
| `jobs` / `job` / `cancel` | `{}` 或 `{"id":"任务编号"}` | 任务列表、查询、取消 |
| `quiesce` / `shutdown` | `{}` | 停止调试任务 / 安卓已停止时退出协调进程 |
| `licenses` | `{}` | 随程序附带的第三方库许可证 |

所有 ADB 操作都固定到经过检查的产品实例。没有“自动选第一个设备”的逻辑，也不调用全局 `adb kill-server`。

0.4.0 增加以下请求，完整参数以 `schema` 为准：

| command | arguments 示例 | 用途 |
|---|---|---|
| `runtime.inspect` | `{}` | 请求配置、实际显示、宿主内存；停机时附启动余量检查 |
| `runtime.configure` | `{"profile":{"renderer":"host","width":1920,"height":1080,"density":240,"refreshRate":120,"memoryMb":3072,"cpuCores":4,"desktopDisplay":true}}` | 停机后保存；下次启动生效 |
| `frames.sample` | `{"package":"me.mugzone.emiria","seconds":30}` | 实际呈现帧率、间隔分布和采样覆盖 |
| `files.export` | `{"package":"test.app","scope":"private","remote":"files/qa","local":"D:\\exports"}` | 导出指定目录，核验并受控解包 |
| `uninstall` | `{"package":"test.app","confirm":true}` | 卸载普通第三方应用；删除该应用数据 |
| `window.focus` | `{}` | 打开已经验证的产品安卓窗口 |

`status.hostMemory` 报告宿主物理余量与提交余量；`memoryProtection` 在警告或自动停机时给出原因。`start` 可能返回 `host_memory_low`，请处理容量不足后重试，不循环强行启动。`preview` 是 GUI 的二进制管道协议：JSON 元数据帧后紧跟 PNG 帧；命令行取证请使用 `screen`，不要把元数据当成已经收到 PNG。

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

## 可复现测试步骤

`test` 接受 `steps` 数组，每项是同样的请求结构。禁止递归 test 和在测试内部恢复检查点。结果记录每步时间、输出、失败和生成的文件目录。例如：

```json
{"command":"test","arguments":{"steps":[
  {"command":"launch","arguments":{"package":"me.mugzone.emiria"}},
  {"command":"screen"},
  {"command":"metrics","arguments":{"package":"me.mugzone.emiria"}}
]}}
```

错误通过 `error.code` 区分，例如 `device_offline`、`instance_mismatch`、`port_conflict`、`permission_denied`、`app_exited`、`stale_observation`、`screen_not_ready`、`disk_full`、`timeout` 和 `cancelled`。命令返回“已启动”不等于游戏功能全部正常；脚本验收要结合截图、日志和实际游玩结果。
