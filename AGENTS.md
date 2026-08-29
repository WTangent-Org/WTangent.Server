# AGENTS.md

.NET 10 自研 Agent，**六仓 + 组件化（dll 加载）架构**（GitHub org：`WTangent-Org`）：

- **WTangent（空壳启动器）**：self-contained 单 exe（含 .NET 运行时，AssemblyName `wtangent`）。内置 `install/remove/upgrade/update`；组件索引 = 主仓 `components.json`（apt 模式：别名→仓库）。组件为 framework-dependent dll，解压到 `%APPDATA%\agent\components\{name}` 后由空壳进程 `Assembly.LoadFrom` 进 **Default ALC** 加载（共享运行时）。空壳对组件**只有运行时关系**（反射找 `IEntry`），不引用组件源码。
- **WTangent.Server（本仓，serve 组件）**：纯 dll（无入口、无 runtimeconfig，`dotnet agent-server.dll` 不可直接跑）。会话 API（WS/SSE）+ git 项目仓库 smart HTTP + Web UI 托管；前端源码在 `web/`（Vue），打进组件 zip，空壳安装时解压到 `%APPDATA%\agent\web`。LLM 调用全归 serve，客户端只是输入收集器。
- **WTangent.Tui（tui 组件）**：纯 dll，顶级 Default = TUI 终端聊天（Terminal.Gui）。
- **WTangent.Client（client 组件）**：纯 dll，`remote/run/web` 命令。
- **WTangent.GitCmd（git 组件）**：纯 dll，`git` 命令。
- **WTangent.Components**：Core/生成器源码仓（`src/WTangent.Core` 契约 + `src/WTangent.Components` 源生成器）。所有仓 csproj 直接 ProjectReference 其平级目录源码（CI checkout 同布局）；发版只把 `WTangent.Core.dll` + 生成器 dll 挂 GitHub release 资产（`wtangent dev restore` 直拉用）。**已彻底去 nuget**：无包引用、无 pack、不推 nuget.org。

## 组件契约（签名稳定）

- 入口类：`[AgentEntry("id", "名称", isAsync)]` + `partial Entry : IEntry`。生成器产出：静态 `Entry.App`（构造注入）、`Commands`（`[AgentCommand]` 收集）、`Tools`（`[AgentTool]`）、事件订阅接线（`[AgentEvent]`）、`[EntryStart]`/`[EntryStop]` 钩子调用（**钩子无参数**，上下文用 `Entry.App`）。
- 能力声明式（`IEntry` 默认空实现）：`Commands` 非空 → 注册到空壳命令树（父路径挂接如 `"root/remote"`）；`Default` 非空 → 顶级行为；`Tools` 非空 → serve 启动时合并进 LLM 工具表。
- 启动分流（空壳 `ComponentManager.StartEntry`，PCL-CE 式）：`isAsync`（→ `SupportAsyncStart`）= true → `Task.Run` 后台并行启动、全部注册完统一 await；false（默认）→ 当前线程串行启动。空壳先 `LoadEntry` 只构造不启动，再统一 `StartEntry`。
- 运行时上下文 `Application`：Logger/Events/Config/Store/Remote/GuiHost/Http/Services，宿主实现、注入每个组件（同一实例）。组件间**互引只用于编译期代码复用**（类型/工具方法，源码引用或未来 PackageReference），运行期协作（互发事件、serve 聚合工具）仍走 App 契约——不因能 using 就把运行期耦合变成编译期耦合。
- 日志/配置门面：任意位置 `Log.Info(...)` / `Config.Get<T>(key)`（Core 全局静态门面，宿主 `BuildApp` 时 `Log.Init`/`Config.Init`；未 Init 时 Log 退化 Console、Config 退化进程内存）。结构化配置走 `AgentConfigStore`（公开部分存 config.json `"agent"` 键，API Key 单独 DPAPI 文件）。

