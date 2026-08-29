using System.CommandLine;
using WTangent.Server.Tools.Mcp;

namespace WTangent.Server.Commands;

/// <summary>mcp：查看/手动调用 MCP 服务器工具（配置 %APPDATA%\agent\mcp.json；
/// serve 启动时桥自动把全部工具合并进 LLM 工具表，本命令用于配置验证与调试，不烧 LLM token）。</summary>
[AgentCommand]
public sealed class McpCommand : Command
{
    public McpCommand() : base("mcp", "MCP 服务器工具（list 查看 / call 手动调用；配置 %APPDATA%\\agent\\mcp.json）")
    {
        var list = new Command("list", "列出配置的 MCP 服务器及其工具");
        list.SetAction(async _ =>
        {
            var tools = McpBridge.Load();
            if (tools.Count == 0)
            {
                Console.WriteLine("[mcp] 无可用工具（mcp.json 缺失/为空/全部失败）");
                return 1;
            }
            foreach (var t in tools)
                Console.WriteLine($"  {t.Name}");
            Console.WriteLine($"[mcp] 共 {tools.Count} 个工具");
            return 0;
        });
        Add(list);

        var server = new Argument<string>("server") { Description = "mcp.json servers 里的键名" };
        var tool = new Argument<string>("tool") { Description = "服务器侧原始工具名" };
        var args = new Argument<string?>("arguments")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = """JSON 参数，如 {"path": "."}""",
        };
        var call = new Command("call", "调用 MCP 工具（原始名，不加前缀）") { server, tool, args };
        call.SetAction(async pr =>
        {
            var name = pr.GetValue(server);
            foreach (var s in McpConfig.LoadServers().Where(kv => kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var client = await McpStdioClient.ConnectAsync(s.Key, s.Value.Command, s.Value.Args, s.Value.Env, s.Value.TimeoutMs);
                    using var _ = client;
                    var (text, isError) = await client.CallToolAsync(pr.GetValue(tool) ?? "", pr.GetValue(args) ?? "{}");
                    if (isError) Console.Error.WriteLine($"[mcp] 工具报错：{text}");
                    else Console.WriteLine(text);
                    return isError ? 1 : 0;
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"[mcp] 调用失败：{e.Message}");
                    return 1;
                }
            }
            Console.Error.WriteLine($"[mcp] 未配置服务器 {name}（%APPDATA%\\agent\\mcp.json）");
            return 1;
        });
        Add(call);
    }
}
