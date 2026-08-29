using System.Text.Json;
using System.Text.RegularExpressions;
using WTangent.Core;

namespace WTangent.Server.Tools.Mcp;

/// <summary>MCP 工具适配器：MCP 服务器的 tools/list 条目 → ITool。
/// 工具名加 mcp_{server}_ 前缀（防与内置工具撞名）并清洗为 LLM API 允许的字符集，
/// 超长截断到 64；inputSchema（JSON Schema）原样作为 function parameters 透传。</summary>
public sealed class McpToolAdapter(McpStdioClient client, string serverName, JsonElement tool) : ITool
{
    private static readonly Regex UnsafeChars = new("[^a-zA-Z0-9_-]", RegexOptions.Compiled);

    private readonly string _toolName = tool.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
    private readonly string _description = tool.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";

    /// <summary>清洗后的 LLM 工具名</summary>
    public string Name { get; } = Truncate($"mcp_{Sanitize(serverName)}_{Sanitize(
        tool.TryGetProperty("name", out var tn) ? tn.GetString() ?? "" : "")}");

    public object Definition => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = $"[{serverName}] {_description}",
            parameters = tool.TryGetProperty("inputSchema", out var schema) ? schema.Clone()
                : JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone(),
        },
    };

    public async Task<string> RunAsync(string arguments, CancellationToken ct = default)
    {
        var (text, isError) = await client.CallToolAsync(_toolName, arguments, ct);
        return isError ? $"[mcp:{serverName}] 错误：{text}" : text;
    }

    private static string Sanitize(string name) => UnsafeChars.Replace(name, "_");

    private static string Truncate(string name) => name.Length <= 64 ? name : name[..64];
}
