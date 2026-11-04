# 一泽的 AI Agent 任务监控

独立的 Windows 悬浮小软件。支持 **Codex / WorkBuddy / Claude 桌面端 Code**，可勾选监控平台；以项目分组展示当前对话，开始执行自动加入，运行中转圈并实时计时，本轮完成变绿并提醒，点击标题打开对应平台的对话。

不同平台使用独立的浅色圆角分区，平台名只在区域左上角显示一次；区域内保留简洁的项目与对话列表。

Codex 使用浅绿色，WorkBuddy 使用浅蓝色，Claude Code 使用浅橙色。任务行与悬停底色保持所属平台的色系，绿色完成标记保留。

![界面示意，使用固定示例数据](docs/platform-preview.png)

## 使用

从 [Releases](https://github.com/YizeAINative/yize-codex-monitor/releases/latest) 下载便携压缩包，解压后双击 `CodexFloat.exe`。源码构建后也可以运行 `dist/CodexFloat.exe`。无需安装 Python、Node.js 或插件。程序使用 Windows 自带的 .NET Framework 桌面组件，已在 Windows 11 上验证。

- **运行中**：右侧转圈。
- **本轮计时**：转圈左侧每秒更新运行时间，如 `03:28` / `01:03:28`；完成后冻结，继续提问进入新一轮时重置。
- **本轮完成**：绿色勾选和完成文字，弹出一次提醒并播放提示音。完成项一直保留到手动清除。
- **继续提问**：同一个对话自动回到转圈，下一轮完成再次提醒。
- **已中断**：明确显示中断，不发送完成提醒。
- **待确认**：缺少明确结束事件且无法确认仍有写入进程，不误标完成。
- **点击标题**：Codex / WorkBuddy 通过应用链接打开对应对话；Claude 桌面端 Code 通过 `claude://claude.ai/epitaxy/local_<ID>` 打开桌面端对应会话，不启动命令行。
- **拖动顶部**：按住顶部标题栏的空白区域或名称都可以移动窗口；置顶、设置、收起按钮仍正常点击。右下角可调整宽度。
- **右上角三个点**：勾选 Codex / WorkBuddy / Claude Code，以及置顶、声音、弹出提醒、测试提醒、重置位置、开机启动、退出。取消平台勾选后停止该平台读取、隐藏其条目并停止提醒；重新勾选不补播暂停期间的完成通知。
- **右上角减号**：收起到系统托盘。双击托盘图标或再次启动 EXE 即可找回。
- **清除完成**：只清除悬浮列表中已结束的行，不删除 Codex 的聊天或文件。

绿色表示当前执行轮次已经结束，并不代表整个项目的业务目标已经完成。

## 数据与工作方式

默认监控 `%USERPROFILE%\.codex`，也支持 `CODEX_HOME` 环境变量。

WorkBuddy 自动发现 `%USERPROFILE%\.workbuddy-ai\workbuddy.db` 或 `%USERPROFILE%\.workbuddy\workbuddy.db`，也支持 `WORKBUDDY_HOME`。使用 Windows 自带 SQLite 组件以只读模式读取标题、项目路径、状态和时间元数据，不读取消息正文。在 WorkBuddy 5.3.14 的本地数据结构上核对。

WorkBuddy 的 `planning` / `working` 显示转圈，`pending` 显示等待输入，`completed` 变绿，`terminated` 显示中断，`error` / `failed` 显示失败。初次运行不列出所有历史完成任务。若首次接入时 WorkBuddy 任务已经在执行，只能从最近的状态时间估计起点，计时前显示 `≈`；之后观察到的新轮次正常重新计时。

Claude 仅监控桌面端 **Code**，不监控普通 Chat、Cowork 或独立命令行任务。自动发现 `%LOCALAPPDATA%\Claude-Data\claude_*`、MSIX 的 `LocalCache\Roaming\Claude`、`%APPDATA%\Claude` 和 `%LOCALAPPDATA%\Claude-3p` 中的 `claude-code-sessions` 索引；可用 `CLAUDE_DESKTOP_HOME` 或 `--claude-home` 指定桌面端数据根目录。索引提供桌面会话 ID、标题和工作目录，仅跟踪索引关联的 `%USERPROFILE%\.claude\projects` 状态日志（此目录仍支持 `CLAUDE_CONFIG_DIR`）。不读取登录凭据，不修改 Claude 文件。

Claude 首次接入每个日志只读取末尾最多 **256 KiB**，跳过首个不完整 JSON 行，保留恰好位于边界的完整行；之后按文件偏移只读取新增字节，每轮最多 256 KiB。文件被截短时也从新的尾部恢复，不回读整段历史。仅提取生命周期字段，不保存消息正文。标题来自桌面端索引，历史结束任务不补发提醒。尾部没有本轮起点时用桌面端最新提问时间估算计时，并显示 `≈`；不会让早于该次提问的结束事件将新一轮误标完成。桌面端日志若未落地或尾部缺少明确状态，不凭空判定完成。桌面端打开本身不代表 Code 正在执行；使用桌面端派生的工作进程辅助判断。

Claude 适配状态单独保存在 `%LOCALAPPDATA%\CodexFloat\claude-desktop-code`，不会继承旧命令行监控列表。已用隔离夹具验证桌面端身份映射、命令行过滤、精确跳转链接、多轮通知、开关恢复、中断、清除与大文件限量读取。

程序仅在本机工作，没有网络请求，不调用模型，不读取登录凭据，不修改 Codex 配置和会话文件。读取来源为：

- `sessions/**/*.jsonl` 的会话身份和生命周期事件；通过目录监听和增量读取更新状态。
- `session_index.jsonl` 的对话标题。
- `.codex-global-state.json` 中的项目名称与对话归属。

自动忽略内部子代理。监控整个会话目录，因此早期日期创建的旧对话恢复执行也能识别。已发现的对话（包括已完成项）每约 0.8 秒轻量检查文件大小，避免 Windows 延迟文件变化通知时漏掉下一轮提问；每 10 秒补查目录。未监控过的历史完成对话不会在首次启动时集中弹提醒。

本软件自己的设置、列表状态及简要诊断保存在 `%LOCALAPPDATA%\CodexFloat`，完成提醒按对话与执行轮次去重。

“开机自动启动”仅在用户勾选时设置当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\CodexFloat`。关闭该选项即可取消。便携版启用后请保持 EXE 路径稳定。

本工具当前针对本地 Codex / WorkBuddy / Claude Code 会话。云端或其他电脑未落地到本机的会话不在监控范围。若平台后续改变本地日志或数据库格式，可能需要更新读取逻辑。无法明确识别的状态保持“待确认”。

## 构建与验证

源文件：`Core.cs`（Codex 生命周期、增量读取、目录监听、持久化和计时），`WorkBuddy.cs`（WorkBuddy 只读 SQLite 适配器、状态机及平台监控调度），`Claude.cs`（限量读取状态事件与状态机）、`ClaudeDesktop.cs`（桌面端索引、会话筛选和桌面跳转），`App.cs`（WPF 悬浮窗、统一风格菜单、托盘、提醒及对话跳转）。

```powershell
.\build.ps1
.\dist\CoreTests.exe
```

使用本机 .NET Framework 编译器，无 NuGet 或 npm 依赖。`CoreTests.exe` 在独立临时目录运行，不修改真实 Codex 数据。

已通过 115 项检查，包括 Codex 多轮状态循环、重启恢复、提醒去重、旧日期目录发现、部分 JSON / UTF-8 写入、截断恢复、子代理过滤，以及 WorkBuddy 原生 SQLite 只读约束、状态循环、等待输入、同 ID 跨平台隔离、平台开关与计时冻结。Claude 增加了桌面身份映射、命令行过滤、桌面跳转、尾部读取上限、完整行边界、截短重读上限、仅进度记录的尾部、旧终止事件和半行追加测试。状态切换测试使用隔离数据库或 JSONL，不修改用户会话；本机桌面安装包的数据结构与跳转路由已核对。

开发诊断参数：

```powershell
# 仅输出一次本地状态，使用独立诊断目录
.\dist\CodexFloat.exe --dump --data-dir .\test-data\dump --output .\test-data\snapshot.json

# 使用隔离的测试会话目录
.\dist\CodexFloat.exe --home C:\path\to\fixture --data-dir C:\path\to\test-state
```

`--demo` 是标注“样式预览 · 非实时”的固定示例界面，仅供开发验图；正式快捷方式与正常启动不使用该参数。

## 参考

状态采集思路与对话链接格式参考 [Agent Monitor HUD](https://github.com/LH-03/agent-monitor-hud) 及其[架构说明](https://github.com/LH-03/agent-monitor-hud/blob/main/docs/ARCHITECTURE.md)。本程序独立实现简洁列表与状态机，未依赖或安装该项目。本地研究资料、真实会话数据和测试运行记录不包含在本仓库内。

非官方工具，与 OpenAI、腾讯、Anthropic 无关联。
