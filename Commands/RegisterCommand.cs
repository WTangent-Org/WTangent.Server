using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WTangent.Server.Commands;


/// <summary>register/unregister：把「agent &lt;剩余参数&gt;」注册为开机自启（Win sc / Linux systemd）。
/// 例：agent register serve --host 0.0.0.0 --projects %APPDATA%\agent\projects --web web —— 盒子重启自动起 serve。</summary>
[AgentComponent]
public sealed class RegisterCommand : Command
{
    public RegisterCommand() : base("register", "注册开机自启（后面参数原样透传给 agent，缺省 serve）")
    {
        var args = new Argument<string[]>("args") { Arity = ArgumentArity.ZeroOrMore, Description = "要自启的命令参数（如 serve --host 0.0.0.0）" };
        Add(args);
        SetAction(pr => RegisterHandler.Run(pr.GetValue(args) ?? []));
    }
}

[AgentComponent]
public sealed class UnregisterCommand : Command
{
    public UnregisterCommand() : base("unregister", "移除开机自启服务")
    {
        SetAction(_ => RegisterHandler.Unregister());
    }
}

/// <summary>跨平台开机自启：Windows → sc 服务；Linux → systemd。服务名固定 agent。</summary>
public static class RegisterHandler
{
    private const string ServiceName = "wtangent";

    public static int Run(string[] args)
    {
        var cmdArgs = args.Length > 0 ? args : ["serve"];
        if (cmdArgs[0] == "serve" && !cmdArgs.Contains("--service"))
            cmdArgs = [cmdArgs[0], "--service", .. cmdArgs[1..]];   // SCM 启动靠 --service 走 ServiceBase 实现
        var bin = $"\"{Environment.ProcessPath ?? "wtangent"}\" {string.Join(' ', cmdArgs)}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { Run("sc.exe", ["stop", ServiceName]); } catch { /* 未安装忽略 */ }
            Run("sc.exe", ["create", ServiceName, $"binPath={bin}", "start=auto", "DisplayName=", "Agent serve"]);
            Run("sc.exe", ["start", ServiceName]);
            Console.WriteLine($"[register] 已注册并启动 Windows 服务 {ServiceName}: {bin}");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var unit = $$"""
                [Unit]
                Description=Agent serve
                After=network.target

                [Service]
                Type=simple
                ExecStart={{bin}}
                Restart=on-failure

                [Install]
                WantedBy=multi-user.target
                """;
            Run("sudo", ["tee", $"/etc/systemd/system/{ServiceName}.service"], unit);
            Run("sudo", ["systemctl", "daemon-reload"]);
            Run("sudo", ["systemctl", "enable", ServiceName]);
            Run("sudo", ["systemctl", "start", ServiceName]);
            Console.WriteLine($"[register] 已注册 systemd 服务 {ServiceName}");
        }
        else
        {
            Console.Error.WriteLine("不支持的平台");
            return 1;
        }
        return 0;
    }

    public static int Unregister()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try { Run("sc.exe", ["stop", ServiceName]); } catch { }
                Run("sc.exe", ["delete", ServiceName]);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Run("sudo", ["systemctl", "stop", ServiceName]);
                Run("sudo", ["systemctl", "disable", ServiceName]);
                Run("sudo", ["rm", "-f", $"/etc/systemd/system/{ServiceName}.service"]);
                Run("sudo", ["systemctl", "daemon-reload"]);
            }
            Console.WriteLine($"[unregister] 已移除 {ServiceName}");
        }
        catch { /* 服务不存在忽略 */ }
        return 0;
    }

    private static void Run(string file, string[] args, string? stdin = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new Exception($"无法启动 {file}");
        if (stdin != null) p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        p.WaitForExit();
        if (p.ExitCode == 0) return;
        var err = p.StandardError.ReadToEnd();
        throw new Exception($"{file} 失败 ({p.ExitCode}): {err}");
    }
}
