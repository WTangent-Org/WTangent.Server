using WTangent.Server.Store;

namespace WTangent.Server.Session;

/// <summary>Agent 配置</summary>
public record AgentOptions
{
    public required ProviderConfig Provider { get; init; }
    public string SystemPrompt { get; init; } =
        "You are a helpful coding assistant. Before implementing anything, follow this order:\n" +
        "1. Search for existing capabilities first: library APIs, framework built-ins, official docs/examples, and existing code in the codebase (use the grep/glob/read_file tools).\n" +
        "2. Prefer composing or reusing existing solutions over writing new code.\n" +
        "3. Verify assumptions with minimal tests rather than guessing.\n" +
        "4. If requirements are ambiguous, ask for clarification instead of assuming.";
    public string? Model { get; init; }
    public int MaxHistory { get; init; } = 50;
    /// <summary>是否启用工具调用，默认开</summary>
    public bool EnableTools { get; init; } = true;
    /// <summary>单次问答最大工具调用轮数，防死循环</summary>
    public int MaxToolRounds { get; init; } = 8;
    /// <summary>工具结果最大回喂长度（防大输出撑爆上下文）</summary>
    public int MaxToolResultChars { get; init; } = 20_000;
    /// <summary>上下文预算（估算总字符），历史越接近预算则动态收紧工具截断</summary>
    public int ContextBudgetChars { get; init; } = 120_000;
    /// <summary>初始历史（续聊时从持久化恢复），不含 system</summary>
    public IEnumerable<ChatMessage>? InitialHistory { get; init; }
    /// <summary>所有工具（必须显式组装：ServerTools.Default() 内置 + 组件扩展；扩展点显式化）</summary>
    public required IReadOnlyList<ITool> Tools { get; init; }
    /// <summary>工具索引（Name → ITool）</summary>
    public Dictionary<string, ITool> ToolIndex => Tools.ToDictionary(t => t.Name);
    /// <summary>是否流式输出（MessageDelta 事件），默认开</summary>
    public bool Stream { get; init; } = true;
    /// <summary>LLM 客户端（缺省用 Provider 构造真实实现；测试可注入假实现）</summary>
    public ILlmClient? Llm { get; init; }
}

/// <summary>Agent 会话事件回调（Pi 风格：turn / tool / message 生命周期）</summary>
public interface IAgentEvents
{
    /// <summary>一轮开始（LLM 收到 prompt）</summary>
    void OnTurnStart() { }
    /// <summary>LLM 回复增量（流式文本）</summary>
    void OnMessageDelta(string delta) { }
    /// <summary>思维链增量（reasoning 模型，可选显示）</summary>
    void OnReasoningDelta(string delta) { }
    /// <summary>工具开始执行</summary>
    void OnToolStart(string name, string arguments) { }
    /// <summary>工具执行完成</summary>
    void OnToolEnd(string name, string result) { }
    /// <summary>一轮完成（含工具结果）</summary>
    void OnTurnEnd(string? finalText) { }
}

/// <summary>一个独立可用的 LLM Agent 会话：维护历史，支持工具调用与流式事件</summary>
public class AgentCore(AgentOptions opts)
{
    /// <summary>动态截断保底阈值（预算耗尽/超预算时也至少保留此长度）</summary>
    private const int MinToolResultChars = 2000;
    private readonly ILlmClient _llm = opts.Llm ?? new LlmClient(opts.Provider);
    private readonly List<ChatMessage> _history =
        [new() { Role = MessageRole.System, Content = opts.SystemPrompt }, .. opts.InitialHistory ?? []];
    public IAgentEvents? Events { get; set; }
    /// <summary>当前对话历史（含 system）</summary>
    public IReadOnlyList<ChatMessage> History => _history;
    /// <summary>本次问答的完整思维链（跨轮累计，供 REPL 折叠查看）</summary>
    public string? LastReasoning { get; private set; }
    /// <summary>本会话累计 token 用量（真实 usage 优先，无则估算兜底；含工具 schema 估算）</summary>
    public SessionUsage Usage { get; } = new();

