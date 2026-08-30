// 路由表组装:AgentServer 的 HTTP 面(16 条路由)一张表看全。
// handler 方法体留在 AgentServer(partial),本文件只做接线 + 少量原来内联在 switch 里的轻逻辑。

using WTangent.Core;

namespace WTangent.Server.Session;

internal static partial class Routes
{
    /// <summary>组装路由表。handler 方法留在 AgentServer(partial),此处只接线。</summary>
    public static RouteTable Build(AgentServer s) => BuildAsync(s).GetAwaiter().GetResult();

#pragma warning disable CS1998 // 组装为同步,签名统一留异步
    private static async Task<RouteTable> BuildAsync(AgentServer s)
    {
        var r = new RouteTable();

        // —— 健康与元数据 ——
        r.Add("GET", "/health", (ctx, _, _) =>
        {
            lock (s.Gate)
                AgentServer.WriteJson(ctx, 200, new { ok = true, sessions = s.SessionCount });
            return Task.CompletedTask;
        });
        r.Add("GET", "/projects", (ctx, _, _) => { AgentServer.WriteJson(ctx, 200, new { projects = s.ListProjects() }); return Task.CompletedTask; });
        r.Add("GET", "/sessions", (ctx, _, _) => { s.HandleSessions(ctx); return Task.CompletedTask; });
        r.Add("GET", "/remotes", (ctx, _, _) =>
        {
            AgentServer.WriteJson(ctx, 200, new { remotes = new ServerRegistry(store: Entry.App.Store).List().Select(x => new { name = x.Name, url = x.Url }) });
            return Task.CompletedTask;
        });
        r.Add("GET", "/config", (ctx, _, _) => { AgentServer.WriteJson(ctx, 200, new { auto_optimize = AgentConfigStore.Load().AutoOptimize }); return Task.CompletedTask; });
        r.Add("POST", "/config", (ctx, _, _) => s.HandleConfig(ctx));

        // —— 会话 ——
        r.Add("POST", "/session", (ctx, _, _) => { s.HandleSessionCreate(ctx); return Task.CompletedTask; });
        r.AddPrefix("GET", "/ws/", (ctx, path, ct) => s.HandleWs(ctx, path["/ws/".Length..], ct));
        r.AddPrefixed("GET", "/session/", "/messages", (ctx, path, _) =>
        {
            var id = path["/session/".Length..^"/messages".Length];
            AgentServer.WriteJson(ctx, 200, new { messages = s.Store.LoadMessages(id).Select(m => new { role = m.Role.ToString().ToLowerInvariant(), content = m.Content }) });
            return Task.CompletedTask;
        });
        r.AddPrefix("POST", "/session/", (ctx, path, ct) =>
        {
            var rest = path["/session/".Length..];
            if (!rest.EndsWith("/ask", StringComparison.Ordinal))
            {
                AgentServer.WriteJson(ctx, 400, new { error = "unknown endpoint" });
                return Task.CompletedTask;
            }
            return s.HandleAsk(ctx, rest[..^"/ask".Length], ct);
        });

        // —— 交互回执 ——
        r.Add("POST", "/confirm", (ctx, _, _) => s.HandleConfirm(ctx));
        r.Add("POST", "/question", (ctx, _, _) => s.HandleQuestion(ctx));

        // —— git 项目 ——
        r.AddPrefixed("POST", "/projects/", "/push", (ctx, path, _) => { s.HandleProjectPush(ctx, path["/projects/".Length..^"/push".Length]); return Task.CompletedTask; });
        r.AddPrefixed("POST", "/projects/", "/branch", (ctx, path, _) => { s.HandleBranch(ctx, path["/projects/".Length..^"/branch".Length]); return Task.CompletedTask; });
        r.AddPrefix("POST", "/optimize/", (ctx, path, _) => s.HandleOptimize(ctx, path["/optimize/".Length..]));
        r.Add("POST", "/git-exec", (ctx, _, _) => s.HandleGitExec(ctx));

        // —— 兜底:git smart HTTP,然后静态托管 ——
        r.AddFallback("ANY", (ctx, path, ct) =>
            path.StartsWith("/git/", StringComparison.Ordinal)
                ? s.HandleGit(ctx, ct)
                : ctx.Request.HttpMethod == "GET" ? s.HandleStatic(ctx, path) : Task.CompletedTask);

        return r;
    }
}
