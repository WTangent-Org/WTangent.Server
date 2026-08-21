using System.Text.Json;
using WTangent.Server.Session;

namespace WTangent.Server.Store;

/// <summary>token 计量（模仿 dsh-token-meter）：无真实 usage 时的固定密度估算 + 会话级累计。
/// 估算：文本 ceil(len/4) + 4 结构开销，每消息 +4 role 开销；真实 usage 到达后以真实值为准累加。</summary>
public static class TokenMeter
{
    public const int CharsPerToken = 4;
    private const int BlockOverhead = 4;

    public static int EstimateText(string text) =>
        text.Length == 0 ? 0 : (int)Math.Ceiling(text.Length / (double)CharsPerToken) + BlockOverhead;

    /// <summary>估算一条消息：内容 + role 框架开销</summary>
    public static int EstimateMessage(ChatMessage m) =>
        EstimateText(m.Content) + (m.ReasoningContent is { Length: > 0 } r ? EstimateText(r) : 0) + 4;

    /// <summary>估算工具 schema（工具定义 JSON 序列化长度）</summary>
    public static int EstimateTools(IEnumerable<object> tools) =>
        tools.Sum(t =>
        {
            try { return EstimateText(JsonSerializer.Serialize(t)); }
            catch { return BlockOverhead; }
        });
}

/// <summary>会话级 token 累加器：真实 usage（LlmResponse/流尾 chunk）优先，无则回退估算。</summary>
public sealed class SessionUsage
{
    private TokenUsage _real = new();
    private int _estimated;
    private readonly Lock _lock = new();

    /// <summary>当前累计用量（真实 + 估算兜底）</summary>
    public TokenUsage Total
    {
        get
        {
            lock (_lock)
            {
                var est = new TokenUsage(InputTokens: _estimated);
                return _real + est;
            }
        }
    }

    /// <summary>真实 usage 到达：累加并清零估算（该轮已有真实值）</summary>
    public void AddReal(TokenUsage usage)
    {
        lock (_lock)
        {
            _real += usage;
            _estimated = 0;
        }
    }

    /// <summary>无真实 usage 时的估算兜底</summary>
    public void AddEstimated(int tokens)
    {
        lock (_lock) { _estimated += tokens; }
    }
}
