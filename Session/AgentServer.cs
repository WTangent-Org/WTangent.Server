using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using WTangent.Server.Store;

namespace WTangent.Server.Session;

/// <summary>serve 会话服务：HTTP + SSE + WebSocket 暴露 Agent 会话 + git 项目仓库（bare，smart HTTP）+ Web UI 静态托管。
/// 端点：POST /session 创建会话、POST /session/{id}/ask 流式问答（SSE）、GET /ws/{sessionId}（WebSocket 流式：ask/cancel/confirm 双向）；
/// POST /confirm 危险命令回执、GET /health、GET /remotes（本机服务器注册表，WUI 下拉用）；
/// GET /projects（列项目）、POST /projects/{project}/branch?name=（切换用户分支）、POST /projects/{project}/push?name=（checkout 上 pull→提交→push）；
/// POST /backup/{project}（手动 backup 推送）；/git/{project}.git/* 走 git http-backend（首次 push 自动建 bare，HEAD=main）。
/// host=0.0.0.0 映射 HttpListener 通配 "+"（局域网绑定需 urlacl）；webRoot 非空时 GET 回退托管静态文件（Vue WUI）。</summary>
public sealed class AgentServer(AgentOptions opts, int port, string? projectsDir = null, string host = "127.0.0.1", string? webRoot = null)
{
    private readonly string _projectsDir = projectsDir ?? Path.Combine(AgentPaths.DataDir, "projects");
    private readonly string? _webRoot = webRoot;
    private readonly SessionStore _store = new();
    private readonly Dictionary<string, AgentCore> _sessions = [];
    private readonly ConcurrentDictionary<string, bool> _activeTurns = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _turnCts = new();
    private readonly ConcurrentDictionary<string, SseWriter> _activeWriters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingConfirms = new();
    private readonly ConcurrentDictionary<string, bool> _optimizing = new();   // 正在自动优化的项目（防重入）
    private Task? _pendingTurn;   // WS 会话当前进行中的轮次（HandleWs finally 等待后 Dispose bridge，防竞态）
    private static readonly AsyncLocal<string?> CurrentSession = new();
    private readonly Lock _sessionLock = new();

