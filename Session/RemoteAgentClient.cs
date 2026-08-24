using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WTangent.Server.Store;

namespace WTangent.Server.Session;

/// <summary>远程 Agent 客户端：HTTP/SSE 连 serve（AgentServer）。事件流反序列化 → Events 回调。
/// 危险命令确认沿用 ConfirmProvider（客户端进程的弹窗/委托），再 POST /confirm 回执。</summary>
public sealed class RemoteAgentClient(Uri baseUrl) : IAgentClient
{
    private static readonly HttpClient Http = WTangent.Core.Http.Client;
    private string? _sessionId;

    public IAgentEvents? Events { get; set; }

    public async Task<string?> AskAsync(string prompt, CancellationToken ct = default)
    {
        _sessionId ??= await CreateSessionAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, $"/session/{_sessionId}/ask"));
        req.Content = new StringContent(JsonSerializer.Serialize(new SsePayload { Text = prompt }), Encoding.UTF8, "application/json");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var parser = new SseParser();
        string? final = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!parser.Feed(line, out var ev)) continue;
            var payload = ev.Data is null ? new SsePayload() : JsonSerializer.Deserialize<SsePayload>(ev.Data, AgentProtocol.Json) ?? new SsePayload();
            switch (ev.Type)
            {
                case SseEventType.MessageDelta:
                    Events?.OnMessageDelta(payload.Text ?? "");
                    break;
                case SseEventType.ReasoningDelta:
                    Events?.OnReasoningDelta(payload.Text ?? "");
                    break;
                case SseEventType.ToolStart:
                    Events?.OnToolStart(payload.Name ?? "", payload.Arguments ?? "");
                    break;
                case SseEventType.ToolEnd:
                    Events?.OnToolEnd(payload.Name ?? "", payload.Result ?? "");
                    break;
                case SseEventType.ConfirmReq:
                    await HandleConfirmAsync(payload);
                    break;
                case SseEventType.TurnEnd:
                    final = payload.FinalText;
                    break;
                case SseEventType.Done:
                    return final;
                default:
                    break;
            }
        }
        return final;
    }

    public Task ResetAsync(CancellationToken ct = default) => Task.CompletedTask;

    private async Task HandleConfirmAsync(SsePayload payload)
    {
        var allow = ConfirmProvider.Ask(payload.Prompt ?? "");
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, "/confirm"));
        req.Content = new StringContent(JsonSerializer.Serialize(new ConfirmReply(payload.Id ?? "", allow), AgentProtocol.Json), Encoding.UTF8, "application/json");
        await Http.SendAsync(req);
    }

    private async Task<string> CreateSessionAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, "/session"));
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("session_id").GetString() ?? throw new InvalidOperationException("no session_id");
    }
}
