using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace WTangent.Server.Tools.Mcp;

/// <summary>MCP stdio 客户端：起子进程，stdin/stdout 走 newline-delimited JSON-RPC 2.0
/// （MCP stdio 传输约定：一行一条消息）。生命周期 = serve 进程；initialize 握手后即用，
/// 通知消息（notifications/*）当前全部忽略，仅按 id 匹配请求响应。</summary>
public sealed class McpStdioClient(string serverName, string command, string[] args,
    Dictionary<string, string>? env, int timeoutMs) : IDisposable
{
    private const string ProtocolVersion = "2024-11-05";

    private readonly Process _proc = StartProc(serverName, command, args, env);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private long _nextId;

    /// <summary>连接异常（进程启动失败/提前退出）</summary>
    public class McpException(string message) : Exception(message);

    /// <summary>握手 + 就绪。失败抛 McpException（桥层捕获跳过该服务器，不阻断 serve）</summary>
    public static async Task<McpStdioClient> ConnectAsync(string serverName, string command, string[] args,
        Dictionary<string, string>? env, int timeoutMs, CancellationToken ct = default)
    {
        var client = new McpStdioClient(serverName, command, args, env, timeoutMs);
        client.Pump();   // 管道有缓冲，握手前启动也不丢早期输出
        try
        {
            await client.RequestAsync("initialize", new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = "wtangent-serve", version = "0.6" },
            }, ct);
            await client.NotifyAsync("notifications/initialized");
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>tools/list → 工具定义数组（name/description/inputSchema）</summary>
    public async Task<List<JsonElement>> ListToolsAsync(CancellationToken ct = default)
    {
        var result = await RequestAsync("tools/list", ct: ct);
        return result.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array
            ? [.. tools.EnumerateArray().Select(t => t.Clone())]
            : [];
    }

    /// <summary>tools/call → (拼接的文本结果, isError)。argumentsJson 为空视作空对象</summary>
    public async Task<(string Text, bool IsError)> CallToolAsync(string tool, string argumentsJson, CancellationToken ct = default)
    {
        JsonElement arguments;
        try { arguments = string.IsNullOrWhiteSpace(argumentsJson) ? JsonDocument.Parse("{}").RootElement.Clone() : JsonDocument.Parse(argumentsJson).RootElement.Clone(); }
        catch (JsonException e) { throw new McpException($"参数不是合法 JSON：{e.Message}"); }
        var result = await RequestAsync("tools/call", new Dictionary<string, object?> { ["name"] = tool, ["arguments"] = arguments }, ct);
        var isError = result.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True;
        var text = result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? string.Join("\n", content.EnumerateArray()
                .Where(c => c.TryGetProperty("type", out var t) && t.GetString() == "text")
                .Select(c => c.TryGetProperty("text", out var x) ? x.GetString() : null))
            : "";
        return (text, isError);
    }

    /// <summary>JSON-RPC 请求：写一行 → 按 id 等响应（超时/进程退出/错误对象都转异常）</summary>
    private async Task<JsonElement> RequestAsync(string method, object? parameters = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        await using var reg = cts.Token.Register(() =>
            tcs.TrySetException(new McpException($"{method} 超时（{timeoutMs}ms）")));
        var req = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null) req["params"] = parameters;
        await _writeLock.WaitAsync(ct);
        try { await _proc.StandardInput.WriteLineAsync(JsonSerializer.Serialize(req)); }
        finally { _writeLock.Release(); }
        try { return await tcs.Task; }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>通知（无 id 无响应）：initialized 等生命周期信号</summary>
    private async Task NotifyAsync(string method)
    {
        await _writeLock.WaitAsync();
        try { await _proc.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method })); }
        finally { _writeLock.Release(); }
    }

    /// <summary>后台读循环：响应按 id 派发，通知忽略，非 JSON 行（服务器违规打日志到 stdout）跳过</summary>
    private void Pump()
    {
        Task.Run(async () =>
        {
            try
            {
                while (await _proc.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (line.Length == 0) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        if (!doc.RootElement.TryGetProperty("id", out var idEl))
                            continue;   // 通知：当前全部忽略
                        var result = doc.RootElement.TryGetProperty("result", out var r) ? r.Clone() : default;
                        if (doc.RootElement.TryGetProperty("error", out var err))
                        {
                            var msg = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                            Complete(idEl, e => e.TrySetException(new McpException($"MCP 错误：{msg ?? line[..Math.Min(line.Length, 200)]}")));
                        }
                        else
                        {
                            Complete(idEl, e => e.TrySetResult(result));
                        }
                    }
                    catch (JsonException)
                    {
                        Console.Error.WriteLine($"[mcp:{serverName}] 非 JSON 输出（忽略）：{line[..Math.Min(line.Length, 200)]}");
                    }
                }
            }
            catch { /* 进程退出/管道断开：下面的 pending 清算兜底 */ }
            foreach (var kv in _pending)
                kv.Value.TrySetException(new McpException($"MCP {serverName} 进程已退出"));
            _pending.Clear();
        });
    }

    private void Complete(JsonElement idEl, Action<TaskCompletionSource<JsonElement>> apply)
    {
        if (!idEl.TryGetInt64(out var id)) return;
        if (_pending.TryRemove(id, out var tcs)) apply(tcs);
    }

    /// <summary>起子进程：Windows 下无扩展名命令（npx 等）补 .cmd 解析；stderr 直通控制台加前缀</summary>
    private static Process StartProc(string serverName, string command, string[] args, Dictionary<string, string>? env)
    {
        var resolved = command;
        if (OperatingSystem.IsWindows() && !Path.HasExtension(resolved))
        {
            var ext = CmdExtensions.FirstOrDefault(e => System.IO.File.Exists(Path.Combine(
                Path.GetDirectoryName(resolved) ?? ".", resolved + e)) || FindOnPath(resolved + e) is not null);
            if (ext is not null) resolved = FindOnPath(resolved + ext) ?? resolved + ext;
        }
        var psi = new ProcessStartInfo(resolved) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardInput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env) psi.EnvironmentVariables[k] = v;
        var proc = Process.Start(psi) ?? throw new McpException($"进程启动失败：{resolved}");
        proc.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) Console.Error.WriteLine($"[mcp:{serverName}] {e.Data}"); };
        proc.BeginErrorReadLine();
        return proc;
    }

    private static readonly string[] CmdExtensions = [".cmd", ".exe", ".bat"];

    private static string? FindOnPath(string file)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator).Select(dir => Path.Combine(dir.Trim(), file))
            .FirstOrDefault(File.Exists);
    }

    public void Dispose()
    {
        try
        {
            if (!_proc.HasExited) _proc.Kill(entireProcessTree: true);
        }
        catch { }
        _proc.Dispose();
        _writeLock.Dispose();
    }
}