    /// <summary>发送一条用户消息，返回 LLM 回复内容（支持工具循环与流式）</summary>
    public async Task<string?> AskAsync(string prompt, CancellationToken ct = default)
    {
        LastReasoning = null;
        return await RunWithToolsAsync([new ChatMessage { Role = MessageRole.User, Content = prompt }], ct);
    }

    /// <summary>工具调用主循环：写入输入消息 → 请求(流式) → 执行工具 → 结果回喂 → 直到 LLM 不再调工具</summary>
    private async Task<string?> RunWithToolsAsync(IReadOnlyList<ChatMessage> input, CancellationToken ct)
    {
        _history.AddRange(input);
        TrimHistory();
        object[]? tools = opts.EnableTools ? [.. opts.Tools.Select(t => t.Definition)] : null;
        for (var round = 0; round < opts.MaxToolRounds; round++)
        {
            Events?.OnTurnStart();
            var (content, reasoning, calls) = await RequestAsync(tools, ct);

            // 累计本次思维链（供 REPL 折叠查看）
            if (reasoning.Length > 0)
                LastReasoning = reasoning;

            if (calls.Count > 0)
            {
                // 记录 assistant 的工具调用请求（含已流式文本 + 思维链，工具调用须回传 reasoning_content）
                _history.Add(new ChatMessage
                {
                    Role = MessageRole.Assistant,
                    Content = content,
                    ReasoningContent = reasoning,
                    ToolCalls = calls,
                });
                await ExecuteToolsAsync(calls, ct);
                TrimHistory();
                Events?.OnTurnEnd(null);
                continue; // 下一轮，让 LLM 基于工具结果推理
            }

            // 无工具调用：最终回复
            if (content.Length > 0)
                _history.Add(new ChatMessage { Role = MessageRole.Assistant, Content = content });
            TrimHistory();
            Events?.OnTurnEnd(content.Length > 0 ? content : null);
            return content.Length > 0 ? content : null;
        }
        return "[已达最大工具轮数，停止]";
    }

    /// <summary>请求一轮 LLM（流式/非流式统一）：产出正文 + 思维链 + 工具调用。流式时逐块触发事件。
    /// token 计量：真实 usage（流尾 chunk / 响应 usage）累加；无则按 4 chars/token 估算该轮输入。</summary>
    private async Task<(string Content, string Reasoning, List<ToolCall> Calls)> RequestAsync(object[]? tools, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        var reasoningSb = new System.Text.StringBuilder();
        var calls = new List<ToolCall>();
        var gotUsage = false;
        if (opts.Stream)
        {
            await foreach (var chunk in _llm.ChatStreamAsync(_history, opts.Model, tools, ct))
            {
                // 流式中间块可能带全零/部分 usage，只取有效块（DeepSeek 流尾上报完整值）
                if (chunk.Usage is { } u && (u.InputTokens > 0 || u.OutputTokens > 0 || u.CacheReadTokens > 0))
                {
                    Usage.AddReal(u);
                    gotUsage = true;
                }
                if (chunk.Text != null)
                {
                    sb.Append(chunk.Text);
                    Events?.OnMessageDelta(chunk.Text);
                }
                if (chunk.ReasoningDelta != null)
                {
                    reasoningSb.Append(chunk.ReasoningDelta);
                    Events?.OnReasoningDelta(chunk.ReasoningDelta);
                }
                if (chunk.ToolCall != null)
                    calls.Add(chunk.ToolCall);
            }
        }
        else
        {
            var resp = await _llm.ChatAsync(_history, opts.Model, tools, ct);
            if (resp.Usage.InputTokens > 0 || resp.Usage.OutputTokens > 0)
            {
                Usage.AddReal(resp.Usage);
                gotUsage = true;
            }
            if (resp.Content != null) sb.Append(resp.Content);
            if (resp.ReasoningContent != null) reasoningSb.Append(resp.ReasoningContent);
            if (resp.ToolCalls is { Count: > 0 }) calls.AddRange(resp.ToolCalls);
        }
        // 无真实 usage：估算兜底（输入 = system + 历史 + 工具 schema）
        if (gotUsage) return (sb.ToString(), reasoningSb.ToString(), calls);
        var est = TokenMeter.EstimateText(opts.SystemPrompt)
                  + _history.Skip(1).Sum(TokenMeter.EstimateMessage)
                  + (tools is { Length: > 0 } ? TokenMeter.EstimateTools(tools) : 0);
        Usage.AddEstimated(est);
        return (sb.ToString(), reasoningSb.ToString(), calls);
    }

