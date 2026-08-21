using System.ComponentModel;
using System.Diagnostics;

namespace WTangent.Server.Store;

/// <summary>git 项目存储：本地项目目录 = git 仓库 + .agent 清单；agent git = 服务器 ↔ git 仓库同步。
/// 本地修改前 fetch（pull 合并，多人协作），修改后 push（add+commit+push origin）；无 origin = 本地项目模式仅本地提交。
/// 提交规范：agent: {项目名}: {用户信息|时间}；本地提交原样推到服务器（服务器不重写），备份推送由服务器后台单独处理。
/// 多人共用同一项目：各自 clone → pull 合并 → push（冲突按 git 常规解决；需要隔离时用 git 分支）。</summary>
public sealed class GitStore(string repoDir)
{
    public string RepoDir => repoDir;
    public string AgentFile => Path.Combine(repoDir, ".agent");

    /// <summary>项目名（.agent 的 name；缺失回退目录名）</summary>
    public string ProjectName => Manifest("name") ?? new DirectoryInfo(repoDir).Name;

    /// <summary>提交信息规范（Conventional Commits）：type(scope): description。
    /// **提交全自动交给 agent**：agent 提供完整消息（feat/fix/refactor/…，type 由它按改动定）；
    /// 没提供时兜底 chore({项目}): {时间}（不再固定 imp）。</summary>
    public string DefaultCommitMessage(string? summary) =>
        summary is { Length: > 0 }
            ? summary
            : $"chore({ProjectName}): {DateTime.Now:yyyy-MM-dd HH:mm}";

    /// <summary>新建本地项目：git init + 身份 + .agent 清单</summary>
    public static GitStore Init(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        var store = new GitStore(dir);
        store.EnsureRepo();
        store.WriteManifest(name, server: null);
        return store;
    }

    /// <summary>从服务器克隆项目到 destDir（服务器 base url + 项目名 → {base}/git/{project}，目录即仓库）</summary>
    public static GitStore Clone(string serverName, string serverUrl, string project, string destDir)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(destDir))!;
        Directory.CreateDirectory(parent);
        var (code, output) = RunIn(parent, "clone", ProjectUrl(serverUrl, project), destDir);
        if (code != 0) throw new InvalidOperationException($"clone 失败: {output.Trim()}");
        var store = new GitStore(destDir);
        store.WriteManifest(project, serverName);
        return store;
    }

    public static string ProjectUrl(string serverBaseUrl, string project) =>
        $"{serverBaseUrl.TrimEnd('/')}/git/{project}";

    /// <summary>提交（无变化静默成功）</summary>
    public bool Commit(string message)
    {
        Run("add", "-A");
        var (code, output) = RunCore("commit", "-m", message);
        return code == 0 || output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>本地修改前拉取：git pull（默认合并分支，多人协作；冲突/分叉报错返回 false）。remoteBranch 指定时 pull origin 该分支。</summary>
    public bool Pull(string? remoteBranch = null)
    {
        var (code, output) = remoteBranch is { Length: > 0 }
            ? RunCore("pull", "origin", remoteBranch, "--no-edit")
            : RunCore("pull", "--no-edit");
        if (code != 0) Console.Error.WriteLine($"[git pull] {output.Trim()}");
        return code == 0;
    }

    /// <summary>修改后推送：add+commit+push origin（本地提交原样上服务器，服务器不重写）。
    /// 无 origin 时：serverName（或 .agent 的 remote）非空 → 先绑定 origin + 写清单（一并提交），再首次推送（在服务器建项目）；否则本地模式仅本地提交。</summary>
    public IReadOnlyList<string> Push(string message, string? serverName = null)
    {
        var branch = CurrentBranch();
        if (!HasOrigin())
        {
            var server = serverName ?? Manifest("remote");
            if (server is null)
            {
                Commit(message);
                return ["无 origin（本地项目模式，仅本地提交；首次上服务器用 agent git push --server <name>）"];
            }
            var url = new ServerRegistry().Get(server);
            if (url is null)
                return [$"服务器 {server} 未配置，先 agent git remote add {server} <url>"];
            var project = ProjectName;
            if (!Run("remote", "add", "origin", ProjectUrl(url, project)))
                return ["git remote add origin 失败"];
            WriteManifest(project, server);   // 先写清单再提交，避免 .agent 未提交导致的 pull 冲突
        }
        Commit(message);
        var (code, output) = RunCore("push", "-u", "origin", branch);   // -u：设 upstream，后续 pull 才能 merge
        return code == 0 ? [] : [$"push 失败: {output.Trim()}"];
    }

    /// <summary>写入 .agent 清单（name + 可选 remote 服务器名）</summary>
    public void WriteManifest(string name, string? server) =>
        File.WriteAllText(AgentFile, $"name: {name}\n{(server is null ? "" : $"remote: {server}\n")}");

    /// <summary>读取 .agent 清单键值（文件缺失或键不存在返回 null）</summary>
    public string? Manifest(string key)
    {
        if (!File.Exists(AgentFile)) return null;
        var match = File.ReadLines(AgentFile)
            .FirstOrDefault(line =>
            {
                var idx = line.IndexOf(':');
                return idx > 0 && line[..idx].Trim() == key;
            });
        var sep = match?.IndexOf(':') ?? -1;
        return sep < 0 ? null : match![(sep + 1)..].Trim();
    }

    private bool HasOrigin() =>
        RunCore("remote").Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Contains("origin");

    private void EnsureRepo()
    {
        if (Directory.Exists(Path.Combine(repoDir, ".git"))) return;
        if (!Run("init", "-b", "main"))
            throw new InvalidOperationException("git 初始化失败：请确认已安装 git 且在 PATH 中");
        Run("config", "user.name", "agent");
        Run("config", "user.email", "agent@local");
    }

    private string CurrentBranch()
    {
        var (_, output) = RunCore("branch", "--show-current");
        var branch = output.Trim();
        return branch.Length > 0 ? branch : "main";
    }

    private bool Run(params string[] args) => RunCore(args).ExitCode == 0;

    /// <summary>完全透传：在项目目录直接跑真 git（不重定向输出，交互/颜色/pager 全保留），返回退出码。</summary>
    public int RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            WorkingDirectory = repoDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git 启动失败");
        p.WaitForExit();
        return p.ExitCode;
    }

    private (int ExitCode, string Output) RunCore(params string[] args) => RunIn(repoDir, args);

    private static (int ExitCode, string Output) RunIn(string cwd, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = cwd,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("git 进程启动失败");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            return (p.ExitCode, outTask.GetAwaiter().GetResult() + errTask.GetAwaiter().GetResult());
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return (127, e.Message);
        }
    }
}
