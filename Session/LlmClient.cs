using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace WTangent.Server.Session;

/// <summary>模型提供商配置</summary>
public record ProviderConfig
{
    public string Name { get; init; } = "";
    /// <summary>OpenAI 兼容的 base URL，如 https://api.deepseek.com/v1</summary>
    public string BaseUrl { get; init; } = "";
    public string ApiKey { get; init; } = "";
    /// <summary>默认模型名，如 deepseek-chat</summary>
    public string DefaultModel { get; init; } = "";
    /// <summary>推理变体：Default（不传）/ low / high / max（reasoning_effort）</summary>
    public string Variants { get; init; } = "Default";
}

public enum MessageRole
{
    System,
    User,
    Assistant,
    Tool,
}

public class ChatMessage
{
    public MessageRole Role { get; init; } = MessageRole.User;
    public string Content { get; init; } = "";
    /// <summary>assistant 消息的思维链（reasoning 模型，工具调用时须原样回传）</summary>
    public string? ReasoningContent { get; init; }
    /// <summary>assistant 消息的工具调用（OpenAI tool_calls）</summary>
    public List<ToolCall>? ToolCalls { get; init; }
    /// <summary>tool 角色的工具调用 ID</summary>
    public string? ToolCallId { get; init; }
}

/// <summary>工具调用（OpenAI function calling）</summary>
public class ToolCall
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Arguments { get; init; } = ""; // JSON 字符串
}

public class LlmResponse
{
    public string? Content { get; init; }
    public string? ReasoningContent { get; init; }
    public List<ToolCall>? ToolCalls { get; init; }
    public string? Model { get; init; }
    /// <summary>token 用量（不相交：InputTokens 已减去缓存命中）</summary>
    public TokenUsage Usage { get; init; } = new();
}

/// <summary>流式产出：Text=文本增量，ToolCall=完成的工具调用，ReasoningDelta=思维链增量。
/// Usage=流尾 usage 块（仅当请求带 stream_options.include_usage 且提供商上报时出现一次）。</summary>
public class LlmStreamChunk
{
    public string? Text { get; init; }
    public string? ReasoningDelta { get; init; }
    public ToolCall? ToolCall { get; init; }
    public TokenUsage? Usage { get; init; }
}

/// <summary>LLM 客户端抽象：真实 HTTP 实现或假实现（测试/模拟）</summary>
public interface ILlmClient
{
    Task<LlmResponse> ChatAsync(IEnumerable<ChatMessage> messages, string? model = null, IEnumerable<object>? tools = null, CancellationToken ct = default);
    IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(IEnumerable<ChatMessage> messages, string? model = null, IEnumerable<object>? tools = null, CancellationToken ct = default);
    Task<string[]> ListModelsAsync(CancellationToken ct = default);
}

/// <summary>OpenAI 兼容的 LLM 客户端（支持 deepseek 等）</summary>
public class LlmClient(ProviderConfig provider) : ILlmClient
{
    private readonly HttpClient _http = InitHttp(provider);

    private static HttpClient InitHttp(ProviderConfig provider)
    {
        var http = Http.New(TimeSpan.FromMinutes(3));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        return http;
    }

