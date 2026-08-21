namespace WTangent.Server.Session;

/// <summary>一次 LLM 调用的 token 用量（**不相交计数**，对齐 dsh 语义）：
/// DeepSeek 的 prompt_tokens 包含缓存命中（prompt_tokens = cache_hit + cache_miss），
/// InputTokens 已减去命中；CacheReadTokens/ReasoningTokens 仅在提供商上报时非零。</summary>
public sealed record TokenUsage(int InputTokens = 0, int OutputTokens = 0, int CacheReadTokens = 0, int ReasoningTokens = 0)
{
    public static TokenUsage operator +(TokenUsage a, TokenUsage b) => new(
        a.InputTokens + b.InputTokens,
        a.OutputTokens + b.OutputTokens,
        a.CacheReadTokens + b.CacheReadTokens,
        a.ReasoningTokens + b.ReasoningTokens);
}