## 加载模型（关键不变量，改加载逻辑前必读）

- 所有组件进同一个 Default ALC，CLR 按**简单名统一**：`WTangent.Core` 永远只有空壳 bundle 里那一份。组件 csproj 源码引用 Core 用 `Private="false"`（等价旧 `ExcludeAssets="runtime"`）——Core 不拷进组件输出/zip（编译引用与生成器保留），"Core 由宿主提供"是显式声明；组件 zip 内出现 Core/System.CommandLine.dll 视为污染（dev 部署时强制清除，防旧版双副本 manifest mismatch）。
- 组件依赖解析（`ResolveComponentDependency`）：**先按简单名归一**——已加载的同名程序集直接复用（组件编译引用的 Core 版本号与内置常有出入，LoadFrom 绑定版本敏感，不归一会绑定失败炸掉类型加载；ABI 兼容由 minCore 门禁保证）；之后 `AssemblyDependencyResolver`（各组件自己的 `deps.json`，按组件隔离版本），最后兜底按名直扫组件目录（无 deps.json 的旧包）。
- 版本门禁：`agent-component.json` 的 `minCore` = 组件编译时引用的 Core 版本，**由生成器构建时自动写入（勿手改，该文件整体由生成器产出：name/asset/minCore/depends/commands/tools）**；install/upgrade 时与空壳内置 Core 版本（`ComponentManager.CoreVersion`）比较，不足则拒绝并提示升级空壳（重跑 `install.ps1`/`install.sh`）。`depends`（别名→最低版本）= 组件间互引的运行时声明（csproj `ComponentDepends` 属性 → 生成器写入）：install 递归自动拉装/版本拒装/循环检测，remove 卸载保护，加载按 depends 拓扑序。
- 双副本唯一来源 = 新建自定义 `AssemblyLoadContext`。真要建（插件隔离/卸载），必须把 Core 共享回 Default（McMaster `PreferSharedTypes` 或等价配置），否则类型同一性崩（`InvalidCastException` A→A）+ 静态分叉。
- **Core 演进纪律（分阶段）**：minCore 门禁只挡"新组件进旧空壳"，挡不住"旧组件进新空壳"。现阶段全是一方组件、同工作区同步更新——删成员直接删、全仓同步即可（`Application.Logger`/`Config` 就是这样删掉的，日志/配置统一走 `Log`/`Config` 门面）；一旦对外开放组件生态（有第三方编译产物要兼容），切换为只加不删 + `[Obsolete]` 引导。

## 命令

- `wtangent install serve|tui|client|gui|git [--force]` / `wtangent remove|upgrade [name]`（缺省全部已装——**纯本地扫 `components\` 目录**；安装时写 `.installed` 元数据 repo+版本，remove/upgrade 不依赖索引）/ `wtangent update`（刷索引）/ 顶级 `wtangent` = 按索引优先级取第一个已装且带 Default 的组件（headless 反转：无桌面 tui 优先）。索引只是远程清单缓存，不代表已装状态。管理/开发命令（install/remove/upgrade/update/dev）启动时不加载组件（避免本进程锁 dll 导致删不动目录；跨进程锁由删除重试兜底）。组件开发工具：`wtangent dev restore|build|install`（见下）。
- serve 参数：`[<host>] [<port>] [--projects] [--web] [--no-web] [--base-url] [--key] [--model] [--mock]`；Windows 服务：`--service`（SCM 注入）。
- 重名命令仅官方组件（serve/tui/gui）可覆盖，其余跳过并提示。

## 发布（手动发版）

- 各仓 `release.yml`：**仅 Actions 手动触发或 release PR 合并**才发版；普通提交不发版。release-please 管版本（`always-bump-patch`，manifest + extra-files 同步 csproj Version）。release PR 可能被分支保护 BLOCKED（PR 上的旧检查），用 `gh pr merge <N> --squash --delete-branch --admin` 手动合——合并提交触发 push run 建 release 挂资产。
- 一键发版脚本：工作区根 `release.ps1`（跨仓本地工具，**不进任何仓库**）：`.\release.ps1 [-Repo WTangent.Server] [-Version x.y.z]`——触发 workflow → 合并 release PR → 等 publish → 等资产挂上。
- 组件产物：`dotnet publish -r {rid} --self-contained false` → 七平台 zip（`agent-server-win-x64.zip` 等；serve 另带 web/dist）；空壳产物：self-contained 单 exe。CI 构建前 checkout Components 仓到平级目录（本仓也进同名子目录，复刻本地工作区布局——ProjectReference 的 `../` 依赖此布局）。
- 下载源：`https://github.com/WTangent-Org/{repo}/releases/latest/download/{asset}`（组件 zip 与 Core/生成器 dll 同一条线）。
- **发版顺序：Components 先发**（挂 Core/生成器 dll 资产），消费仓后发——`wtangent dev restore` 的直拉通道与 minCore 门禁都依赖 Components 最新资产先就位。
- 跨仓联调：工作区平级源码引用（零配置）；第三方单仓开发走 `wtangent dev restore`（GitHub release 直拉 Core/生成器 + depends 自动补装，`-p:WTangentDev=true` props 注入模式，不改 csproj）。

