using System.Runtime.Versioning;
using System.ServiceProcess;
using WTangent.Server.Session;
using WTangent.Server.Store;
using WTangent.Server.Tools;

namespace WTangent.Server;

/// <summary>Windows 服务宿主：SCM 以非交互方式启动 `agent serve` 时走这里——**真服务实现（ServiceBase）**，
/// 不是把普通 exe 硬塞给 sc。register 的 `sc create` 因此有效；终端手动跑 serve 仍走命令行路径。</summary>
public static class AgentServiceHost
{
    [SupportedOSPlatform("windows")]
    public static int Run(string[] args)
    {
        var server = BuildServeServer(args);
        ServiceBase.Run(new AgentService(server));
        return 0;
    }

    /// <summary>极简解析 serve 参数并构建 AgentServer（服务路径专用；终端路径走 ServeCommand）</summary>
    private static AgentServer BuildServeServer(string[] args)
    {
        string? host = null, projects = null, web = null, baseUrl = null, key = null, model = null;
        var port = 8890;
        var mock = false;
        var noWeb = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port": if (int.TryParse(Next(args, ref i), out var p)) port = p; break;
                case "--host": host = Next(args, ref i); break;
                case "--projects": projects = Next(args, ref i); break;
                case "--web": web = Next(args, ref i); break;
                case "--no-web": noWeb = true; break;
                case "--mock": mock = true; break;
                case "--base-url": baseUrl = Next(args, ref i); break;
                case "--key": key = Next(args, ref i); break;
                case "--model": model = Next(args, ref i); break;
            }
        }
        var provider = (mock
            ? new ProviderConfig { Name = "fake", BaseUrl = "http://fake", ApiKey = "fake", DefaultModel = "fake-model" }
            : ModelConfig.ResolveProvider(baseUrl, key, model)) ?? throw new InvalidOperationException("无模型配置（用 --base-url/--key/--model 或先配置缓存）");
        var opts = new AgentOptions
        {
            Provider = provider,
            Tools = [.. ServerTools.Default(provider, mock ? false : null), .. ToolComponentLoader.Load()],
            Llm = mock ? new FakeLlmClient(
                scriptedToolCalls: ["glob **/*.cs", "read_file Core/Agent/AgentCore.cs", "bash 1..10 | ForEach-Object { 'line ' + $_ }"],
                finalText: ["## 结论\n\n服务模式 mock 回复。\n"]) : null,
        };
        return new AgentServer(opts, port, projects, host ?? "127.0.0.1", noWeb ? null : web ?? AgentServer.FindWebDir());
    }

    private static string? Next(string[] args, ref int i) => i + 1 < args.Length ? args[++i] : null;
}

/// <summary>agent serve 的 Windows 服务实现（SCM 接口）</summary>
[SupportedOSPlatform("windows")]
public sealed class AgentService : ServiceBase
{
    private readonly AgentServer _server;
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;

    public AgentService(AgentServer server)
    {
        _server = server;
        ServiceName = "wtangent";
    }

    protected override void OnStart(string[] args) => _task = _server.StartAsync(_cts.Token);

    protected override void OnStop()
    {
        _cts.Cancel();
        _task?.Wait(TimeSpan.FromSeconds(10));
    }

    protected override void OnShutdown() => OnStop();
}
