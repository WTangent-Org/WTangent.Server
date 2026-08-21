using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WTangent.Server.Store;

namespace WTangent.Server.Tools;

/// <summary>网络搜索工具：web_search。走 DeepSeek 官方 Anthropic 兼容端点（/anthropic/v1/messages）的原生
/// web_search_20250305 server tool——复用 config.json 里已有 API key，无第三方依赖。
/// 每次搜索消耗一次模型调用（token），结果返回 url/title/snippet 列表。</summary>
public sealed class WebSearchTool : ITool
{
    private static readonly HttpClient Http = WTangent.Core.Http.New(TimeSpan.FromSeconds(60));

    private const string AnthropicBase = "https://api.deepseek.com/anthropic/v1";
    private const string ApiVersion = "2023-06-01";
    private const string Model = "deepseek-v4-flash";
    private const int MaxTokens = 4096;
    private const int MaxUses = 5;
    private const int MaxResults = 8;

    public string Name => "web_search";

    public object Definition => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = "联网搜索（走 DeepSeek 原生搜索，每次消耗一次模型调用）。返回相关网页的 url/title/snippet。需要最新信息、外部资料、或代码库外的事实核查时使用。",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "搜索查询词" },
                },
                required = new[] { "query" },
            },
        },
    };

    public async Task<string> RunAsync(string arguments, CancellationToken ct = default)
    {
        var query = ToolArgs.GetString(arguments, "query");
        if (string.IsNullOrWhiteSpace(query)) return "[web_search] 缺少 query 参数";
        var provider = ConfigStore.LoadActive();
        if (provider is null || string.IsNullOrEmpty(provider.ApiKey))
            return "[web_search] 未配置 API key（config.json 提供商缺少 ApiKey）";
        try
        {
            var body = new
            {
                model = Model,
                max_tokens = MaxTokens,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new[]
                        {
                            new { type = "text", text = $"Perform a web search for the query: {query}" },
                        },
                    },
                },
                tools = new[]
                {
                    new { type = "web_search_20250305", name = "web_search", max_uses = MaxUses },
                },
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{AnthropicBase}/messages");
            req.Headers.TryAddWithoutValidation("x-api-key", provider.ApiKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
            req.Headers.TryAddWithoutValidation("user-agent", "agent/0.1");
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return $"[web_search] HTTP {(int)resp.StatusCode}: {text[..Math.Min(300, text.Length)]}";
            using var doc = JsonDocument.Parse(text);
            var results = ParseResults(doc.RootElement);
            return FormatOutput(results);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return $"[web_search] 请求失败: {e.Message}";
        }
    }

    /// <summary>dsh 风格输出：markdown 来源列表（[title](url) + snippet + 日期）+ 引用指令。</summary>
    private static string FormatOutput(List<SearchHit> results)
    {
        if (results.Count == 0) return "[web_search] 无结果";
        var lines = results.Select(r =>
        {
            var label = r.Title.Length > 0 ? r.Title : r.Url;
            var meta = new List<string>();
            if (r.Snippet.Length > 0) meta.Add(r.Snippet);
            if (r.PublishedAt.Length > 0) meta.Add($"({r.PublishedAt})");
            var suffix = meta.Count > 0 ? $" — {string.Join(" ", meta)}" : "";
            return $"- [{label}]({r.Url}){suffix}";
        });
        return $"Sources:\n{string.Join("\n", lines)}\n\n请以 markdown 链接引用上述相关 URL。需要完整内容时用 web_fetch 抓取具体结果。";
    }

    /// <summary>解析 Anthropic Messages 响应：web_search_tool_result 块的 url/title + text 块 citations 的 snippet</summary>
    private static List<SearchHit> ParseResults(JsonElement root)
    {
        var results = new List<SearchHit>();
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return results;
        // 先收集 citations：url → cited_text（text 块的引文片段即 snippet）
        var snippets = new Dictionary<string, string>();
        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var t) || t.GetString() != "text") continue;
            if (!block.TryGetProperty("citations", out var cites) || cites.ValueKind != JsonValueKind.Array) continue;
            foreach (var cite in cites.EnumerateArray())
            {
                var url = cite.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var cited = cite.TryGetProperty("cited_text", out var c) ? c.GetString() ?? "" : "";
                if (url.Length > 0 && cited.Length > 0)
                    snippets.TryAdd(url, cited);
            }
        }
        // 再收集 web_search_result 块
        var seen = new HashSet<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var t) || t.GetString() != "web_search_tool_result") continue;
            if (!block.TryGetProperty("content", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray())
            {
                var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                if (url.Length == 0 || !seen.Add(url)) continue;
                var title = item.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
                var pageAge = item.TryGetProperty("page_age", out var pa) ? pa.GetString() ?? "" : "";
                results.Add(new SearchHit(url, title, snippets.GetValueOrDefault(url, ""), pageAge));
                if (results.Count >= MaxResults) return results;
            }
        }
        return results;
    }

    private sealed record SearchHit(string Url, string Title, string Snippet, string PublishedAt);
}