## 组件开发工具（wtangent dev）

- `wtangent dev restore --root <组件仓>`：读 `agent-component.json`（本地 json 自声明）→ depends 组件走安装链补装 → Core/生成器 dll 从 Components 仓 GitHub release 直拉缓存到 `%APPDATA%\agent\dev\refs`（内容健全性检查防老快照）→ 生成 `wtangent.dev.props`（HintPath Reference + Analyzer）。
- `wtangent dev build` / `wtangent dev install --proj <csproj>`：用 props 编译（`-p:WTangentDev=true -p:CustomBeforeMicrosoftCommonProps=<props>`，不改组件 csproj——csproj 里源码引用组带 `WTangentDev != true` 条件）→ 部署到 components 目录（`.installed` Version=local-dev，**别跑 upgrade**）。工作区官方组件用 `dev install serve` 等（平级源码引用构建）。

## 本地工作区

- `D:\Agent` = 六仓容器：`WTangent\`（空壳）、`WTangent.Server\`（本仓）、`WTangent.Tui\`、`WTangent.Client\`、`WTangent.GitCmd\`、`WTangent.Components\`、`box-backup\`（机顶盒刷机材料，勿删）。
- 构建：本仓 `dotnet build WTangent.Server.csproj`。
- 根 `Agent.slnx` 聚合各仓项目。

## 环境铁律（Windows + PowerShell）

- 每次命令行先切 UTF-8：`chcp 65001 | Out-Null; [Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8`
- 含中文的文件用 Read 工具读取，勿用 Get-Content 看乱码
- **`agent run` 会烧 API 配额**，调试走代码走查/mock（serve `--mock`，经空壳起）
- 格式化：`dotnet format WTangent.Server.csproj style` + `analyzers --severity warn`（自动修复），之后 `dotnet build` 验证
- 风格提示全量提为 warning：`.editorconfig` + `Directory.Build.props`（EnforceCodeStyleInBuild）→ `dotnet build` 直接输出全部风格警告
- 命名空间必须跟随文件夹；代码贴合 C# 14/.NET 10（`field` 关键字、扩展成员 extension blocks、集合表达式、主构造函数、record）——能用新特性就用，别写旧式样板

## 已知遗留（待清理）

- `Server/README.md` 部分内容滞后（WtAgent 老仓名、`[AgentComponent]`/`[AgentDefault]` 老特性名、"五仓"、nuget 时代发版描述），以本文件为准。
- 各仓根 `nuget.config`（`D:\nuget-local` 本地源）已无消费方（无包引用），可删但无害。
