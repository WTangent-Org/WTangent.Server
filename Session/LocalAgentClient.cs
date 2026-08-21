namespace WTangent.Server.Session;

/// <summary>进程内 Agent 客户端：直接包 AgentCore，无网络（本地终端默认路径）</summary>
public sealed class LocalAgentClient(AgentOptions opts) : IAgentClient
{
    private readonly AgentCore _core = new(opts);

    public IAgentEvents? Events
    {
        get => _core.Events;
        set => _core.Events = value;
    }

    public Task<string?> AskAsync(string prompt, CancellationToken ct = default) =>
        _core.AskAsync(prompt, ct);

    public Task ResetAsync(CancellationToken ct = default)
    {
        _core.Reset();
        return Task.CompletedTask;
    }
}