    /// <summary>发送对话，返回回复。可传 tools（OpenAI function calling）</summary>
    public async Task<LlmResponse> ChatAsync(
        IEnumerable<ChatMessage> messages,
        string? model = null,
        IEnumerable<object>? tools = null,
        CancellationToken ct = default)
    {
        var body = BuildRequestBody(messages, model, tools, stream: false);
        using var resp = await PostChatAsync(body, stream: false, model, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);

        var choice = doc.RootElement.GetProperty("choices")[0];
        var msg = choice.TryGetProperty("message", out var m) ? m : default;
        var content = GetString(msg, "content");
        var reasoningContent = GetString(msg, "reasoning_content");

        // 解析 tool_calls
        List<ToolCall>? toolCalls = null;
        if (msg.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
            toolCalls = [.. tc.EnumerateArray().Where(c => c.TryGetProperty("function", out _)).Select(ParseToolCall)];

        var usage = doc.RootElement.TryGetProperty("usage", out var u) ? u : (JsonElement?)null;

        return new LlmResponse
        {
            Content = content,
            ReasoningContent = reasoningContent,
            ToolCalls = toolCalls,
            Model = GetString(doc.RootElement, "model"),
            Usage = ParseUsage(usage),
        };
    }

    /// <summary>解析 usage 为不相交计数（dsh 语义）：prompt_tokens 含缓存命中，InputTokens 减去；
    /// cacheRead = prompt_tokens_details.cached_tokens ?? prompt_cache_hit_tokens；reasoning = completion_tokens_details.reasoning_tokens。</summary>
    private static TokenUsage ParseUsage(JsonElement? usage)
    {
        if (usage is not { } u || u.ValueKind != JsonValueKind.Object) return new();
        var prompt = GetInt(u, "prompt_tokens");
        var completion = GetInt(u, "completion_tokens");
        var cacheRead = 0;
        if (u.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
            cacheRead = GetInt(details, "cached_tokens");
        if (cacheRead == 0) cacheRead = GetInt(u, "prompt_cache_hit_tokens");
        var reasoning = 0;
        if (u.TryGetProperty("completion_tokens_details", out var cDetails) && cDetails.ValueKind == JsonValueKind.Object)
            reasoning = GetInt(cDetails, "reasoning_tokens");
        return new TokenUsage(
            InputTokens: Math.Max(0, prompt - cacheRead),
            OutputTokens: completion,
            CacheReadTokens: cacheRead,
            ReasoningTokens: reasoning);
    }

    private static int GetInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    /// <summary>流式对话：SSE 逐块产出（文本 delta / 工具调用片段），供 MessageDelta 事件。</summary>
    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
        IEnumerable<ChatMessage> messages,
        string? model = null,
        IEnumerable<object>? tools = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = BuildRequestBody(messages, model, tools, stream: true);
        using var resp = await PostChatAsync(body, stream: true, model, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var toolCalls = new Dictionary<int, ToolCallAcc>();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(':')) continue;
            if (!line.StartsWith("data:")) continue;
            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;

            using var doc = JsonDocument.Parse(data);
            // usage 块（include_usage=true 时 DeepSeek 在流尾上报一次；该块 choices 非空但 delta 为空）
            if (doc.RootElement.TryGetProperty("usage", out var usageEl))
                yield return new LlmStreamChunk { Usage = ParseUsage(usageEl) };
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                continue;
            var choice = choices[0];
            if (!choice.TryGetProperty("delta", out var delta)) continue;

            var text = GetString(delta, "content") ?? "";
            if (text.Length > 0) yield return new LlmStreamChunk { Text = text };

            // reasoning 模型的思维链增量
            var rt = GetString(delta, "reasoning_content") ?? "";
            if (rt.Length > 0) yield return new LlmStreamChunk { ReasoningDelta = rt };

            if (delta.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in tc.EnumerateArray())
                {
                    if (!call.TryGetProperty("index", out var idxEl)) continue;
                    var idx = idxEl.GetInt32();
                    if (!toolCalls.TryGetValue(idx, out var acc))
                    {
                        acc = new ToolCallAcc();
                        toolCalls[idx] = acc;
                    }
                    if (call.TryGetProperty("id", out var id)) acc.Id = id.GetString() ?? acc.Id;
                    if (!call.TryGetProperty("function", out var fn)) continue;
                    if (fn.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                        acc.Name += nm.GetString();
                    if (fn.TryGetProperty("arguments", out var ar) && ar.ValueKind == JsonValueKind.String)
                        acc.Args.Append(ar.GetString());
                }
            }

            // 终止工具调用片段
            if (!choice.TryGetProperty("finish_reason", out var fr) || fr.ValueKind != JsonValueKind.String) continue;
            
            var reason = fr.GetString(); 
            if (reason is not ("tool_calls" or "stop")) continue; 
            foreach (var (_, acc) in toolCalls) 
                if (acc.Args.Length > 0) 
                    yield return new LlmStreamChunk { ToolCall = new ToolCall { Id = acc.Id, Name = acc.Name, Arguments = acc.Args.ToString() } };
            toolCalls.Clear();
        }
    }

