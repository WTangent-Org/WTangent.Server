# build.ps1 — 一键构建（UTF-8 + 杀残留进程 + dotnet build；-Mock 追加 mock 冒烟）
param(
    [switch]$Mock,   # 构建成功后跑 10s mock 冒烟（serve 组件 --mock）
    [switch]$Clean   # 先 clean 再 build
)
chcp 65001 | Out-Null
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$OutputEncoding = [Text.Encoding]::UTF8
$root = $PSScriptRoot

# 杀残留 Agent 进程（会锁 dll 导致构建失败）
Get-Process | Where-Object { $_.ProcessName -match "agent" } | ForEach-Object { Stop-Process -Id $_.Id -Force }
Start-Sleep 1

if ($Clean) { dotnet clean Agent.Server.csproj | Out-Host }
dotnet build Agent.Server.csproj
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Mock) {
    # serve 组件 mock 冒烟：起 10s 看是否正常监听
    $out = Join-Path $env:TEMP "agent\mock-smoke"
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    $dll = Join-Path $root "bin\Release\net10.0\agent-server.dll"
    $p = Start-Process dotnet -ArgumentList "$dll --mock" -WorkingDirectory $root -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $out "out.txt") -RedirectStandardError (Join-Path $out "err.txt")
    Start-Sleep 10
    if (!$p.HasExited) { Stop-Process -Id $p.Id -Force; "MOCK OK (10s, killed)"; Get-Content (Join-Path $out "out.txt") | Select-Object -First 3 }
    else { "MOCK EXITED code=$($p.ExitCode)"; Get-Content (Join-Path $out "err.txt") -ErrorAction SilentlyContinue | Select-Object -First 8 }
}
