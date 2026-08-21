using System.Text.Json;

namespace WTangent.Server.Session;

/// <summary>serve 会话协议（HTTP + SSE）：事件类型与载荷。
/// 每个事件一行 `event: {name}` + 一行 `data: {json}` + 空行。</summary>
public enum SseEventType
{
    /// <summary>正文增量 data={"text":...}</summary>
    MessageDelta,
    /// <summary>思维链增量 data={"text":...}</summary>
    ReasoningDelta,
    /// <summary>工具开始 data={"name":...,"arguments":...}</summary>
    ToolStart,
    /// <summary>工具结束 data={"name":...,"result":...}</summary>
    ToolEnd,
    /// <summary>危险命令确认请求 data={"id":...,"prompt":...}；客户端 POST /confirm 回执</summary>
    ConfirmReq,
    /// <summary>一轮完成 data={"final_text":...}</summary>
    TurnEnd,
    /// <summary>流结束 data={}</summary>
    Done,
}

/// <summary>SSE 事件载荷（data 为 JSON 文本，类型专属）</summary>
public sealed record SseEvent(SseEventType Type, string? Data = null);

/// <summary>SSE 事件 JSON 载荷模型（Data 反序列化用）</summary>
public sealed record SsePayload
{
    public string? Text { get; init; }
    public string? Name { get; init; }
    public string? Arguments { get; init; }
    public string? Result { get; init; }
    public string? Id { get; init; }
    public string? Prompt { get; init; }
    public string? FinalText { get; init; }
}

/// <summary>confirm 回执载荷（客户端 POST /confirm 请求体，两侧共用）</summary>
public sealed record ConfirmReply(string Id, bool Allow);

/// <summary>WebSocket 统一信封（/ws/{sessionId}）：JSON camelCase。
/// 客户端→服务端：ask(text) / cancel / confirm(id, allow)；
/// 服务端→客户端：message_delta / reasoning_delta / tool_start / tool_end / confirm_req / turn_end / done。</summary>
public sealed record WsEnvelope
{
    public string? Type { get; init; }
    public string? Text { get; init; }
    public string? Name { get; init; }
    public string? Arguments { get; init; }
    public string? Result { get; init; }
    public string? Id { get; init; }
    public bool? Allow { get; init; }
    public string? Prompt { get; init; }
    public string? FinalText { get; init; }
}

public static class AgentProtocol
{
    /// <summary>统一 JSON 配置：camelCase + 大小写不敏感（serve/Remote 双向兼容）</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string ToSseName(this SseEventType t) => t switch
    {
        SseEventType.MessageDelta => "message_delta",
        SseEventType.ReasoningDelta => "reasoning_delta",
        SseEventType.ToolStart => "tool_start",
        SseEventType.ToolEnd => "tool_end",
        SseEventType.ConfirmReq => "confirm_req",
        SseEventType.TurnEnd => "turn_end",
        SseEventType.Done => "done",
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    public static SseEventType FromSseName(string name) => name switch
    {
        "message_delta" => SseEventType.MessageDelta,
        "reasoning_delta" => SseEventType.ReasoningDelta,
        "tool_start" => SseEventType.ToolStart,
        "tool_end" => SseEventType.ToolEnd,
        "confirm_req" => SseEventType.ConfirmReq,
        "turn_end" => SseEventType.TurnEnd,
        "done" => SseEventType.Done,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    public static SseEvent MessageDelta(string text) => new(SseEventType.MessageDelta, JsonSerializer.Serialize(new SsePayload { Text = text }, Json));
    public static SseEvent ReasoningDelta(string text) => new(SseEventType.ReasoningDelta, JsonSerializer.Serialize(new SsePayload { Text = text }, Json));
    public static SseEvent ToolStart(string name, string arguments) => new(SseEventType.ToolStart, JsonSerializer.Serialize(new SsePayload { Name = name, Arguments = arguments }, Json));
    public static SseEvent ToolEnd(string name, string result) => new(SseEventType.ToolEnd, JsonSerializer.Serialize(new SsePayload { Name = name, Result = result }, Json));
    public static SseEvent ConfirmReq(string id, string prompt) => new(SseEventType.ConfirmReq, JsonSerializer.Serialize(new SsePayload { Id = id, Prompt = prompt }, Json));
    public static SseEvent TurnEnd(string? finalText) => new(SseEventType.TurnEnd, JsonSerializer.Serialize(new SsePayload { FinalText = finalText }, Json));
    public static SseEvent Done() => new(SseEventType.Done, "{}");

    /// <summary>序列化为 SSE 块：`event: {name}\ndata: {json}\n\n`</summary>
    public static string Serialize(SseEvent e) => $"event: {e.Type.ToSseName()}\ndata: {e.Data}\n\n";
}

/// <summary>SSE 行解析器：逐行喂入，遇完整事件块（event + data + 空行）产出 SseEvent</summary>
public sealed class SseParser
{
    private string? _eventName;
    private string? _data;

    /// <summary>喂入一行（不含换行符）。产出完整事件则返回 true。</summary>
    public bool Feed(string line, out SseEvent result)
    {
        result = null!;
        if (line.Length == 0)
        {
            if (_eventName == null || _data == null) { Reset(); return false; }
            var ev = new SseEvent(AgentProtocol.FromSseName(_eventName), _data);
            Reset();
            result = ev;
            return true;
        }
        if (line.StartsWith("event:", StringComparison.Ordinal)) _eventName = line[6..].Trim();
        else if (line.StartsWith("data:", StringComparison.Ordinal)) _data = line[5..].TrimStart();
        return false;
    }

    private void Reset()
    {
        _eventName = null;
        _data = null;
    }
}