    public async Task StartAsync(CancellationToken ct = default)
    {
        // 危险命令确认：推 confirm_req 到当前会话的 SSE 流，阻塞等 POST /confirm 回执
        ConfirmProvider.Confirm = prompt =>
        {
            var sessionId = CurrentSession.Value;
            if (sessionId == null || !_activeWriters.TryGetValue(sessionId, out var writer)) return false;
            var id = Guid.NewGuid().ToString("N")[..8];
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingConfirms[id] = tcs;
            writer.Write(AgentProtocol.ConfirmReq(id, prompt));
            try { return tcs.Task.GetAwaiter().GetResult(); }
            finally { _pendingConfirms.TryRemove(id, out _); }
        };
        try
        {
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://{(host == "0.0.0.0" ? "+" : host)}:{port}/");
            listener.Start();
            Console.WriteLine($"[serve] listening on http://127.0.0.1:{port}");
            // 自动注册 local remote（本机回环）+ 写 serve.port（本机 serve 检测：TUI/run 默认连这里）
            new ServerRegistry().Add("local", "127.0.0.1", port);
            await File.WriteAllTextAsync(Path.Combine(AgentPaths.DataDir, "serve.port"), port.ToString(), ct);
            // 已有项目重写 post-receive hook（端口/启用状态变化后同步）
            foreach (var p in ListProjects()) WriteOptimizeHook(p);

            while (!ct.IsCancellationRequested)
            {
                var ctx = await listener.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => Handle(ctx, ct), ct);
            }
        }
        finally
        {
            try { File.Delete(Path.Combine(AgentPaths.DataDir, "serve.port")); } catch { }
            ConfirmProvider.Confirm = ConfirmProvider.DefaultConfirm;
        }
    }

    private async Task Handle(HttpListenerContext ctx, CancellationToken ct)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";   // WUI 跨域（Vite dev 直连）
        ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        try
        {
            if (ctx.Request.HttpMethod == "OPTIONS") { ctx.Response.StatusCode = 204; ctx.Response.Close(); return; }
            switch (ctx.Request.HttpMethod)
            {
                case "GET" when path == "/health":
                {
                    lock (_sessionLock)
                    {
                        WriteJson(ctx, 200, new { ok = true, sessions = _sessions.Count });
                    }

                    return;
                }
                case "GET" when path == "/projects":
                {
                    WriteJson(ctx, 200, new { projects = ListProjects() });
                    return;
                }
                case "GET" when path == "/sessions":
                {
                    WriteJson(ctx, 200, new
                    {
                        sessions = _store.ListSessions().Select(s =>
                        {
                            TokenUsage? usage = null;
                            lock (_sessionLock)
                                if (_sessions.TryGetValue(s.Id, out var a))
                                    usage = a.Usage.Total;
                            return new
                            {
                                id = s.Id,
                                title = s.Title,
                                count = s.Count,
                                input_tokens = usage?.InputTokens ?? 0,
                                output_tokens = usage?.OutputTokens ?? 0,
                                cache_hit_tokens = usage?.CacheReadTokens ?? 0,
                            };
                        }),
                    });
                    return;
                }
                case "GET" when path.StartsWith("/session/", StringComparison.Ordinal)
                    && path.EndsWith("/messages", StringComparison.Ordinal):
                {
                    var id = path["/session/".Length..^"/messages".Length];
                    WriteJson(ctx, 200, new
                    {
                        messages = _store.LoadMessages(id).Select(m => new { role = m.Role.ToString().ToLowerInvariant(), content = m.Content }),
                    });
                    return;
                }
                case "GET" when path == "/remotes":
                {
                    WriteJson(ctx, 200, new
                    {
                        remotes = new ServerRegistry().List().Select(r => new { name = r.Name, url = r.Url }),
                    });
                    return;
                }
                case "GET" when path == "/config":
                {
                    WriteJson(ctx, 200, new { auto_optimize = ConfigStore.Load().AutoOptimize });
                    return;
                }
                case "POST" when path == "/config":
                {
                    await HandleConfig(ctx);
                    return;
                }
                case "POST" when path.StartsWith("/optimize/", StringComparison.Ordinal):
                {
                    await HandleOptimize(ctx, path["/optimize/".Length..]);
                    return;
                }
                case "GET" when path.StartsWith("/ws/", StringComparison.Ordinal):
                {
                    await HandleWs(ctx, path["/ws/".Length..], ct);
                    return;
                }
                case "POST" when path.StartsWith("/projects/", StringComparison.Ordinal)
                    && path.EndsWith("/push", StringComparison.Ordinal):
                {
                    HandleProjectPush(ctx, path["/projects/".Length..^"/push".Length]);
                    return;
                }
                case "POST" when path.StartsWith("/projects/", StringComparison.Ordinal)
                    && path.EndsWith("/branch", StringComparison.Ordinal):
                {
                    HandleBranch(ctx, path["/projects/".Length..^"/branch".Length]);
                    return;
                }
                case "POST" when path == "/session":
                {
                    var id = _store.NewSession("");   // SQLite 持久化（重启后可列可续聊），返回长 id
                    lock (_sessionLock) _sessions[id] = new AgentCore(opts);
                    WriteJson(ctx, 200, new { session_id = id });
                    return;
                }
                case "POST" when path.StartsWith("/session/", StringComparison.Ordinal):
                {
                    var rest = path["/session/".Length..];
                    const string askSuffix = "/ask";
                    if (!rest.EndsWith(askSuffix, StringComparison.Ordinal))
                    {
                        WriteJson(ctx, 400, new { error = "unknown endpoint" });
                        return;
                    }
                    await HandleAsk(ctx, rest[..^askSuffix.Length], ct);
                    return;
                }
                case "POST" when path == "/confirm":
                    await HandleConfirm(ctx);
                    return;
                default:
                    if (path.StartsWith("/git/", StringComparison.Ordinal))
                    {
                        await HandleGit(ctx, ct);
                        return;
                    }
                    if (ctx.Request.HttpMethod == "GET")
                    {
                        await HandleStatic(ctx, path);
                        return;
                    }
                    WriteJson(ctx, 404, new { error = "not found" });
                    break;
            }
        }
        catch (Exception e)
        {
            WriteJson(ctx, 500, new { error = e.Message });
        }
    }

    /// <summary>取会话：内存有则返回；无则从 SQLite 恢复历史建 AgentCore（续聊/断线重连），并注册进内存表。
    /// 返回 null 表示会话不存在（SQLite 也没有）。</summary>
    private AgentCore? GetOrCreateAgent(string sessionId)
    {
        lock (_sessionLock)
        {
            if (_sessions.TryGetValue(sessionId, out var exist)) return exist;
            var history = _store.LoadMessages(sessionId);
            if (history.Count == 0) return null;   // SQLite 无此会话
            var agent = new AgentCore(opts with { InitialHistory = history });
            _sessions[sessionId] = agent;
            return agent;
        }
    }

    /// <summary>轮次结束落库：user 消息 + 该轮新增的 assistant 消息（按内容去重），首条 user 自动生成会话标题（前 20 字）。</summary>
    private void PersistTurn(string sessionId, AgentCore agent, string prompt)
    {
        try
        {
            var userRole = nameof(MessageRole.User).ToLowerInvariant();
            if (!_store.MessageExists(sessionId, userRole, prompt))
            {
                _store.AddMessage(sessionId, userRole, prompt);
                var sessions = _store.ListSessions();
                var info = sessions.FirstOrDefault(s => s.Id == sessionId);
                if (info is { Title.Length: 0 })
                    _store.UpdateTitle(sessionId, prompt.Length > 20 ? prompt[..20] : prompt);
            }
            var assistantRole = nameof(MessageRole.Assistant).ToLowerInvariant();
            foreach (var m in agent.History)
            {
                if (m.Role is not (MessageRole.Assistant or MessageRole.User)) continue;
                if (m.Content.Length == 0) continue;
                var role = m.Role == MessageRole.Assistant ? assistantRole : userRole;
                if (!_store.MessageExists(sessionId, role, m.Content))
                    _store.AddMessage(sessionId, role, m.Content);
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[serve] 会话落库失败 {sessionId}: {e.Message}");
        }
    }

    /// <summary>WebSocket 流式对话：/ws/{sessionId}。客户端 ask/cancel/confirm 双向；服务端事件桥接写回 socket。
    /// 单会话同时只允许一个进行中轮次；cancel 只取消本轮（不关 socket）；confirm 与 POST /confirm 共用 _pendingConfirms。</summary>
    private async Task HandleWs(HttpListenerContext ctx, string sessionId, CancellationToken ct)
    {
        if (!ctx.Request.IsWebSocketRequest) { WriteJson(ctx, 400, new { error = "websocket upgrade required" }); return; }
        var agent = GetOrCreateAgent(sessionId);
        if (agent is null) { WriteJson(ctx, 404, new { error = $"session not found: {sessionId}" }); return; }

        var wsCtx = await ctx.AcceptWebSocketAsync(null);
        var ws = wsCtx.WebSocket;
        CurrentSession.Value = sessionId;   // 危险命令确认桥接：ConfirmProvider → _pendingConfirms（SSE/WS 共用）
        var bridge = new WsEventsBridge(ws, ct);   // 非 using：闭包可能引用，finally 里 await 轮次结束后显式 Dispose
        agent.Events = bridge;
        try
        {
            var buffer = new byte[16 * 1024];
            while (ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult res;
                do
                {
                    res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    ms.Write(buffer, 0, res.Count);
                } while (!res.EndOfMessage);
                if (res.MessageType == WebSocketMessageType.Close) break;
                if (res.MessageType != WebSocketMessageType.Text) continue;

                WsEnvelope? env;
                try { env = JsonSerializer.Deserialize<WsEnvelope>(Encoding.UTF8.GetString(ms.ToArray()), AgentProtocol.Json); }
                catch { continue; }
                switch (env?.Type)
                {
                    case "ask" when env.Text is { Length: > 0 } && !_activeTurns.ContainsKey(sessionId):
                    {
                        _activeTurns[sessionId] = true;
                        var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        _turnCts[sessionId] = turnCts;
                        // 轮次在独立 Task 执行；bridge/turnCts 以参数传入（HandleWs finally await 轮次后 Dispose，防竞态）
                        _pendingTurn = RunTurnAsync(bridge, agent, env.Text, sessionId, turnCts);
                        break;
                    }
                    case "cancel":
                        if (_turnCts.TryGetValue(sessionId, out var turn)) await turn.CancelAsync();
                        break;
                    case "confirm" when env.Id is { Length: > 0 }:
                        if (_pendingConfirms.TryRemove(env.Id, out var tcs)) tcs.TrySetResult(env.Allow == true);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            agent.Events = null;
            if (_turnCts.TryRemove(sessionId, out var tc)) await tc.CancelAsync();
            try { if (_pendingTurn is not null) await _pendingTurn; } catch { }   // 等轮次结束再 Dispose bridge
            bridge.Dispose();
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        }    }

    /// <summary>WS 会话的单轮执行（Task.Run 里跑）：LLM 问答 → 落库 → 事件收尾。
    /// bridge/turnCts 以参数传入，生命周期由 HandleWs finally（await 本 Task 后 Dispose）保证。</summary>
    private Task RunTurnAsync(WsEventsBridge bridge, AgentCore agent, string prompt, string sessionId, CancellationTokenSource turnCts)
    {
        return Task.Run(async () =>
        {
            try
            {
                var final = await agent.AskAsync(prompt, turnCts.Token);
                PersistTurn(sessionId, agent, prompt);
                bridge.TurnEnd(final);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { bridge.Error(e.Message); }
            finally
            {
                bridge.Done();
                _activeTurns.TryRemove(sessionId, out _);
                turnCts.Dispose();
            }
        }, turnCts.Token);
    }

    private async Task HandleAsk(HttpListenerContext ctx, string sessionId, CancellationToken ct)
    {
        var agent = GetOrCreateAgent(sessionId);
        if (agent == null)
        {
            WriteJson(ctx, 404, new { error = $"session not found: {sessionId}" });
            return;
        }

        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);
        string? prompt;
        try { prompt = JsonSerializer.Deserialize<SsePayload>(body, AgentProtocol.Json)?.Text; }
        catch { prompt = null; }
        if (string.IsNullOrWhiteSpace(prompt))
        {
            WriteJson(ctx, 400, new { error = "prompt required" });
            return;
        }

        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["Connection"] = "keep-alive";
        using var writer = new SseWriter(new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false)) { AutoFlush = true });
        _activeWriters[sessionId] = writer;
        CurrentSession.Value = sessionId;
        agent.Events = new SseEventsBridge(writer);
        try
        {
            var final = await agent.AskAsync(prompt, ct);
            PersistTurn(sessionId, agent, prompt);
            writer.Write(AgentProtocol.TurnEnd(final));
            writer.Write(AgentProtocol.Done());
        }
        finally
        {
            _activeWriters.TryRemove(sessionId, out _);
            agent.Events = null;
        }
    }

    private async Task HandleConfirm(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        ConfirmReply? reply;
        try { reply = JsonSerializer.Deserialize<ConfirmReply>(body, AgentProtocol.Json); }
        catch { WriteJson(ctx, 400, new { error = "invalid body" }); return; }
        if (reply != null && _pendingConfirms.TryRemove(reply.Id, out var tcs))
        {
            tcs.TrySetResult(reply.Allow);
            WriteJson(ctx, 200, new { ok = true });
        }
        else WriteJson(ctx, 404, new { error = "confirm id not found" });
    }

    /// <summary>git smart HTTP：/git/{project}/{path} 转发给 git http-backend。
    /// **目录即仓库**：首次 push（receive-pack）自动建普通仓库（git init -b main + receive.denyCurrentBranch=updateInstead，
    /// 推 main 即更新服务器工作树）；项目不存在且非 push → 404。</summary>
    private async Task HandleGit(HttpListenerContext ctx, CancellationToken ct)
    {
        Directory.CreateDirectory(_projectsDir);
        var path = ctx.Request.Url!.AbsolutePath;          // /git/{project}/info/refs
        var rest = path["/git/".Length..];
        var sep = rest.IndexOf('/');
        if (sep <= 0) { WriteJson(ctx, 404, new { error = "bad git path" }); return; }
        var project = rest[..sep];
        var pathInfo = "/" + rest;                     // http-backend 需要 /{project}/{path}
        var repoDir = Path.Combine(_projectsDir, project);
        var service = ctx.Request.QueryString["service"] ?? "";
        var isPush = service.Contains("receive-pack", StringComparison.OrdinalIgnoreCase)
            || pathInfo.Contains("/git-receive-pack", StringComparison.OrdinalIgnoreCase);
        if (!Directory.Exists(Path.Combine(repoDir, ".git")))
        {
            if (!isPush) { WriteJson(ctx, 404, new { error = $"project not found: {project}" }); return; }
            var init = GitRun("init", "-b", "main", project);
            if (init.ExitCode != 0) { WriteJson(ctx, 500, new { error = init.Output }); return; }
            // 目录即仓库：push 更新工作树（updateInstead）；http-backend 默认只开 upload-pack，receive-pack 需显式开
            GitRunIn(repoDir, "config", "receive.denyCurrentBranch", "updateInstead");
            GitRunIn(repoDir, "config", "http.receivepack", "true");
            GitRunIn(repoDir, "config", "http.uploadpack", "true");
            WriteOptimizeHook(project);
        }

        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _projectsDir,
        };
        psi.ArgumentList.Add("http-backend");
        psi.EnvironmentVariables["GIT_PROJECT_ROOT"] = _projectsDir;
        psi.EnvironmentVariables["GIT_HTTP_EXPORT_ALL"] = "1";
        psi.EnvironmentVariables["PATH_INFO"] = pathInfo;
        psi.EnvironmentVariables["QUERY_STRING"] = ctx.Request.Url.Query.TrimStart('?');
        psi.EnvironmentVariables["REQUEST_METHOD"] = ctx.Request.HttpMethod;
        psi.EnvironmentVariables["CONTENT_TYPE"] = ctx.Request.ContentType ?? "";
        psi.EnvironmentVariables["CONTENT_LENGTH"] = ctx.Request.ContentLength64.ToString();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git http-backend 启动失败");
        await ctx.Request.InputStream.CopyToAsync(p.StandardInput.BaseStream, ct);
        p.StandardInput.Close();

        // 转发 CGI 响应：头部（到空行）→ 其余字节原样输出
        var stdout = p.StandardOutput.BaseStream;
        List<byte> header = [];
        var found = false;
        while (header.Count < 65_536)
        {
            if (stdout.ReadByte() is not (>= 0 and var b)) break;
            header.Add((byte)b);
            if (header.Count < 4) continue;
            if (header[^4] != 13 || header[^3] != 10 || header[^2] != 13 || header[^1] != 10) continue;
            found = true;
            break;
        }
        var headerText = Encoding.ASCII.GetString([.. header]);
        foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var name = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                ctx.Response.ContentType = value;
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(value, out var len) && found)
                ctx.Response.ContentLength64 = len;
            else if (!name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                ctx.Response.Headers[name] = value;
        }
        if (!found) { WriteJson(ctx, 500, new { error = "http-backend 无响应" }); return; }
        await stdout.CopyToAsync(ctx.Response.OutputStream, ct);
        ctx.Response.OutputStream.Close();
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
            await Console.Error.WriteLineAsync($"[git http-backend {project}] exit {p.ExitCode}: {await p.StandardError.ReadToEndAsync(ct)}");
    }
    /// <summary>项目列表：projects 目录下含 .git 的普通仓库（目录即仓库）</summary>
    private List<string> ListProjects() =>
        !Directory.Exists(_projectsDir)
            ? []
            : [.. Directory.GetDirectories(_projectsDir)
                .Where(d => Directory.Exists(Path.Combine(d, ".git")))
                .Select(d => Path.GetFileName(d))];

    /// <summary>WUI 设置：写 config.json 的 auto_optimize（POST /config，body {"auto_optimize": bool}）</summary>
    private async Task HandleConfig(HttpListenerContext ctx)
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            var req = JsonSerializer.Deserialize<AutoOptimizeRequest>(body, JsonNoEscape);
            if (req is null) { WriteJson(ctx, 400, new { error = "bad body" }); return; }
            var cfg = ConfigStore.Load();
            ConfigStore.Save(cfg with { AutoOptimize = req.AutoOptimize });
            WriteJson(ctx, 200, new { ok = true, auto_optimize = req.AutoOptimize });
        }
        catch (Exception e) { WriteJson(ctx, 400, new { error = e.Message }); }
    }

    private sealed record AutoOptimizeRequest([property: System.Text.Json.Serialization.JsonPropertyName("auto_optimize")] bool AutoOptimize);

    /// <summary>收到 git push 后自动优化（POST /optimize/{project}，由项目 post-receive hook 触发）：
    /// config auto_optimize 开 → 起 AgentCore 会话，审查最新提交、做简单优化、git commit；关 → 直接跳过（省 token）。</summary>
    private async Task HandleOptimize(HttpListenerContext ctx, string project)
    {
        try
        {
            var repo = ProjectRepo(project);
            if (!ConfigStore.Load().AutoOptimize)
            {
                WriteJson(ctx, 200, new { ok = true, skipped = "auto_optimize disabled" });
                return;
            }
            if (!_optimizing.TryAdd(project, true))
            {
                WriteJson(ctx, 200, new { ok = true, skipped = "already optimizing" });
                return;
            }
            try
            {
                WriteJson(ctx, 200, new { ok = true, started = true });
                await OptimizeProjectAsync(repo, project);
            }
            finally { _optimizing.TryRemove(project, out _); }
        }
        catch (Exception e) { WriteJson(ctx, 400, new { ok = false, error = e.Message }); }
    }

    /// <summary>跑一轮优化：审查最新提交 → 改代码 → commit（git_commit 工具，Conventional Commits）。
    /// 用 git -C 和绝对路径操作（BashTool 跑在 serve 进程 cwd）；危险命令会被 ConfirmProvider 拒（保守安全）。</summary>
    private async Task OptimizeProjectAsync(string repo, string project)
    {
        Console.WriteLine($"[optimize] {project}: 开始");
        var prompt =
            $"项目 {project} 刚收到一次 git push（服务器仓库 {repo}）。请审查最新提交并做简单优化：\n" +
            $"1. git -C \"{repo}\" log -1 --stat 查看最新提交改了什么\n" +
            $"2. git -C \"{repo}\" show HEAD 看具体 diff\n" +
            $"3. 找出值得优化的小点（风格/性能/正确性），用工具直接改文件（路径用绝对路径 {repo}/…）\n" +
            $"4. 优化完成后用 git_commit 工具提交：repo=\"{repo}\"，message 用 Conventional Commits 格式（type 按改动定：fix/perf/refactor/imp 等，如 perf: 优化xx查询）\n" +
            "5. 回复总结改了什么、为什么\n" +
            "要求：只做小而安全的优化，不做大规模重构；若无可优化处直接回复\"无需优化\"，不要空提交。";
        var agent = new AgentCore(opts);
        var final = await agent.AskAsync(prompt);
        var summary = final?.Trim();
        Console.WriteLine($"[optimize] {project}: 完成 → {(summary?.Length > 0 ? summary : "(无输出)")}");
    }

    /// <summary>写项目的 post-receive hook：git push 完成后由 git 触发，后台调 serve 的 /optimize/{project}。
    /// 开关由 serve 的 config（auto_optimize）决定，hook 恒写（关闭时 serve 直接跳过，零 token）。
    /// 脚本跨平台（git 自带 sh 执行；Windows 的 git 自带 curl，Linux 盒子需装 curl）。</summary>
    private void WriteOptimizeHook(string project)
    {
        try
        {
            var repo = ProjectRepo(project);
            var hookDir = Path.Combine(repo, ".git", "hooks");
            Directory.CreateDirectory(hookDir);
            var hook = Path.Combine(hookDir, "post-receive");
            var script =
                "#!/bin/sh\n" +
                "# agent auto-optimize hook（serve 写入，勿手改；开关在 serve 的 config auto_optimize）\n" +
                $"curl -s -X POST \"http://127.0.0.1:{port}/optimize/{project}\" >/dev/null 2>&1 &\n";
            File.WriteAllText(hook, script);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(hook,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[serve] 写 post-receive hook 失败 {project}: {e.Message}");
        }
    }

    /// <summary>切换到用户分支：优先已存在，否则从 main 新建（目录即仓库，直接切工作树）</summary>
    private bool SwitchBranch(string repo, string branch) =>
        GitRunIn(repo, "switch", branch).ExitCode == 0
        || GitRunIn(repo, "switch", "-c", branch, "main").ExitCode == 0;

    /// <summary>WUI/API 切换用户分支（多用户协作：每人一个开发分支 + main；直接操作项目目录）</summary>
    private void HandleBranch(HttpListenerContext ctx, string project)
    {
        var user = ctx.Request.QueryString["name"];
        try
        {
            if (user is not { Length: > 0 }) { WriteJson(ctx, 400, new { ok = false, error = "缺少 name 参数" }); return; }
            var repo = ProjectRepo(project);
            if (!SwitchBranch(repo, user)) { WriteJson(ctx, 400, new { ok = false, error = $"无法切换分支 {user}" }); return; }
            WriteJson(ctx, 200, new { ok = true, project, branch = user });
        }
        catch (Exception e) { WriteJson(ctx, 400, new { ok = false, error = e.Message }); }
    }

    /// <summary>提交（目录即仓库，**全自动交给 agent**）：add -A + commit 当前分支所有改动（含用户 push 进来的，下一轮一起交）。
    /// ?msg= 由 agent 提供 Conventional Commits 消息（type 按改动定），缺省 chore({项目}): {时间}。先不管多用户/分支。</summary>
    private void HandleProjectPush(HttpListenerContext ctx, string project)
    {
        try
        {
            var repo = ProjectRepo(project);
            var store = new GitStore(repo);
            var msg = ctx.Request.QueryString["msg"];
            var message = msg is { Length: > 0 } ? msg : store.DefaultCommitMessage(null);
            var ok = store.Commit(message);
            WriteJson(ctx, ok ? 200 : 400, new { ok, project, message });
        }
        catch (Exception e) { WriteJson(ctx, 400, new { ok = false, error = e.Message }); }
    }

    /// <summary>项目仓库目录（目录即仓库）；不存在抛错</summary>
    private string ProjectRepo(string project) =>
        Directory.Exists(Path.Combine(Path.Combine(_projectsDir, project), ".git"))
            ? Path.Combine(_projectsDir, project)
            : throw new InvalidOperationException($"项目 {project} 不存在（先由客户端 git push 创建）");

    /// <summary>静态托管（Vue WUI dist）：GET 回退到 webRoot；SPA 路由回退 index.html。</summary>
    private async Task HandleStatic(HttpListenerContext ctx, string path)
    {
        if (_webRoot is null || !Directory.Exists(_webRoot)) { WriteJson(ctx, 404, new { error = "not found" }); return; }
        var rel = path == "/" ? "index.html" : path.TrimStart('/');
        if (rel.Contains("..", StringComparison.Ordinal)) { WriteJson(ctx, 400, new { error = "bad path" }); return; }
        var file = Path.Combine(_webRoot, rel);
        if (!File.Exists(file)) file = Path.Combine(_webRoot, "index.html");
        if (!File.Exists(file)) { WriteJson(ctx, 404, new { error = "not found" }); return; }
        ctx.Response.ContentType = MimeOf(Path.GetExtension(file));
        ctx.Response.ContentLength64 = new FileInfo(file).Length;
        await using var fs = File.OpenRead(file);
        await fs.CopyToAsync(ctx.Response.OutputStream);
        ctx.Response.OutputStream.Close();
    }

    private static string MimeOf(string ext) => ext switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "application/javascript",
        ".css" => "text/css",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".json" or ".map" => "application/json",
        _ => "application/octet-stream",
    };

    /// <summary>自动查找 Web 根目录（含 index.html）：已安装（%APPDATA%\agent\web，agent install web）→ cwd/web/dist → cwd/web → exe 目录向上。
    /// serve 检测到就托管，没有就跳过（不报错）。</summary>
    public static string? FindWebDir()
    {
        var installed = Path.Combine(AgentPaths.DataDir, "web");
        if (File.Exists(Path.Combine(installed, "index.html"))) return installed;
        var candidates = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory }
            .SelectMany(root => Ancestors(new DirectoryInfo(root), 7)
                .SelectMany(dir => new[] { Path.Combine(dir.FullName, "web", "dist"), Path.Combine(dir.FullName, "web") }));
        return candidates.FirstOrDefault(sub => File.Exists(Path.Combine(sub, "index.html")));
    }

    /// <summary>目录及其向上 N 层祖先（不含 null）</summary>
    private static IEnumerable<DirectoryInfo> Ancestors(DirectoryInfo? dir, int depth)
    {
        for (var i = 0; i < depth && dir is not null; i++, dir = dir.Parent)
            yield return dir;
    }

    private (int ExitCode, string Output) GitRun(params string[] args) => GitRunIn(_projectsDir, args);

    private (int ExitCode, string Output) GitRunIn(string cwd, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = cwd,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("git 启动失败");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            return (p.ExitCode, outTask.GetAwaiter().GetResult() + errTask.GetAwaiter().GetResult());
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return (127, e.Message);
        }
    }

    private static readonly JsonSerializerOptions JsonNoEscape = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static void WriteJson(HttpListenerContext ctx, int status, object obj)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj, JsonNoEscape));
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.OutputStream.Close();
    }
}

