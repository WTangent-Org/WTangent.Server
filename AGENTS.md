# AGENTS.md

.NET 10 自研 Agent，**六仓 + 组件化（dll 加载）架构**（GitHub org：`WTangent-Org`）：

- **WTangent（空壳启动器）**：self-contained 单 exe（含 .NET 运行时，AssemblyName `wtangent`）。内置 `install/remove/upgrade/update`；组件索引 = 主仓 `components.json`（apt 模式：别名→仓库）。组件为 framework-dependent dll，解压到 `%APPDATA%\agent\components\{name}` 后由空壳进程 `Assembly.LoadFrom` 进 **Default ALC** 加载（共享运行时）。空壳对组件**只有运行时关系**（反射找 `IEntry`），不引用组件源码。
- **WTangent.Server（本仓，serve 组件）**：纯 dll（无入口、无 runtimeconfig，`dotnet agent-server.dll` 不可直接跑）。会话 API（WS/SSE）+ git 项目仓库 smart HTTP + Web UI 托管；前端源码在 `web/`（Vue），打进组件 zip，空壳安装时解压到 `%APPDATA%\agent\web`。LLM 调用全归 serve，客户端只是输入收集器。
- **WTangent.Tui（tui 组件）**：纯 dll，顶级 Default = TUI 终端聊天（Terminal.Gui）。
- **WTangent.Client（client 组件）**：纯 dll，`remote/run/web` 命令。
- **WTangent.GitCmd（git 组件）**：纯 dll，`git` 命令。
- **WTangent.Components**：共享 NuGet 包 = 源生成器（analyzers/dotnet/cs）+ `WTangent.Core` 契约（lib/net10.0）；发布到 nuget.org（Trusted Publishing）。

## 组件契约（签名稳定）

- 入口类：`[AgentEntry("id", "名称", isAsync)]` + `partial Entry : IEntry`。生成器产出：静态 `Entry.App`（构造注入）、`Commands`（`[AgentCommand]` 收集）、`Tools`（`[AgentTool]`）、事件订阅接线（`[AgentEvent]`）、`[EntryStart]`/`[EntryStop]` 钩子调用。
- 能力声明式（`IEntry` 默认空实现）：`Commands` 非空 → 注册到空壳命令树（父路径挂接如 `"root/remote"`）；`Default` 非空 → 顶级行为；`Tools` 非空 → serve 启动时合并进 LLM 工具表。
- 运行时上下文 `Application`：Logger/Events/Config/Store/Remote/GuiHost/Http/Services，宿主实现、注入每个组件（同一实例）。组件间不互引 dll，协作全走 App。
- 日志/配置门面：任意位置 `Log.Info(...)` / `Config.Get<T>(key)`（Core 全局静态门面，宿主 `BuildApp` 时 `Log.Init`/`Config.Init`；未 Init 时 Log 退化 Console、Config 退化进程内存）。**门面在 WTangent.Components ≥ 0.0.9 才有**，组件引用升到 0.0.9 后方可使用。

## 加载模型（关键不变量，改加载逻辑前必读）

- 所有组件进同一个 Default ALC，CLR 按**简单名统一**：`WTangent.Core` 永远只有空壳 bundle 里那一份。组件 csproj 用 `ExcludeAssets="runtime"` 引 WTangent.Components——Core 不拷进组件输出/zip（编译引用与生成器保留），"Core 由宿主提供"是显式声明。
- 组件依赖解析：`AssemblyDependencyResolver`（各组件自己的 `deps.json`，确定性、按组件隔离版本），注册于 `TryLoadComponent`；`Resolving` 事件兜底按名直扫组件目录（无 deps.json 的旧包）。
- 版本门禁：`agent-component.json` 的 `minCore` = 组件编译时引用的 Core 版本，**由生成器构建时自动写入（勿手改，该文件整体由生成器产出：name/asset/minCore/commands/tools）**；install/upgrade 时与空壳内置 Core 版本（`ComponentManager.CoreVersion`）比较，不足则拒绝并提示升级空壳（重跑 `install.ps1`/`install.sh`）。
- 双副本唯一来源 = 新建自定义 `AssemblyLoadContext`。真要建（插件隔离/卸载），必须把 Core 共享回 Default（McMaster `PreferSharedTypes` 或等价配置），否则类型同一性崩（`InvalidCastException` A→A）+ 静态分叉。
- **Core 演进纪律（分阶段）**：minCore 门禁只挡"新组件进旧空壳"，挡不住"旧组件进新空壳"。现阶段全是一方组件、同工作区同步更新——删成员直接删、全仓同步即可（`Application.Logger`/`Config` 就是这样删掉的，日志/配置统一走 `Log`/`Config` 门面）；一旦对外开放组件生态（有第三方编译产物要兼容），切换为只加不删 + `[Obsolete]` 引导。

