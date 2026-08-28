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
        // SCM 启动标记（register 注入 binPath）：走 Windows 服务实现，不在终端启动
        var service = new Option<bool>("--service") { Description = "Windows 服务模式（SCM 启动注入，终端勿用）", Hidden = true };
        Add(host);
        Add(port);
        Add(projects);
        Add(web);
        Add(noWeb);
        Add(baseUrl);
        Add(key);
        Add(model);
        Add(mock);
        Add(service);

        // --service 分流唯一入口：SCM 标记 → 同一套参数组装出 AgentServer 交 ServiceBase 生命周期；否则终端自启
        SetAction(pr => OperatingSystem.IsWindows() && pr.GetValue(service)
            ? Task.FromResult(RunService(pr.GetValue(host), pr.GetValue(port), pr.GetValue(projects),
                pr.GetValue(web), pr.GetValue(noWeb), pr.GetValue(baseUrl), pr.GetValue(key),
                pr.GetValue(model), pr.GetValue(mock)))
            : RunAsync(pr.GetValue(host), pr.GetValue(port), pr.GetValue(projects),
                pr.GetValue(web), pr.GetValue(noWeb), pr.GetValue(baseUrl), pr.GetValue(key),
                pr.GetValue(model), pr.GetValue(mock)));
    }

    /// <summary>Windows 服务路径：同一套参数 → AgentServer（不启动），交 SCM 生命周期</summary>
    private static int RunService(string? host, int port, string? projects, string? web, bool noWeb,
        string? baseUrl, string? key, string? model, bool mock)
    {
        if (!OperatingSystem.IsWindows()) return 1;   // 分流处已判平台，此处兜底（ServiceBase 仅 Windows）
        AgentServer server;
        try { server = BuildServer(host, port, projects, web, noWeb, baseUrl, key, model, mock); }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        return AgentServiceHost.Run(server);
    }

    /// <summary>serve 参数 → AgentServer（终端与 Windows 服务两条路径共用的唯一组装点；不启动）</summary>
    internal static AgentServer BuildServer(string? host, int port, string? projects, string? web, bool noWeb,
        string? baseUrl, string? key, string? model, bool mock)
    {
        var provider = ResolveServeProvider(baseUrl, key, model, mock);
        var opts = new AgentOptions
        {
            Provider = provider,
            // 工具显式组装：内置默认 + 组件扩展（tool 组件，Entry.Tools）
            Tools = [.. ServerTools.All(provider, mock ? false : null)],
            Llm = mock ? new FakeLlmClient(
                scriptedToolCalls: ["glob **/*.cs", "read_file Core/Agent/AgentCore.cs", "bash 1..10 | ForEach-Object { 'line ' + $_ }"],
                finalText: ["## 结论\n\nserve mock 回复。\n"]) : null,
        };
        return new AgentServer(opts, port > 0 ? port : 8890, projects, host ?? "127.0.0.1", noWeb ? null : web ?? AgentServer.FindWebDir());
    }

    /// <summary>serve 参数 → Provider（mock 造 fake；否则缓存/参数解析，失败抛 InvalidOperationException）</summary>
    internal static ProviderConfig ResolveServeProvider(string? baseUrl, string? key, string? model, bool mock) =>
        mock
            ? new ProviderConfig { Name = "fake", BaseUrl = "http://fake", ApiKey = "fake", DefaultModel = "fake-model" }
            : ModelConfig.ResolveProvider(baseUrl, key, model)
              ?? throw new InvalidOperationException("无模型配置（用 --base-url/--key/--model 或先配置缓存）");

    /// <summary>serve 核心逻辑</summary>
    public static async Task<int> RunAsync(string? host = null, int port = 0, string? projects = null,
        string? web = null, bool noWeb = false, string? baseUrl = null, string? key = null,
        string? model = null, bool mock = false)
    {
        ProviderConfig provider;
        try { provider = ResolveServeProvider(baseUrl, key, model, mock); }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        var opts = new AgentOptions
        {
            Provider = provider,
            // 工具显式组装：内置默认 + 组件扩展（tool 组件，Entry.Tools）
            Tools = [.. ServerTools.All(provider, mock ? false : null)],
            Llm = mock ? new FakeLlmClient(
                scriptedToolCalls: ["glob **/*.cs", "read_file Core/Agent/AgentCore.cs", "bash 1..10 | ForEach-Object { 'line ' + $_ }"],
                finalText: ["## 结论\n\nserve mock 回复。\n"]) : null,
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
                if (p is not null) await p.WaitForExitAsync();
                webDir = AgentServer.FindWebDir();
            }
            catch (Exception e)
            {
                await Console.Error.WriteLineAsync($"[serve] 自动下载 web 失败（可手动 agent install web）：{e.Message}");
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