    /// <summary>并行执行工具调用（保持原顺序回喂结果），动态截断后写入历史</summary>
    private async Task ExecuteToolsAsync(List<ToolCall> calls, CancellationToken ct)
    {
        var results = await Task.WhenAll(calls.Select(async call =>
        {
            var tool = opts.ToolIndex.GetValueOrDefault(call.Name);
            string result;
            if (tool == null)
            {
                result = $"[未知工具: {call.Name}]";
            }
            else
            {
                Events?.OnToolStart(call.Name, call.Arguments);
                result = await tool.RunAsync(call.Arguments, ct);
                Events?.OnToolEnd(call.Name, result);
            }
            return (call, result);
        }));
        var (toolLimit, nearLimit) = CurrentToolLimit();
        var remaining = toolLimit; // 本轮所有工具结果共享总预算（总量封顶）
        foreach (var (call, r) in results)
        {
            // 每条至多 remaining，总量用完后后续结果直接截断到提示
            var result = r.Length > remaining
                ? Truncate(r, remaining, nearLimit || remaining <= 0)
                : r;
            remaining = Math.Max(0, remaining - result.Length);
            _history.Add(new ChatMessage { Role = MessageRole.Tool, ToolCallId = call.Id, Content = result });
        }
    }

    /// <summary>当前动态工具截断阈值：随历史增长线性收紧；返回 (limit, 是否接近预算)</summary>
    private (int Limit, bool NearLimit) CurrentToolLimit()
    {
        var used = _history.Sum(m => m.Content.Length + (m.ReasoningContent?.Length ?? 0));
        var budget = opts.ContextBudgetChars;
        if (budget <= 0) return (opts.MaxToolResultChars, false);   // 未启用预算：固定上限
        if (used >= budget) return (MinToolResultChars, true);      // 已超预算：直接给保底，不再放宽
        // 剩余占比 0~1，阈值随剩余缩减（最低保底 MinToolResultChars）
        var ratio = (double)(budget - used) / budget;
        var limit = Math.Max(MinToolResultChars, (int)(opts.MaxToolResultChars * ratio));
        return (limit, ratio < 0.3);
    }

    /// <summary>截断工具结果，按接近程度给分级提示（引导 AI 收敛）</summary>
    private static string Truncate(string text, int limit, bool nearLimit)
    {
        var cut = text[..limit] + $"\n...[已截断，共 {text.Length} 字符]";
        return !nearLimit ? cut : cut + "\n[提示] 上下文/工具结果预算紧张，后续请改用更精确的查询（如 grep 加 limit、read_file 分页、缩小 glob 范围）。";
    }

    /// <summary>清空对话历史（保留 system）</summary>
    public void Reset()
    {
        _history.RemoveRange(1, _history.Count - 1);
    }

    private void TrimHistory()
    {
        if (_history.Count <= opts.MaxHistory) return;
        // 目标：裁掉最旧消息直到不超过 MaxHistory，但按"完整工具轮次"为单位，
        // 避免拆开 assistant(tool_calls) 与其 tool 结果（否则产生孤立 tool → API 400）。
        // 新起点默认从末尾向前数 MaxHistory 条；若起点是 tool 消息，则回溯到其 assistant 之前。
        var start = _history.Count - opts.MaxHistory;
        if (start < 1) return;
        // 若起点落在 tool 消息上，回溯到该轮次的 assistant(tool_calls) 之前，整体保留这轮
        if (_history[start].Role == MessageRole.Tool)
        {
            var i = start;
            while (i > 1 && _history[i - 1].Role == MessageRole.Tool) i--;     // 回退连续 tool
            if (i > 1 && _history[i - 1].Role == MessageRole.Assistant) i--;   // 回退到 assistant
            start = i;
        }
        if (start > 1)
            _history.RemoveRange(1, start - 1);
    }
}
