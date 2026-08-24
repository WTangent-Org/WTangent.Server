using System.Diagnostics;
using System.Text;
using WTangent.Server.Store;

namespace WTangent.Server.Tools;

/// <summary>bash 工具执行结果</summary>
public sealed record BashResult(string Output, string Error, int ExitCode)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>bash 工具：执行 shell 命令并返回输出</summary>
public sealed class BashTool : ITool
{
    /// <summary>危险命令（权限控制：拦截或确认）。在 tool 入口对命令做模式匹配。</summary>
    private static readonly string[] DangerousPatterns =
    [
        // 删除/格式化/系统级危险操作（大小写不敏感，词边界匹配，避免 Format-Table 误伤）
        @"\brm\s+-[rf]", @"\bRemove-Item\b", @"\bremove-item\b", @"\brmdir\b", @"\bRemove-", @"\bri\b",
        @"\bformat\b", @"\bFormat-Volume\b", @"\bClear-Content\b", @"\bclear-content\b", @"\bclear\b",
        @"\bshutdown\b", @"\bRestart-Computer\b", @"\bstop-computer\b", @"\breboot\b", @"\bmkfs\b",
        @"\bdd\s+if=", @"\bdel\b", @"\berase\b",
    ];

    /// <summary>命令是否命中危险命令（tool 入口直接匹配，整条命令扫描）</summary>
    private static bool IsDangerous(string command)
    {
        return DangerousPatterns.Any(pattern => System.Text.RegularExpressions.Regex.IsMatch(command, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    public string Name => "bash";

    /// <summary>LLM 工具定义（OpenAI function calling 格式）</summary>
    public object Definition => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = "执行 shell 命令。重要：当前环境是 Windows，使用 PowerShell 语法（不是 bash）。PowerShell 不识别 head/tail/ls/grep/cat/pwd 等 bash 命令，请用对应 PowerShell 命令：ls→Get-ChildItem、cat→Get-Content、grep→Select-String、pwd→Get-Location、head/tail→Select-Object -First/-Last。返回命令输出。",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    command = new { type = "string", description = "要执行的 shell 命令" },
                    cwd = new { type = "string", description = "工作目录（缺省 serve 进程目录；操作项目时传项目目录）" },
                    timeout = new { type = "integer", description = "超时毫秒数（缺省 60000，0=不超时）" },
                    background = new { type = "boolean", description = "true=后台运行，立即返回 PID；之后用 background 工具查状态/杀" },
                    input = new { type = "string", description = "命令启动后写入 stdin（交互式命令用），可省" },
                },
                required = new[] { "command" },
            },
        },
    };

    /// <summary>ITool 入口：解析 command 参数并执行，返回文本结果</summary>
    public async Task<string> RunAsync(string arguments, CancellationToken ct = default)
    {
        var command = ToolArgs.GetString(arguments, "command");
        if (string.IsNullOrWhiteSpace(command)) return "[bash] 缺少 command 参数";
        // cwd：工作目录（缺省 serve 进程目录）
        var cwd = ToolArgs.GetString(arguments, "cwd");
        // timeout：显式提供才使用（0=不超时），否则默认 60s
        var timeoutStr = ToolArgs.GetString(arguments, "timeout");
        var timeoutMs = timeoutStr.Length > 0 && int.TryParse(timeoutStr, out var t) ? t : 60_000;
        // background：true 则后台运行，立即返回 PID
        if (bool.TryParse(ToolArgs.GetString(arguments, "background"), out var bg) && bg)
            return RunBackground(command, cwd);
        // input：命令启动后写入 stdin（交互式），不关闭
        var input = ToolArgs.GetString(arguments, "input");
        var result = await RunAsync(command, confirm: true, timeoutMs, input.Length > 0 ? input : null, cwd, ct);
        return $"[exit {result.ExitCode}]\n{result.Output}{result.Error}".Trim();
    }

    /// <summary>后台运行：启动即返回，进程保留供查询/杀。可配 args[1]=id 查状态，args[2]=kill</summary>
    private static string RunBackground(string command, string cwd)
    {
        if (IsDangerous(command) &&
            !ConfirmProvider.Ask($"危险命令: {command}\n确认后台执行?"))
            return "[bash] 用户拒绝执行";

        var (proc, outTask, errTask) = StartProcess(command, cwd);
        proc.StandardInput.Close();
        BackgroundProcess.Register(new ProcessHandle(proc, outTask, errTask));
        return $"[后台已启动 PID={proc.Id}]\n初始输出: {(outTask.IsCompleted ? outTask.Result : "")}";
    }

    /// <summary>执行命令。confirm：危险命令需要确认；timeoutMs：超时强制杀进程（0/负=不超时）；input：写入 stdin（交互，null=关闭）；cwd：工作目录（空=当前目录）</summary>
    private static async Task<BashResult> RunAsync(string command, bool confirm = true, int timeoutMs = 60_000, string? input = null, string cwd = "", CancellationToken ct = default)
    {
        if (IsDangerous(command))
        {
            if (confirm && !ConfirmProvider.Ask($"危险命令: {command}\n确认执行?"))
                return new BashResult("", "用户拒绝执行", 126);
        }

        var (proc, outTask, errTask) = StartProcess(command, cwd);

        // 交互：写入 input；否则关闭 stdin 防等待
        if (input != null)
        {
            try { await proc.StandardInput.WriteAsync(input); await proc.StandardInput.FlushAsync(ct); } catch (IOException) { }
        }
        else
        {
            proc.StandardInput.Close();
        }

        // 超时或取消：杀进程树
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs > 0) timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            KillTree(proc);
            return new BashResult("", $"[超时 {timeoutMs}ms，已终止]", 124);
        }
        catch (Exception e)
        {
            KillTree(proc);
            return new BashResult("", e.Message, 127);
        }

        var output = await outTask;
        var error = await errTask;
        return new BashResult(output, error, proc.ExitCode);
    }

    /// <summary>启动命令进程：构建 shell 参数、启动、并发读管道。调用方负责等待/杀/关闭 stdin。cwd：工作目录（空=继承当前目录）</summary>
    private static (Process Proc, Task<string> Out, Task<string> Err) StartProcess(string command, string cwd = "")
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (cwd.Length > 0 && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
        if (OperatingSystem.IsWindows())
        {
            // Windows：PowerShell（pwsh 优先 → 系统 powershell.exe；TUI 要求 Win10+，无需 cmd 兜底）
            psi.FileName = FindWindowsShell();
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            // UTF-8 三连：pwsh 输出按系统 ANSI 编码（GBK），必须切 UTF-8 才能按 UTF-8 解码
            command = "chcp 65001 | Out-Null; [Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $OutputEncoding=[System.Text.Encoding]::UTF8; " + command;
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.FileName = "/bin/bash";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }

        var proc = Process.Start(psi)
            ?? throw new Exception("启动进程失败");
        // 输出管道并发读取（防大输出缓冲满死锁）。stdin 由调用方决定关闭或写输入
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        return (proc, outTask, errTask);
    }

    /// <summary>强杀进程树（含子进程），失败忽略</summary>
    private static void KillTree(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    /// <summary>Windows shell：pwsh 优先 → 系统自带 powershell.exe（Win10+ 必有，无需 cmd 兜底）</summary>
    private static string FindWindowsShell()
    {
        foreach (var exe in new[] { "pwsh.exe", "powershell.exe" })
        {
            var onPath = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
                .Select(p => Path.Combine(p, exe)).FirstOrDefault(File.Exists);
            if (onPath != null) return onPath;
            var known = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", exe);
            if (File.Exists(known)) return known;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    }
}
