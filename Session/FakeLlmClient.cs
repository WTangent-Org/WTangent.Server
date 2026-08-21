using System.Runtime.CompilerServices;

namespace WTangent.Server.Session;

/// <summary>假 LLM 客户端：按脚本多轮产出（thinking → 工具调用 → 正文），驱动真实 AgentCore 工具循环，不调用真实 API（测试/serve mock 用）。</summary>
public sealed class FakeLlmClient(string[] scriptedToolCalls, string[] finalText) : ILlmClient
{
    private int _round;

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
        IEnumerable<ChatMessage> messages, string? model = null, IEnumerable<object>? tools = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // 每轮：先产出 thinking，再按脚本产出工具调用或正文
        yield return new LlmStreamChunk { ReasoningDelta = $"第 {_round + 1} 轮思考：模拟思维链。\n" };
        await Task.Delay(300, ct);
        if (_round < scriptedToolCalls.Length)
        {
            var (name, args) = SplitTool(scriptedToolCalls[_round]);
            yield return new LlmStreamChunk { ToolCall = new ToolCall { Id = $"call_{_round}", Name = name, Arguments = args } };
        }
        else if (_round < scriptedToolCalls.Length + finalText.Length)
        {
            var text = finalText[_round - scriptedToolCalls.Length];
            // 模拟流式：按行产出（保持 Markdown 结构完整，避免标题/代码块被打断）
            foreach (var line in text.Split('\n'))
            {
                yield return new LlmStreamChunk { Text = line + "\n" };
                await Task.Delay(30, ct);
            }
        }
        _round++;
    }

    private static (string Name, string Args) SplitTool(string script)
    {
        var idx = script.IndexOf(' ');
        if (idx < 0) return (script, "{}");
        var name = script[..idx];
        var rest = script[(idx + 1)..];
        return name switch
        {
            "bash" => (name, $"{{\"command\":\"{rest}\"}}"),
            "glob" => (name, $"{{\"pattern\":\"{rest}\"}}"),
            _ => (name, rest.Contains('{') ? rest : $"{{\"path\":\"{rest}\"}}"),
        };
    }

    public Task<LlmResponse> ChatAsync(IEnumerable<ChatMessage> messages, string? model = null, IEnumerable<object>? tools = null, CancellationToken ct = default) =>
        throw new NotSupportedException("Fake 仅支持流式");

    private static readonly string[] FakeModels = ["fake-model"];

    public Task<string[]> ListModelsAsync(CancellationToken ct = default) =>
        Task.FromResult(FakeModels);
}
