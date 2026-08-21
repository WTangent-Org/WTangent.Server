namespace WTangent.Server.Session;

/// <summary>Agent 会话客户端抽象：Local=进程内 AgentCore，Remote=HTTP 连 serve。
/// TUI 只依赖此接口，本地/远程透明切换。</summary>
public interface IAgentClient
{
    /// <summary>会话事件回调（thinking/正文/工具/回合生命周期）</summary>
    IAgentEvents? Events { get; set; }

    /// <summary>发送一条用户消息，返回 LLM 回复内容（支持工具循环与流式事件）</summary>
    Task<string?> AskAsync(string prompt, CancellationToken ct = default);

    /// <summary>清空对话历史（保留 system）</summary>
    Task ResetAsync(CancellationToken ct = default);
}
