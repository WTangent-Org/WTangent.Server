using System.Net;
using System.Text.RegularExpressions;

namespace WTangent.Server.Tools;

/// <summary>网页抓取工具：web_fetch。抓取指定 URL 内容并转文本（dsh 的 web_fetch 闭环：web_search 结果 → 抓全文）。
/// HTML 剥标签：去 script/style、标签换行、实体解码；输出截断保底上下文安全。</summary>
public sealed partial class WebFetchTool : ITool
{
    private const int MaxOutputChars = 20_000;

    public string Name => "webfetch";

    public object Definition => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = @"抓取 HTTP(S) URL 页面并转为文本（状态码 + 内容，超长截断）。

Tips:
- 配合 websearch：对搜索结果的 URL 抓全文。
- 站点可能反爬或超时，失败信息会带原因。",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    url = new { type = "string", description = "要抓取的 HTTP(S) URL" },
                },
                required = new[] { "url" },
            },
        },
    };

    public async Task<string> RunAsync(string arguments, CancellationToken ct = default)
    {
        var url = ToolArgs.GetString(arguments, "url");
        if (string.IsNullOrWhiteSpace(url)) return "[web_fetch] 缺少 url 参数";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return $"[web_fetch] 无效 URL: {url}";
        try
        {
            var http = Http.Client;
            
            http.DefaultRequestHeaders.UserAgent.ParseAdd("agent/0.1");
            using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseContentRead, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var text = StripHtml(body);
            var truncated = text.Length > MaxOutputChars;
            if (truncated) text = text[..MaxOutputChars];
            var header = $"Fetched {url} (HTTP {(int)resp.StatusCode})";
            var footer = truncated ? "\n\n(内容截断。抓取更具体的 URL 或章节获取全文。)" : "";
            return $"{header}\n\n{text}{footer}";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return $"[web_fetch] 请求失败: {e.Message}";
        }
    }

    /// <summary>HTML → 纯文本：去 script/style、标签换块、实体解码、压缩空行</summary>
    private static string StripHtml(string html)
    {
        var s = html;
        // 去掉 script/style/noscript 块
        s = ScriptStyleRegex.Replace(s, "");
        // 块级标签 → 换行
        s = BlockTagRegex().Replace(s, "\n");
        // 去掉剩余标签
        s = AnyTagRegex().Replace(s, "");
        // 实体解码（&amp; &lt; 等）
        s = WebUtility.HtmlDecode(s);
        // 压缩连续空行
        s = BlankLinesRegex().Replace(s, "\n");
        return s.Trim();
    }

    // 反向引用 \1 超出 regex 源生成器能力（SYSLIB1044），保留运行时构造
    private static readonly Regex ScriptStyleRegex = new(@"(?is)<(script|style|noscript)[^>]*>.*?</\1>");

    [GeneratedRegex("(?i)</?(p|div|br|li|tr|h[1-6]|pre|blockquote|table)[^>]*>")]
    private static partial Regex BlockTagRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTagRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLinesRegex();
}
