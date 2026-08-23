# release.ps1 — 一键发版：触发 release workflow → 等 release PR → 审批其 CI（action_required）→
# 等自动合并 → 等合并触发的 publish run → 校验产物就位。
#
# 用法（普通 PowerShell，无需管理员；需 gh 已登录）：
#   .\release.ps1                             # 发 WTangent.Components（release-please 自动版本）
#   .\release.ps1 -Repo WTangent.Server       # 发 serve 组件（七平台 zip + web.zip）
#   .\release.ps1 -Version 0.1.0              # 手动指定版本（workflow 的 version 输入）
#
# 注意：发 Components 后，消费仓（空壳/四组件）必须等 nuget.org 索引就位再推，否则 CI restore
# 会浮动到更高旧版本（NU1603）。脚本最后会等到索引进位再退出。

param(
    [string]$Repo = "WTangent.Components",
    [string]$Version = ""
)

chcp 65001 | Out-Null
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = "Stop"

$org  = "WTangent-Org"
$full = "$org/$Repo"
$isNuget = $Repo -eq "WTangent.Components"

function Fail([string]$msg) { Write-Host $msg -ForegroundColor Red; exit 1 }

# ---------- 1. 触发 ----------
$before = [DateTimeOffset]::UtcNow.AddSeconds(-15)
if ($Version) { gh workflow run release.yml -R $full -f version=$Version }
else          { gh workflow run release.yml -R $full }
if ($LASTEXITCODE -ne 0) { Fail "触发失败（gh auth status 查一下）" }
Write-Host "已触发 release workflow ← $full"

# ---------- 2. 找到本次 workflow_dispatch run ----------
$runId = $null
for ($i = 0; $i -lt 24; $i++) {
    Start-Sleep 5
    $runs = gh run list -R $full --workflow release.yml --event workflow_dispatch --limit 5 --json databaseId,createdAt | ConvertFrom-Json
    $hit = $runs | Where-Object { [DateTimeOffset]::Parse($_.createdAt) -ge $before } | Select-Object -First 1
    if ($hit) { $runId = $hit.databaseId; break }
}
if (-not $runId) { Fail "找不到本次 run（Actions 页看看）" }
Write-Host "run: https://github.com/$full/actions/runs/$runId"

# ---------- 3. 等 release-please 产出 PR ----------
$pr = $null
for ($i = 0; $i -lt 24; $i++) {
    Start-Sleep 5
    $prs = gh pr list -R $full --head release-please--branches--main --json number,title | ConvertFrom-Json
    if ($prs.Count -gt 0) { $pr = $prs[0]; break }
}
if (-not $pr) { Fail "release PR 未出现（可能没有可发布提交？查 run 日志）" }
Write-Host "release PR #$($pr.number)：$($pr.title)"
$tag = ($pr.title -replace '.*release\s+', '').Trim()
if (-not $tag) { Fail "从 PR 标题解析版本失败：$($pr.title)" }

# ---------- 4. 审批 PR 的 CI（action_required = 需要人工批准的 workflow run） ----------
for ($i = 0; $i -lt 12; $i++) {
    $pruns = gh run list -R $full --branch release-please--branches--main --limit 5 --json databaseId,conclusion | ConvertFrom-Json
    $pending = @($pruns | Where-Object { $_.conclusion -eq 'action_required' })
    foreach ($p in $pending) {
        try { gh api repos/$full/actions/runs/$($p.databaseId)/approve -X POST | Out-Null } catch { }
        Write-Host "已审批 PR CI run $($p.databaseId)"
    }
    if ($pending.Count -gt 0) { break }
    Start-Sleep 5
}

# ---------- 5. 等 PR 自动合并（workflow 内的合并循环要求 PR CI 绿；最长 ~15 分钟） ----------
$merged = $false
for ($i = 0; $i -lt 90; $i++) {
    Start-Sleep 10
    $state = gh pr view $pr.number -R $full --json state --jq .state
    if ($state -eq "MERGED") { $merged = $true; break }
    $failed = @(gh run list -R $full --branch release-please--branches--main --limit 3 --json conclusion |
        ConvertFrom-Json | Where-Object conclusion -eq 'failure')
    if ($failed.Count -gt 0) { Fail "PR CI 失败：https://github.com/$full/pull/$($pr.number)" }
}
if (-not $merged) { Fail "等合并超时（分支保护/检查未过？）" }
$mergeTime = [DateTimeOffset]::UtcNow.AddSeconds(-10)
Write-Host "PR 已合并（squash）"

# ---------- 6. 等合并触发的 release run 完成（那次才真 publish；dispatch 那次的 publish 撞 409 属预期） ----------
$done = $false
for ($i = 0; $i -lt 90; $i++) {
    Start-Sleep 10
    $runs = gh run list -R $full --workflow release.yml --event push --limit 5 --json databaseId,createdAt,status,conclusion | ConvertFrom-Json
    $hit = $runs | Where-Object { [DateTimeOffset]::Parse($_.createdAt) -ge $mergeTime } | Select-Object -First 1
    if ($hit -and $hit.status -eq 'completed') {
        if ($hit.conclusion -ne 'success') { Fail "publish run 失败：https://github.com/$full/actions/runs/$($hit.databaseId)" }
        $done = $true; break
    }
}
if (-not $done) { Fail "等 publish 超时" }
Write-Host "publish 完成（release v$tag）" -ForegroundColor Green

# ---------- 7. 校验产物就位 ----------
if ($isNuget) {
    # nuget.org 索引有分钟级延迟；本机被镜像重定向时 -L 跟随即可（restore 实际走的也是它）
    $url = "https://api.nuget.org/v3-flatcontainer/wtangent.components/$tag/wtangent.components.$tag.nupkg"
    $ok = $false
    for ($i = 0; $i -lt 30; $i++) {
        $code = curl.exe -sL -o NUL -w '%{http_code}' $url 2>$null
        if ($code -eq '200') { $ok = $true; break }
        Start-Sleep 20
    }
    if ($ok) { Write-Host "✅ nuget.org 已可 restore：WTangent.Components $tag（可以推消费仓了）" -ForegroundColor Green }
    else { Write-Host "⚠ nuget 索引尚未就位（publish 已成功，等几分钟再推消费仓）" -ForegroundColor Yellow }
} else {
    $rel = gh release view "v$tag" -R $full --json tagName,assets 2>$null | ConvertFrom-Json
    if ($rel) { Write-Host "✅ release v$tag 已建，资产：$(($rel.assets | ForEach-Object name) -join ', ')" -ForegroundColor Green }
    else { Write-Host "⚠ release v$tag 未查到，Actions 页确认" -ForegroundColor Yellow }
}
