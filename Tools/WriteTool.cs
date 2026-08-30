using System.Text.Json;

namespace WTangent.Server.Tools;

/// <summary>write 工具：整文件写入（新建/覆盖）。对齐 Claude/pi/kimi 的 write。
/// 已有文件的覆盖建议先 read 再写（避免盲目覆盖他人改动）。</summary>
public sealed class WriteTool : ITool
{
    public string Name => "write";

    public object Definition => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = @"将内容写入文件（新建或整体覆盖），自动创建父目录。

Tips:
- 覆盖已存在的文件前建议先 read 确认内容；局部修改用 edit。
- content 为完整目标内容（不是追加）；追加可用 bash 的 Add-Content。".Replace("\r\n", "\n"),
            parameters = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "文件路径（相对或绝对）" },
                    content = new { type = "string", description = "完整文件内容" },
                },
                required = new[] { "path", "content" },
            },
        },
    };

    public Task<string> RunAsync(string arguments, CancellationToken ct = default)
    {
        var path = ToolArgs.GetString(arguments, "path");
        var content = ToolArgs.GetString(arguments, "content");
        if (string.IsNullOrWhiteSpace(path) || content is null)
            return Task.FromResult("[write] 缺少 path/content 参数");
        try
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            return Task.FromResult($"[write] 已写入 {full}（{content.Length} 字符）");
        }
        catch (Exception e)
        {
            return Task.FromResult($"[write] 失败: {e.Message}");
        }
    }
}
