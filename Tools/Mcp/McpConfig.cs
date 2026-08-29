using System.Text.Json;

namespace WTangent.Server.Tools.Mcp;

/// <summary>MCP 服务器配置（%APPDATA%\agent\mcp.json）：stdio 服务器 = 启动命令 + 参数 + 环境变量。
/// 后续插件层（plugin.json 的 mcp.json）与此同构，桥只认这一份合并后的视图。</summary>
public static class McpConfig
{
    /// <summary>单个 MCP 服务器条目</summary>
    public sealed record ServerEntry(string Command, string[] Args, Dictionary<string, string>? Env, int TimeoutMs);

    private static readonly string File = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "agent", "mcp.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>读配置；文件缺失/损坏 → 空表（serve 不因 MCP 配置问题起不来）</summary>
    public static Dictionary<string, ServerEntry> LoadServers()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return [];
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(File));
            var result = new Dictionary<string, ServerEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in doc.RootElement.GetProperty("servers").EnumerateObject())
            {
                var command = s.Value.TryGetProperty("command", out var c) ? c.GetString() : null;
                if (string.IsNullOrWhiteSpace(command)) continue;
                string[] args = s.Value.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
                    ? [.. a.EnumerateArray().Select(v => v.GetString() ?? "")]
                    : [];
                var env = s.Value.TryGetProperty("env", out var e) && e.ValueKind == JsonValueKind.Object
                    ? e.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
                        .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.OrdinalIgnoreCase)
                    : null;
                var timeout = s.Value.TryGetProperty("timeoutMs", out var t) && t.TryGetInt32(out var ms) ? ms : 120_000;
                result[s.Name] = new ServerEntry(command, args, env, timeout);
            }
            return result;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[serve] mcp.json 读取失败（忽略）：{e.Message}");
            return [];
        }
    }
}
