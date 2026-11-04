# 一泽 Codex 监控

独立的 Windows 悬浮小软件。以项目分组展示当前 Codex 对话，开始执行自动加入，运行中转圈，本轮完成变绿并提醒，点击标题打开对应对话。

## 使用

从 [Releases](https://github.com/YizeAINative/yize-codex-monitor/releases/latest) 下载便携压缩包，解压后双击 `CodexFloat.exe`。源码构建后也可以运行 `dist/CodexFloat.exe`。无需安装 Python、Node.js 或插件。程序使用 Windows 自带的 .NET Framework 桌面组件，已在 Windows 11 上验证。

- **运行中**：右侧转圈。
- **本轮完成**：绿色勾选和完成文字，弹出一次提醒并播放提示音。完成项一直保留到手动清除。
- **继续提问**：同一个对话自动回到转圈，下一轮完成再次提醒。
- **已中断**：明确显示中断，不发送完成提醒。
- **待确认**：缺少明确结束事件且无法确认仍有写入进程，不误标完成。
- **点击标题**：通过 Codex 桌面版的 `codex://threads/{id}` 打开对应对话。
- **拖动顶部**：移动窗口；右下角可调整宽度。
- **右上角三个点**：置顶、声音、弹出提醒、测试提醒、重置位置、开机启动、退出。
- **右上角减号**：收起到系统托盘。双击托盘图标或再次启动 EXE 即可找回。
- **清除完成**：只清除悬浮列表中已结束的行，不删除 Codex 的聊天或文件。

绿色表示当前执行轮次已经结束，并不代表整个项目的业务目标已经完成。

## 数据与工作方式

默认监控 `%USERPROFILE%\.codex`，也支持 `CODEX_HOME` 环境变量。

程序仅在本机工作，没有网络请求，不调用模型，不读取登录凭据，不修改 Codex 配置和会话文件。读取来源为：

- `sessions/**/*.jsonl` 的会话身份和生命周期事件；通过目录监听和增量读取更新状态。
- `session_index.jsonl` 的对话标题。
- `.codex-global-state.json` 中的项目名称与对话归属。

自动忽略内部子代理。监控整个会话目录，因此早期日期创建的旧对话恢复执行也能识别。已发现的对话（包括已完成项）每约 0.8 秒轻量检查文件大小，避免 Windows 延迟文件变化通知时漏掉下一轮提问；每 10 秒补查目录。未监控过的历史完成对话不会在首次启动时集中弹提醒。

本软件自己的设置、列表状态及简要诊断保存在 `%LOCALAPPDATA%\CodexFloat`，完成提醒按对话与执行轮次去重。

“开机自动启动”仅在用户勾选时设置当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\CodexFloat`。关闭该选项即可取消。便携版启用后请保持 EXE 路径稳定。

本工具当前针对本地 Codex 会话。云端或其他电脑未落地到本机的会话不在监控范围。若 Codex 后续改变本地日志格式，可能需要更新读取逻辑。无法从日志明确识别的状态保持“待确认”。

## 构建与验证

源文件：`Core.cs`（生命周期、增量读取、目录监听、持久化），`App.cs`（WPF 悬浮窗、统一风格菜单、托盘、提醒及对话跳转）。

```powershell
.\build.ps1
.\dist\CoreTests.exe
```

使用本机 .NET Framework 编译器，无 NuGet 或 npm 依赖。`CoreTests.exe` 在独立临时目录运行，不修改真实 Codex 数据。

已通过 33 项检查，包括多轮状态循环、重启恢复、提醒去重、旧日期目录中新建任务、部分 JSON / UTF-8 写入、截断恢复、子代理过滤及中断区分。另用隔离记录验证了界面从转圈变绿以及提醒浮窗的实际渲染；真实监控结果与 Codex 对话列表状态进行了对照。

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

非官方工具，与 OpenAI 无关联。