## 命令

- `wtangent install serve|tui|client|gui|git [--force]` / `wtangent remove|upgrade [name]`（缺省全部已装）/ `wtangent update`（刷索引）/ 顶级 `wtangent` = 按索引优先级取第一个已装且带 Default 的组件（headless 反转：无桌面 tui 优先）。
- serve 参数：`[<host>] [<port>] [--projects] [--web] [--no-web] [--base-url] [--key] [--model] [--mock]`；Windows 服务：`--service`（SCM 注入）。
- 重名命令仅官方组件（serve/tui/gui）可覆盖，其余跳过并提示。

## 发布（手动发版）

- 各仓 `release.yml`：**仅 Actions 手动触发或 release PR 合并**才发版；普通提交不发版。release-please 管版本（`always-bump-patch`，manifest + extra-files 同步 csproj Version），release PR 自动合并（RELEASE_TOKEN PAT）。CI 必须过（main 分支保护需 build check）。
- 一键发版脚本：`.\release.ps1 [-Repo WTangent.Server] [-Version x.y.z]`——触发 workflow → 审批 release PR 的 CI → 等合并 → 等 publish → 等 nuget 索引就位（Components 发版后消费仓必须等索引再推，否则 CI restore 浮动到更高旧版本 NU1603）。
- 组件产物：`dotnet publish -r {rid} --self-contained false` → 七平台 zip（`agent-server-win-x64.zip` 等；serve 另有 web.zip）；空壳产物：self-contained 单 exe。
- 下载源：`https://github.com/WTangent-Org/{repo}/releases/latest/download/{asset}`。
- 跨仓联调：本地包源 `D:\nuget-local`（各仓 nuget.config 已配）。Core 有改动时先 `dotnet pack -p:Version=<next> -o D:\nuget-local`，消费方升引用验证；**推送顺序：Components 先发 nuget.org，再推消费仓**（否则对方 CI restore 失败）。

## 本地工作区

- `D:\Agent` = 六仓容器：`WTangent\`（空壳）、`WTangent.Server\`（本仓）、`WTangent.Tui\`、`WTangent.Client\`、`WTangent.GitCmd\`、`WTangent.Components\`、`box-backup\`（机顶盒刷机材料，勿删）。
- 构建：本仓 `dotnet build WTangent.Server.csproj`（或 `.\build.ps1`：杀残留进程 + build）。**注意 `build.ps1 -Mock` 是旧 exe 时代残留**：agent-server 已是纯 dll，无 runtimeconfig，`dotnet agent-server.dll` 直接报错，待修。
- 根 `Agent.slnx` 聚合各仓项目。

## 环境铁律（Windows + PowerShell）

- 每次命令行先切 UTF-8：`chcp 65001 | Out-Null; [Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8`
- 含中文的文件用 Read 工具读取，勿用 Get-Content 看乱码
- **`agent run` 会烧 API 配额**，调试走代码走查/mock（serve `--mock`，经空壳起）
- 格式化用 `.\format.ps1`（dotnet format 自动修复 + build）
- 风格提示全量提为 warning：`.editorconfig` + `Directory.Build.props`（EnforceCodeStyleInBuild）→ `dotnet build` 直接输出全部风格警告
- 命名空间必须跟随文件夹；代码贴合 C# 14/.NET 10（`field`、集合表达式、主构造函数、record）

## 已知遗留（待清理）

- `Store/PluginLoader.cs`：旧架构残留（还引用 `Agent.Tui.dll`、`agent install tui --from` 老名字）；`McMaster.NETCore.Plugins` 包引用（本仓与 Tui csproj）目前无代码使用。
- `Server/README.md` 部分内容滞后（WtAgent 老仓名、`[AgentComponent]`/`[AgentDefault]` 老特性名、"五仓"），以本文件为准。
