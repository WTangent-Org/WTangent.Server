using System.Text.Json;
using WTangent.Core;

namespace WTangent.Server.Tools;

/// <summary>askuser 工具：向用户发起结构化提问（选项卡 + 说明），阻塞等回答并把选择回喂给 LLM。
/// 对齐 kimi AskUser / ZCode AskUserQuestion 规范。无 UI 通道（无客户端连接）时向 LLM 报告并自行决策。</summary>
public sealed class AskUserTool : ITool
{
    public string Name => "askuser";

    public object Definition => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = @"向用户发起结构化提问并获得选择。适用于：需求歧义、方案取舍、破坏性操作前的偏好确认等「用户的决定」。

Tips:
- 只在真正需要用户拍板时使用；能自行查证的问题不要问。
- question 必须具体、可执行、以问号结尾。
- options 一般 2-4 个；推荐项的 label 末尾加 '(Recommended)'；用户总能自由输入其他答案。
- header 是 12 字符内的分类短标签（如 '登录方式'、'数据库'）。
- 无客户端连接时本工具会报告不可用，你应自行决策并继续。".Replace("\r\n", "\n"),
            parameters = new
            {
                type = "object",
                properties = new
                {
                    question = new { type = "string", description = "完整的问题（具体、可执行、以 ? 结尾）" },
                    header = new { type = "string", description = "分类短标签（≤12 字符，如 '登录方式'）" },
                    options = new
                    {
                        type = "array",
                        description = "2-4 个选项",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                label = new { type = "string", description = "简短展示文本（1-5 词；推荐项加 '(Recommended)'）" },
                                description = new { type = "string", description = "该选项的取舍/影响说明" },
                            },
                            required = new[] { "label" },
                        },
                    },
                    multiSelect = new { type = "boolean", description = "允许多选（缺省 false）" },
                },
                required = new[] { "question", "options" },
            },
        },
    };

    public Task<string> RunAsync(string arguments, CancellationToken ct = default)
    {
        string? question, header, optionsJson;
        bool multiSelect;
        List<QuestionOption>? options;
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            var root = doc.RootElement;
            question = root.TryGetProperty("question", out var q) ? q.GetString() : null;
            header = root.TryGetProperty("header", out var h) ? h.GetString() ?? "" : "";
            multiSelect = root.TryGetProperty("multiSelect", out var ms) && ms.ValueKind == JsonValueKind.True;
            options = root.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array
                ? [.. opts.EnumerateArray()
                    .Select(o => new QuestionOption(
                        o.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
                        o.TryGetProperty("description", out var d) ? d.GetString() ?? "" : ""))
                    .Where(o => o.Label.Length > 0)]
                : null;
            optionsJson = options is null ? null : JsonSerializer.Serialize(options, AgentProtocol.Json);
        }
        catch (JsonException e)
        {
            return Task.FromResult($"[askuser] 参数不是合法 JSON：{e.Message}");
        }
        if (string.IsNullOrWhiteSpace(question) || options is not { Count: > 0 })
            return Task.FromResult("[askuser] 缺少 question 或 options 参数");

        var answer = QuestionProvider.Ask(new QuestionSpec(question, header, options, multiSelect));
        return Task.FromResult(answer is null
            ? "[askuser] 当前无可用交互通道（无客户端连接），请自行决策并继续"
            : $"[askuser] 用户选择：{answer}");
    }
}