    private Dictionary<string, object?> BuildRequestBody(
        IEnumerable<ChatMessage> messages, string? model, IEnumerable<object>? tools, bool stream)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model ?? provider.DefaultModel,
            ["messages"] = messages.Select(ToApiMessage).ToArray(),
        };
        if (tools != null) body["tools"] = tools;
        if (stream)
        {
            body["stream"] = true;
            // DeepSeek：请求流式 usage（SSE 尾部上报一次，含 prompt_cache_hit_tokens）
            body["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true };
        }
        // 推理变体（Default 不传，交给模型默认）
        if (provider.Variants is { Length: > 0 } v && !v.Equals("Default", StringComparison.OrdinalIgnoreCase))
            body["reasoning_effort"] = v.ToLowerInvariant();
        return body;
    }

    /// <summary>POST /chat/completions（流式/非流式共用）：请求构建 + 发送 + 状态检查。调用方负责 Dispose。</summary>
    private async Task<HttpResponseMessage> PostChatAsync(Dictionary<string, object?> body, bool stream, string? model, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{provider.BaseUrl.TrimEnd('/')}/chat/completions");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct);
        await EnsureSuccessAsync(resp, model ?? provider.DefaultModel);
        return resp;
    }

    /// <summary>取 JSON 字符串字段（缺失/非字符串 → null），message/delta 解析共用</summary>
    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>解析单个 tool_call（非流式响应用）</summary>
    private static ToolCall ParseToolCall(JsonElement call)
    {
        var fn = call.GetProperty("function");
        return new ToolCall
        {
            Id = call.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            Name = fn.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
            Arguments = fn.TryGetProperty("arguments", out var ar) ? ar.GetString() ?? "{}" : "{}",
        };
    }

    /// <summary>非 2xx 时读取错误体并抛带详情的异常（利于定位 400 是超限还是字段问题）</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, string model)
    {
        if (resp.IsSuccessStatusCode) return;
        string detail = "";
        try { detail = await resp.Content.ReadAsStringAsync(); } catch { }
        var code = (int)resp.StatusCode;
        throw new HttpRequestException(
            $"LLM 请求失败: {code} {resp.ReasonPhrase} (model={model})\n{detail}");
    }

    /// <summary>流式工具调用累积器（可变引用，跨 chunk 累加 id/name/arguments）</summary>
    private sealed class ToolCallAcc
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public StringBuilder Args { get; } = new();
    }

    private static object ToApiMessage(ChatMessage m)
    {
        var d = new Dictionary<string, object?>
        {
            ["role"] = m.Role.ToString().ToLowerInvariant(),
            ["content"] = m.Content,
        };
        if (m.ToolCallId != null) d["tool_call_id"] = m.ToolCallId;
        // reasoning 模型：工具调用的 assistant 消息必须回传思维链，否则 400
        if (m.ReasoningContent != null) d["reasoning_content"] = m.ReasoningContent;
        if (m.ToolCalls is { Count: > 0 })
        {
            d["tool_calls"] = m.ToolCalls.Select(t => new
            {
                id = t.Id,
                type = "function",
                function = new { name = t.Name, arguments = t.Arguments },
            }).ToArray();
        }
        return d;
    }

    /// <summary>提供商支持的推理变体（reasoning_effort 取值）。DeepSeek 官方文档：low/high/max
    /// （medium/xhigh 会映射为 high）；其余按 OpenAI 标准 low/medium/high。Default = 不传该参数。</summary>
    public static string[] VariantsFor(string baseUrl) =>
        baseUrl.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
            ? ["Default", "low", "high", "max"]
            : ["Default", "low", "medium", "high"];

    /// <summary>获取可用模型列表（OpenAI 兼容 /models）</summary>
    public async Task<string[]> ListModelsAsync(CancellationToken ct = default)
    {
        var url = $"{provider.BaseUrl.TrimEnd('/')}/models";
        using var resp = await _http.GetAsync(url, ct);
        await EnsureSuccessAsync(resp, provider.Name);
        var text = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        return !doc.RootElement.TryGetProperty("data", out var data)
            ? []
            : [.. data.EnumerateArray()
            .Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(id => id != null)
            .Select(id => id!)];
    }
}