/// <summary>SSE 输出器：每次 Write 即 Flush（流式推送）</summary>
public sealed class SseWriter(StreamWriter sw) : IDisposable
{
    public void Write(SseEvent e) => sw.Write(AgentProtocol.Serialize(e));

    public void Dispose() => sw.Dispose();
}

/// <summary>把 AgentCore 事件回调桥接为 SSE 写入</summary>
public sealed class SseEventsBridge(SseWriter writer) : IAgentEvents
{
    public void OnMessageDelta(string delta) => writer.Write(AgentProtocol.MessageDelta(delta));
    public void OnReasoningDelta(string delta) => writer.Write(AgentProtocol.ReasoningDelta(delta));
    public void OnToolStart(string name, string arguments) => writer.Write(AgentProtocol.ToolStart(name, arguments));
    public void OnToolEnd(string name, string result) => writer.Write(AgentProtocol.ToolEnd(name, result));
}

/// <summary>把 AgentCore 事件回调桥接为 WebSocket 写入：Channel 队列 + 单泵线程，保证发送顺序与并发安全
/// （WebSocket.SendAsync 并发不安全）。TurnEnd/Done/Error 也走同一队列，避免与事件乱序。</summary>
public sealed class WsEventsBridge : IAgentEvents, IDisposable
{
    private readonly WebSocket _ws;
    private readonly CancellationToken _ct;
    private readonly Channel<WsEnvelope> _queue = Channel.CreateUnbounded<WsEnvelope>();

