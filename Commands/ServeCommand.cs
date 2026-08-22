using System.CommandLine;
using WTangent.Server.Session;
using WTangent.Server.Store;
using WTangent.Server.Tools;

namespace WTangent.Server.Commands;


/// <summary>serve 命令：agent serve [&lt;host&gt;] [&lt;port&gt;] [--projects ...] [--mock ...]</summary>
[AgentCommand]
public sealed class ServeCommand : Command
{
    public ServeCommand() : base("serve", "启动服务：会话 API（WS/SSE）+ git 项目仓库 + Web UI（--help 看参数）")
    {
        var host = new Argument<string?>("host")
        {
            Description = "监听地址（默认 127.0.0.1；局域网用 0.0.0.0，需 urlacl：netsh http add urlacl url=http://+:8890/ user=Everyone）",
            Arity = ArgumentArity.ZeroOrOne
        };
        var port = new Argument<int>("port")
        {
            Description = "监听端口（默认 8890）",
            Arity = ArgumentArity.ZeroOrOne
        };
        var projects = new Option<string?>("--projects") { Description = @"git 项目仓库目录（默认 %APPDATA%\agent\projects）" };
        var web = new Option<string?>("--web") { Description = @"Web UI 静态目录（缺省 %APPDATA%\agent\web 或自动查找）" };
        var noWeb = new Option<bool>("--no-web") { Description = "不托管 Web UI" };
        var baseUrl = new Option<string?>("--base-url") { Description = "OpenAI 兼容 base URL（缺省用缓存）" };
        var key = new Option<string?>("--key") { Description = "API Key（缺省用缓存）" };
        var model = new Option<string?>("--model") { Description = "模型名（缺省用缓存）" };
        var mock = new Option<bool>("--mock") { Description = "用假 LLM 起服务（联调用，不烧 API）" };
        Add(host);
        Add(port);
        Add(projects);
        Add(web);
        Add(noWeb);
        Add(baseUrl);
        Add(key);
        Add(model);
        Add(mock);

        SetAction(pr => OperatingSystem.IsWindows() && pr.UnmatchedTokens.Contains("--service")
            ? AgentServiceHost.Run(["serve", .. pr.UnmatchedTokens])
            : RunAsync(pr.GetValue(host), pr.GetValue(port), pr.GetValue(projects),
                pr.GetValue(web), pr.GetValue(noWeb), pr.GetValue(baseUrl), pr.GetValue(key),
                pr.GetValue(model), pr.GetValue(mock)).GetAwaiter().GetResult());
    }

    /// <summary>serve 核心逻辑</summary>
    public static async Task<int> RunAsync(string? host = null, int port = 0, string? projects = null,
        string? web = null, bool noWeb = false, string? baseUrl = null, string? key = null,
        string? model = null, bool mock = false)
    {
        var provider = mock
            ? new ProviderConfig { Name = "fake", BaseUrl = "http://fake", ApiKey = "fake", DefaultModel = "fake-model" }
            : ModelConfig.ResolveProvider(baseUrl, key, model);
        if (provider == null) return 1;

        var opts = new AgentOptions
        {
            Provider = provider,
            // 工具显式组装：内置默认 + 组件扩展（tool 组件，Entry.Tools）
            Tools = [.. ServerTools.Default(provider, mock ? false : null), .. ToolComponentLoader.Load()],
            Llm = mock ? new FakeLlmClient(
                scriptedToolCalls: ["glob **/*.cs", "read_file Core/Agent/AgentCore.cs", "bash 1..10 | ForEach-Object { 'line ' + $_ }"],
                finalText: ["## 结论\n\n远程 serve mock 回复。\n"]) : null,
        };
        var webDir = noWeb ? null : web ?? AgentServer.FindWebDir();
        if (webDir is null && !noWeb && web is null)
        {
            // 缺 Web UI → 自动下载（agent install web → %APPDATA%\agent\web），失败仅提示不阻断 serve
            Console.WriteLine("[serve] Web UI 未安装，自动下载…（agent install web）");
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("wtangent", "install web")
                {
                    UseShellExecute = false,
                });
                p?.WaitForExit();
                webDir = AgentServer.FindWebDir();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[serve] 自动下载 web 失败（可手动 agent install web）：{e.Message}");
            }
        }
        var server = new AgentServer(opts, port > 0 ? port : 8890, projects, host ?? "127.0.0.1", webDir);
        if (!noWeb)
            Console.WriteLine(webDir is null
                ? "[serve] Web UI 未找到（先 agent install web 或 --web 指定目录；--no-web 可关）"
                : $"[serve] Web UI: {webDir}");
        await server.StartAsync();
        return 0;
    }
}
