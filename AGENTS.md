# AGENTS.md

.NET 10 自研 Agent，**三仓 + 组件化（dll 加载）架构**：

- **WtAgent（空壳启动器）**：self-contained 单 exe（含 .NET 运行时）。`install serve|tui|gui|web` 下载组件 zip 解压到 `%APPDATA%\agent\components\{name}`；`upgrade` 检查更新；组件 **dll 由空壳进程加载**（共享运行时）。空壳编译期 ProjectReference `deps/`（组件源码 clone），运行时从 components 目录 `Assembly.LoadFrom` 下载的 dll（ExcludeAssets=runtime + Private=false，dll 绝不随空壳发布）。
- **WtAgent.Server（serve 组件）**：纯 dll（Library）。serve 服务（会话 API WS/SSE + git 项目仓库 smart HTTP + Web UI 托管）。web UI 前端源码在 `web/`（Vue），打进组件 zip，空壳解压到 `%APPDATA%\agent\web`。
- **WtAgent.Client（tui 组件）**：纯 dll（Library）。TUI 终端聊天（顶级 Default）+ `web <remote>` 命令（浏览器打开目标 serve）。
- **Agent.Gui / Agent.Web**：未来（gui 未实现，web 为资源组件）。

## 组件接口（关键约定，签名稳定）
- `Entry.Commands`（`System.CommandLine.Command[]`）：子命令列表，空壳注册到自己的命令树（wsl 式扩展：`wtagent --help` 显示全部已装组件命令）
- `Entry.Default`（`Func<string[], int>?`）：顶级行为（无子命令时执行；tui = TUI）
- 组件内部各自解析命令行；空壳与组件同版本 System.CommandLine（类型统一）
- 组件可自行 `Process.Start("agent", ["install", ...])` 触发安装/更新（agent 在 PATH）

## 命令
- `wtagent install serve|tui|gui|web [--force]` / `wtagent upgrade [serve|tui|gui|web]` / `wtagent serve [<host>] [<port>]` / `agent`（顶级=TUI）/ `agent web [<remote>]`（未装组件显示占位命令提示安装）
- serve 参数：`[<host>] [<port>] [--projects] [--web] [--no-web] [--base-url] [--key] [--model] [--mock]`；Windows 服务：`--service`（SCM 注入）

## 发布（手动发版）
- 三仓各 `release.yml`：**仅 Actions 手动触发**（或 release PR 合并）发版；普通提交不发版
- 组件产物：framework-dependent 目录 zip（`agent-server-win-x64.zip` 等 7 平台，含 runtimes native 库）；空壳产物：self-contained 单 exe（`agent-win-x64.exe` 等）
- release-please 管版本（manifest + extra-files 同步 csproj Version）；PR 自动合并（RELEASE_TOKEN PAT）；CI 必须过（branch protection：main 需 build check）
- 下载源：`https://github.com/wtommy932/WtAgent.{repo}/releases/latest/download/{asset}`

## 本地工作区
- `D:\Agent` = 三仓容器：`Agent.Server\`（本仓）、`Agent.Client\`（tui 组件仓）、`Agent\`（空壳仓）、`box-backup\`（机顶盒刷机材料，勿删）
- 空壳开发需 `D:\Agent\Agent\deps\{Agent.Server,Agent.Client}`（clone 组件源码，gitignore）
- 构建：Server `dotnet build Agent.Server.csproj`（或 `.\build.ps1`）；Client `dotnet build Agent.Client.csproj`；Shell `dotnet build Agent.csproj`

## 环境铁律（Windows + PowerShell）
- 每次命令行先切 UTF-8：`chcp 65001 | Out-Null; [Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8`
- 含中文的文件用 Read 工具读取，勿用 Get-Content 看乱码
- **`agent run` 会烧 API 配额**，调试走代码走查/mock（serve `--mock`）
- 构建/清理用仓库脚本 `.\build.ps1`（杀残留 + build + 可选 -Mock 冒烟）、`.\format.ps1`（dotnet format 自动修复 + build）
- 风格提示全量提为 warning：`.editorconfig` + `Directory.Build.props`（EnforceCodeStyleInBuild）→ `dotnet build` 直接输出全部风格警告
- 命名空间必须跟随文件夹；代码贴合 C# 14/.NET 10（`field`、集合表达式、主构造函数、record）
