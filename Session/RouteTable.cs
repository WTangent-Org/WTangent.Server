// 原 AgentServer.Handle 的巨型 switch(131 行)→ 表驱动路由。
// 路由顺序敏感:先注册精确匹配,再前缀匹配,最后兜底(static/git)。
// mode: Exact = 全等;Prefix = StartsWith;Suffix = EndsWith 组合。

using System.Net;
using System.Text.Json;
using WTangent.Core;

namespace WTangent.Server.Session;

/// <summary>HTTP 路由表(GET/POST → handler)。handler 内自行 WriteJson 并关闭响应。</summary>
internal sealed class RouteTable
{
    private readonly List<(string Method, Func<string, bool> Match, Func<HttpListenerContext, string, CancellationToken, Task> Handler)> _routes = [];

    private static bool Exact(string path, string expected) => path == expected;
    private static Func<string, bool> Prefix(string p) => path => path.StartsWith(p, StringComparison.Ordinal);

    public void Add(string method, string path, Func<HttpListenerContext, string, CancellationToken, Task> handler)
        => _routes.Add((method, p => Exact(p, path), handler));

    public void AddPrefix(string method, string prefix, Func<HttpListenerContext, string, CancellationToken, Task> handler)
        => _routes.Add((method, Prefix(prefix), handler));

    /// <summary>组合匹配(如 POST /projects/{id}/push:前缀 + 后缀)。</summary>
    public void AddPrefixed(string method, string prefix, string suffix, Func<HttpListenerContext, string, CancellationToken, Task> handler)
        => _routes.Add((method, path => path.StartsWith(prefix, StringComparison.Ordinal) && path.EndsWith(suffix, StringComparison.Ordinal), handler));

    /// <summary>兜底路由(git smart HTTP / 静态托管)。</summary>
    public void AddFallback(string method, Func<HttpListenerContext, string, CancellationToken, Task> handler)
        => _routes.Add((method, _ => true, handler));

    /// <summary>按注册顺序找第一条命中路由;未命中返回 false。</summary>
    public bool TryRoute(string method, string path, HttpListenerContext ctx, CancellationToken ct)
    {
        foreach (var (m, match, handler) in _routes)
        {
            if (m != method || !match(path)) continue;
            handler(ctx, path, ct);
            return true;
        }
        return false;
    }
}
