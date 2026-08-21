# format.ps1 — 一键代码清理：dotnet format（style + analyzers 自动修复）→ 构建（可加 -Mock 冒烟）
param(
    [switch]$Mock    # 清理后跑 mock 冒烟
)
chcp 65001 | Out-Null
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$OutputEncoding = [Text.Encoding]::UTF8
$root = $PSScriptRoot

# 杀残留 Agent 进程（会锁 dll 导致构建失败）
Get-Process | Where-Object { $_.ProcessName -match "agent" } | ForEach-Object { Stop-Process -Id $_.Id -Force }
Start-Sleep 1

# style：IDE 风格规则（using 清理/this 前缀等）；analyzers：IDE/CA 规则（有 code fix 的自动应用）
dotnet format Agent.Server.csproj style --verbosity minimal
dotnet format Agent.Server.csproj analyzers --severity warn --verbosity minimal

# 无 code fix 的规则（如 CA1068 参数顺序）无法自动修，verify 列出残留供手动处理
$leftover = dotnet format Agent.Server.csproj analyzers --severity warn --verify-no-changes --verbosity minimal 2>&1
if ($LASTEXITCODE -ne 0) { "--- 残留需手动修复 ---"; $leftover }

# 构建
$buildArgs = @()
if ($Mock) { $buildArgs += "-Mock" }
& "$root\build.ps1" @buildArgs
