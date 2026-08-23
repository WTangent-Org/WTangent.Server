# WtAgent.Server

.NET 10 自研 AI 助手服务端（**serve 组件**）：会话 API（WebSocket/SSE）+ git 项目仓库（smart HTTP）+ Web UI 托管。LLM 完全归 serve 调用，客户端只是输入收集器。

## 架构（五仓 + 组件化）

| 仓库 | 组件 | 形态 |
|---|---|---|
| [WtAgent](https://github.com/wtommy932/WtAgent) | 空壳启动器 | self-contained 单 exe（带运行时），install/upgrade/加载组件 |
| **本仓（WtAgent.Server）** | serve | 纯 dll，Entry 由源生成器生成（`[AgentComponent]`） |
| [WtAgent.Client](https://github.com/wtommy932/WtAgent.Client) | tui | 纯 dll，顶级 TUI + `web` 命令 |
| [WtAgent.Components](https://github.com/wtommy932/WtAgent.Components) | — | 共享源生成器（`[AgentComponent]`/`[AgentDefault]`） |
| [WtAgent.Core](https://github.com/wtommy932/WtAgent.Core) | — | 共享设施（统一 HttpClient 等） |

组件为 framework-dependent dll，由空壳进程加载（共享运行时）；发布为按平台 zip，`wtagent install serve` 下载解压。

## 安装

```powershell
# Windows（PowerShell）
irm https://raw.githubusercontent.com/wtommy932/WtAgent/main/install.ps1 | iex
wtagent install serve
wtagent serve
```

```bash
# Linux / macOS
curl -fsSL https://raw.githubusercontent.com/wtommy932/WtAgent/main/install.sh | bash
wtagent install serve
wtagent serve
```

## 开发

- `dotnet build WTangent.Server.csproj`
- 组件入口：命令类标 `[AgentComponent]` → 源生成器自动生成 `Entry.Commands`；顶级行为用 `[AgentDefault]`
- 内置工具：bash / read_file / glob / grep / refs（符号引用）/ edit_file / web_fetch / web_search …
- 版本手动指定，**手动发版**（Actions 页 run release workflow，可填版本号）
