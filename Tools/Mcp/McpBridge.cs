using WTangent.Core;

namespace WTangent.Server.Tools.Mcp;

/// <summary>MCP 桥：读 mcp.json → 逐服务器连接（initialize 握手 + tools/list）→ 每个工具包成 ITool。
/// 单服务器失败跳过并警告，不阻断 serve；客户端随工具存活，app.shutdown 时统一回收。</summary>
public static class McpBridge
{
    /// <summary>连接全部配置的 MCP 服务器并返回其工具。失败的服务器跳过（日志），不影响其余</summary>
    public static List<ITool> Load()
    {
        var tools = new List<ITool>();
        var clients = new List<McpStdioClient>();
        foreach (var (name, entry) in McpConfig.LoadServers())
        {
            try
            {
                var client = McpStdioClient.ConnectAsync(name, entry.Command, entry.Args, entry.Env, entry.TimeoutMs).GetAwaiter().GetResult();
                var defs = client.ListToolsAsync().GetAwaiter().GetResult();
                foreach (var def in defs)
                    tools.Add(new McpToolAdapter(client, name, def));
                clients.Add(client);
                Console.WriteLine($"[serve] MCP {name} 已连接：{defs.Count} 个工具（{string.Join(", ", defs.Select(d => d.TryGetProperty("name", out var n) ? n.GetString() : "?"))}）");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[serve] MCP {name} 加载失败（跳过）：{e.Message}");
            }
        }
        if (clients.Count > 0)
        {
            // serve 退出时收掉 MCP 子进程（Windows 上父进程退出不会自动杀子进程树）
            Entry.App.Events.Subscribe("app.shutdown", _ =>
            {
                foreach (var c in clients) c.Dispose();
            });
        }
        return tools;
    }
}
