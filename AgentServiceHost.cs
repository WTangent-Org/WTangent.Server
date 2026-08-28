using System.Runtime.Versioning;
using System.ServiceProcess;
using WTangent.Server.Session;

namespace WTangent.Server;

/// <summary>Windows 服务宿主：SCM 以非交互方式启动 `agent serve` 时走这里——**真服务实现（ServiceBase）**，
/// 不是把普通 exe 硬塞给 sc。register 的 `sc create` 因此有效；终端手动跑 serve 仍走命令行路径。
/// 参数解析/组装归 ServeCommand（--service 分流的单一来源）；本类只做 SCM 生命周期接线。</summary>
public static class AgentServiceHost
{
    [SupportedOSPlatform("windows")]
    public static int Run(AgentServer server)
    {
        ServiceBase.Run(new AgentService(server));
        return 0;
    }
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
        _task?.Wait(TimeSpan.FromSeconds(10));   // 同步边界：SCM 的 OnStop 是同步 API，必须等住再返回
    }

    protected override void OnShutdown() => OnStop();
}
