using System.Text.RegularExpressions;

namespace WTangent.Server.Tools;

/// <summary>提交工具：git_commit。在指定 git 仓库 add -A + commit（替代 agent 手拼 shell 命令）。
/// 提交消息按 Conventional Commits 校验：type(scope): description；不符合时自动补 type（按文件特征推断，兜底 chore）。</summary>
public sealed partial class GitCommitTool : ITool
{
    public string Name => "git_commit";

    public object Definition => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = "提交当前仓库所有改动（git add -A + commit）。repo 为仓库目录（缺省当前目录）；message 为提交消息（Conventional Commits：type(scope): description，如 feat: 添加xx / fix: 修复xx；不符合时自动补 type）。返回提交结果。",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    repo = new { type = "string", description = "git 仓库目录（缺省当前目录）" },
                    message = new { type = "string", description = "提交消息（Conventional Commits 格式）" },
                },
                required = new[] { "message" },
            },
        },
    };

    public Task<string> RunAsync(string arguments, CancellationToken ct = default)
    {
        var repo = ToolArgs.GetString(arguments, "repo");
        var message = ToolArgs.GetString(arguments, "message");
        if (string.IsNullOrWhiteSpace(message)) return Task.FromResult("[git_commit] 缺少 message 参数");
        var cwd = repo.Length > 0 ? repo : Directory.GetCurrentDirectory();
        if (!Directory.Exists(Path.Combine(cwd, ".git")))
            return Task.FromResult($"[git_commit] 不是 git 仓库: {cwd}");

        var fixedMessage = NormalizeMessage(message);
        try
        {
            var add = RunGit(cwd, "add", "-A");
            if (add.ExitCode != 0) return Task.FromResult($"[git_commit] add 失败: {add.Output}");
            var commit = RunGit(cwd, "commit", "-m", fixedMessage);
            return Task.FromResult(commit.ExitCode != 0
                ? commit.Output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase)
                    ? $"[git_commit] 无改动可提交（{cwd}）"
                    : $"[git_commit] commit 失败: {commit.Output}"
                : $"[git_commit] 已提交（{cwd}）：{fixedMessage}");
        }
        catch (Exception e)
        {
            return Task.FromResult($"[git_commit] 失败: {e.Message}");
        }
    }

    /// <summary>Conventional Commits 规范：type(scope): description。无 type 前缀时按改动特征推断（test*→test、docs*→docs、
    /// 其余按 message 关键词，兜底 chore）。已有规范前缀则原样保留（仅修空白）。</summary>
    internal static string NormalizeMessage(string message)
    {
        var msg = message.Trim();
        // 先去前缀杂质（如 "commit: " / "message: "），避免被误判为规范格式
        msg = LeadingNoiseRegex().Replace(msg, "");
        if (ConvRegex().IsMatch(msg)) return msg;
        var type = InferType(msg);
        return $"{type}: {msg}";
    }

    private static string InferType(string msg)
    {
        var lower = msg.ToLowerInvariant();
        return lower switch
        {
            _ when lower.StartsWith("test", StringComparison.Ordinal) || lower.Contains("测试") => "test",
            _ when lower.StartsWith("docs", StringComparison.Ordinal) || lower.Contains("文档") || lower.Contains("readme") => "docs",
            _ when lower.Contains("修复") || lower.Contains("bug") || lower.Contains("fix") => "fix",
            _ when lower.Contains("性能") || lower.Contains("优化") || lower.Contains("perf") => "perf",
            _ when lower.Contains("重构") || lower.Contains("refactor") => "refactor",
            _ when lower.Contains("样式") || lower.Contains("style") || lower.Contains("格式") => "style",
            _ when lower.StartsWith("feat", StringComparison.Ordinal) || lower.Contains("新增") || lower.Contains("添加") || lower.Contains("支持") => "feat",
            _ => "chore",
        };
    }

    [GeneratedRegex(@"^[a-z]+(\([^)]*\))?!?: .+", RegexOptions.IgnoreCase)]
    private static partial Regex ConvRegex();

    [GeneratedRegex(@"^(commit|message|msg|提交|提交信息)\s*[:：]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingNoiseRegex();

    private static (int ExitCode, string Output) RunGit(string cwd, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = cwd,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("git 启动失败");
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, outTask.GetAwaiter().GetResult() + errTask.GetAwaiter().GetResult());
    }
}
