using WTangent.Server.Tools.Mcp;

namespace WTangent.Server.Tools;

/// <summary>LLM 工具组装入口（serve/服务两条路径共用）：All = 内置默认 + 组件扩展。
/// 组件扩展工具由空壳启动时聚合注册进 App.Services（单实例单次启动），这里只取不扫。
/// websearch 按 Provider 判断（DeepSeek 官方端点支持原生搜索才暴露）。</summary>
public static class ServerTools
{
    /// <summary>全部工具 = 内置默认 + 组件扩展（Entry.Tools，经 App.Services 聚合）</summary>
    public static List<ITool> All(ProviderConfig provider, bool? enableWebSearch = null)
    {
        var tools = Default(provider, enableWebSearch);
        if (Entry.App.Services.Resolve<IReadOnlyList<ITool>>() is { Count: > 0 } extra)
            tools.AddRange(extra);
        tools.AddRange(McpBridge.Load());   // MCP 服务器工具（mcp.json；单服务器失败跳过）
        return tools;
    }

    /// <summary>内置工具（provider 决定 websearch 是否暴露；enableWebSearch 显式覆盖）</summary>
    public static List<ITool> Default(ProviderConfig provider, bool? enableWebSearch = null)
    {
        var tools = new List<ITool>
        {
            new BashTool(), new BashOutputTool(), new KillShellTool(), new ReadFileTool(), new GlobTool(),
            new GrepTool(), new EditFileTool(), new WriteTool(), new GitCommitTool(), new WebFetchTool(),
            new RefSearchTool(),
        };
        var webSearch = enableWebSearch
            ?? provider.BaseUrl.Contains("api.deepseek.com", StringComparison.OrdinalIgnoreCase);
        if (webSearch) tools.Add(new WebSearchTool());
        return tools;
    }
}
