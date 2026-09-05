using System.Collections.Concurrent;
using WTangent.Core;

namespace WTangent.Server.Session;

/// <summary>待回执注册表:confirm(y/n)与 question(文本)共用的阻塞-回执管道。
/// 请求方注册 id → TCS 并把请求推给客户端(UI 渲染弹窗/选项卡),回执到达时按 id 完成 TCS。
/// 同步边界:ConfirmProvider/QuestionProvider 契约是同步问询(LLM 工具循环内联等待用户),
/// async 化需整体改造交互流,此处刻意保持 GetAwaiter().GetResult()。</summary>
public sealed class PendingReplies
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _confirms = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _questions = new();

    /// <summary>注册一个确认请求并等待回执(y/n)。onRequest: 把请求推给客户端(同步写事件桥)。</summary>
    public bool WaitConfirm(string id, Action<string> onRequest)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _confirms[id] = tcs;
        onRequest(id);
        try { return tcs.Task.GetAwaiter().GetResult(); }
        finally { _confirms.TryRemove(id, out _); }
    }

    /// <summary>注册一个提问并等待回答(选项 label 或自由文本;空串=跳过)。</summary>
    public string? WaitQuestion(string id, Action<string> onRequest)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _questions[id] = tcs;
        onRequest(id);
        try { return tcs.Task.GetAwaiter().GetResult(); }
        finally { _questions.TryRemove(id, out _); }
    }

    /// <summary>回执确认(WS confirm / POST /confirm)。id 不存在返回 false。</summary>
    public bool CompleteConfirm(string id, bool allow) =>
        _confirms.TryRemove(id, out var tcs) && tcs.TrySetResult(allow);

    /// <summary>回执回答(WS answer / POST /question)。id 不存在返回 false。</summary>
    public bool CompleteQuestion(string id, string selected) =>
        _questions.TryRemove(id, out var tcs) && tcs.TrySetResult(selected);
}

/// <summary>AgentServer 侧扩展:把"会话查找+通道判断"也收进管道,调用点一行化。</summary>
internal static class PendingRepliesExt
{
    /// <summary>确认流完整链:查会话→推 confirm_req→阻塞等回执。无会话/无事件桥返回 false。</summary>
    public static bool WaitConfirmFor(this PendingReplies replies, string? sessionId,
        Dictionary<string, AgentCore> sessions, string prompt,
        Action<AgentCore, string> push)
    {
        if (sessionId == null || !sessions.TryGetValue(sessionId, out var agent) || agent.Events is null) return false;
        return replies.WaitConfirm(System.Guid.NewGuid().ToString("N")[..8], id => push(agent, id));
    }
}
