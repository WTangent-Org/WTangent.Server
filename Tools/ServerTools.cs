using WTangent.Server.Session;

namespace WTangent.Server.Tools;

/// <summary>内置工具工厂：bash/background/文件/git/web/ref 等默认工具。
/// web_search 按 Provider 判断（DeepSeek 官方端点支持原生搜索才暴露）。
/// 组件扩展工具由 ToolComponentLoader 加载后拼接进 AgentOptions.Tools（必须显式组装）。</summary>
public static class ServerTools
{
    /// <summary>内置工具（provider 决定 web_search 是否暴露；enableWebSearch 显式覆盖）</summary>
    public static List<ITool> Default(ProviderConfig provider, bool? enableWebSearch = null)
    {
        var tools = new List<ITool>
        {
            new BashTool(), new BackgroundTool(), new ReadFileTool(), new GlobTool(), new GrepTool(),
            new EditFileTool(), new GitCommitTool(), new WebFetchTool(), new RefSearchTool(),
        };
        var webSearch = enableWebSearch
            ?? provider.BaseUrl.Contains("api.deepseek.com", StringComparison.OrdinalIgnoreCase);
        if (webSearch) tools.Add(new WebSearchTool());
        return tools;
    }
}