    public WsEventsBridge(WebSocket ws, CancellationToken ct)
    {
        _ws = ws;
        _ct = ct;
        _ = PumpAsync();   // 单泵 fire-and-forget：Dispose 完成队列后自然结束
    }

    public void OnMessageDelta(string delta) => Enqueue(new WsEnvelope { Type = "message_delta", Text = delta });
    public void OnReasoningDelta(string delta) => Enqueue(new WsEnvelope { Type = "reasoning_delta", Text = delta });
    public void OnToolStart(string name, string arguments) => Enqueue(new WsEnvelope { Type = "tool_start", Name = name, Arguments = arguments });
    public void OnToolEnd(string name, string result) => Enqueue(new WsEnvelope { Type = "tool_end", Name = name, Result = result });

    /// <summary>一轮完成（final_text），随后 done</summary>
    public void TurnEnd(string? final) => Enqueue(new WsEnvelope { Type = "turn_end", FinalText = final });

    public void Done() => Enqueue(new WsEnvelope { Type = "done" });

    public void Error(string message) => Enqueue(new WsEnvelope { Type = "error", Text = message });

    private void Enqueue(WsEnvelope env) => _queue.Writer.TryWrite(env);

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var env in _queue.Reader.ReadAllAsync(_ct))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(env, AgentProtocol.Json);
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _ct);
            }
        }
        catch (Exception) { /* 发送失败/取消：泵结束，丢弃剩余 */ }
    }

    public void Dispose() => _queue.Writer.TryComplete();
}
