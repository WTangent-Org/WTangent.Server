using System.Text;
using System.Text.RegularExpressions;

namespace WTangent.Server.Tools;

/// <summary>文件读取工具：read_file</summary>
public sealed class ReadFileTool : ITool
{
    public string Name => "read_file";
    public object Definition => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = "读取文件内容。path 为绝对或相对路径。支持 offset/limit 分页读取（行号从 1 起，缺省读全部）。",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "文件路径" },
                    offset = new { type = "integer", description = "起始行号（1 起），可省" },
                    limit = new { type = "integer", description = "读取行数，可省" },
                },
                required = new[] { "path" },
            },
        },
    };

    public Task<string> RunAsync(string arguments, CancellationToken ct = default)
    {
        var path = ToolArgs.GetString(arguments, "path");
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult("[read_file] 缺少 path 参数");
        if (!File.Exists(path)) return Task.FromResult($"[read_file] 文件不存在: {path}");
        try
        {
            var lines = File.ReadAllLines(path);
            var offset = int.TryParse(ToolArgs.GetString(arguments, "offset"), out var o) ? o : 1;
            var limit = int.TryParse(ToolArgs.GetString(arguments, "limit"), out var l) ? l : lines.Length;
            offset = Math.Max(1, offset);
            limit = Math.Clamp(limit, 1, Math.Max(0, lines.Length - offset + 1));
            var slice = lines.Skip(offset - 1).Take(limit).Select((t, i) => $"{offset + i}: {t}");
            return Task.FromResult(string.Join("\n", slice));
        }
        catch (Exception e)
        {
            return Task.FromResult($"[read_file] 读取失败: {e.Message}");
        }
    }
}

/// <summary>文件查找工具：glob</summary>
public sealed class GlobTool : ITool
{
    public string Name => "glob";
    public object Definition => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = "按 glob 模式查找文件，如 **/*.cs。path 为起始目录（缺省当前目录）。返回相对路径列表。",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    pattern = new { type = "string", description = "glob 模式，如 **/*.cs" },
                    path = new { type = "string", description = "起始目录，可省" },
                },
                required = new[] { "pattern" },
            },
        },
    };

    public Task<string> RunAsync(string arguments, CancellationToken ct = default)
    {
        var pattern = ToolArgs.GetString(arguments, "pattern");
        var path = ToolArgs.GetString(arguments, "path");
        if (string.IsNullOrWhiteSpace(pattern)) return Task.FromResult("[glob] 缺少 pattern 参数");
        var root = string.IsNullOrWhiteSpace(path) ? Directory.GetCurrentDirectory() : Path.GetFullPath(path);
        if (!Directory.Exists(root)) return Task.FromResult($"[glob] 目录不存在: {root}");
        try
        {
            const int maxFiles = 200;
            var files = Directory.EnumerateFiles(root, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = pattern.Contains("**"),
                IgnoreInaccessible = true,
            }).Take(maxFiles);
            var list = files.Select(f => Path.GetRelativePath(root, f)).ToList();
            return list.Count == 0
                ? Task.FromResult("[glob] 无匹配")
                : Task.FromResult(list.Count >= maxFiles
                ? string.Join("\n", list) + $"\n...[已截断，超过 {maxFiles} 个文件]"
                : string.Join("\n", list));
        }
        catch (Exception e)
        {
            return Task.FromResult($"[glob] 失败: {e.Message}");
        }
    }
}

/// <summary>内容搜索工具：grep</summary>
public sealed class GrepTool : ITool
{
    public string Name => "grep";
    public object Definition => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = "在目录内按正则搜索文件内容，返回 文件:行号:匹配行。path 为起始目录（缺省当前目录）。",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    pattern = new { type = "string", description = "正则表达式" },
                    path = new { type = "string", description = "起始目录，可省" },
                    include = new { type = "string", description = "文件名过滤器，如 *.cs，可省" },
                    limit = new { type = "integer", description = "最多返回行数，缺省 50" },
                },
                required = new[] { "pattern" },
            },
        },
    };

    public Task<string> RunAsync(string arguments, CancellationToken ct = default)
    {
        var pattern = ToolArgs.GetString(arguments, "pattern");
        var path = ToolArgs.GetString(arguments, "path");
        var include = ToolArgs.GetString(arguments, "include");
        var limit = int.TryParse(ToolArgs.GetString(arguments, "limit"), out var l) ? l : 50;
        if (string.IsNullOrWhiteSpace(pattern)) return Task.FromResult("[grep] 缺少 pattern 参数");
        var root = string.IsNullOrWhiteSpace(path) ? Directory.GetCurrentDirectory() : Path.GetFullPath(path);
        if (!Directory.Exists(root)) return Task.FromResult($"[grep] 目录不存在: {root}");
        try
        {
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);
            var count = 0;
            var sb = new StringBuilder();
            foreach (var file in Directory.EnumerateFiles(root, include is { Length: > 0 } ? include : "*", new EnumerationOptions 
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                     }).TakeWhile(_ => !ct.IsCancellationRequested).TakeWhile(_ => count < limit))
            {
                try
                {
                    var rel = Path.GetRelativePath(root, file);
                    var lines = File.ReadAllLines(file);
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (count >= limit) break;
                        if (!regex.IsMatch(lines[i])) continue;
                        sb.AppendLine($"{rel}:{i + 1}: {lines[i]}");
                        count++;
                    }
                }
                catch { /* 跳过不可读文件 */ }
            }
            return Task.FromResult(count == 0 ? "[grep] 无匹配" : sb.ToString());
        }
        catch (Exception e)
        {
            return Task.FromResult($"[grep] 失败: {e.Message}");
        }
    }
}

/// <summary>文件编辑工具：edit_file（write/append/replace，替代 shell 写文件避免转义问题）</summary>
public sealed class EditFileTool : ITool
{
    public string Name => "edit_file";
    public object Definition => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = "写入/追加/替换文件内容（替代 shell 写文件，避免转义问题）。action: write（覆盖，缺省）、append（追加）、replace（用 content 替换第一个出现的 old）。",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "文件路径" },
                    action = new { type = "string", description = "write | append | replace，缺省 write" },
                    content = new { type = "string", description = "要写入/追加/替换的内容" },
                    old = new { type = "string", description = "replace 时被替换的原文（需精确匹配）" },
                },
                required = new[] { "path", "content" },
            },
        },
    };

    public Task<string> RunAsync(string arguments, CancellationToken ct = default)
    {
        var path = ToolArgs.GetString(arguments, "path");
        var action = ToolArgs.GetString(arguments, "action");
        var content = ToolArgs.GetString(arguments, "content");
        var old = ToolArgs.GetString(arguments, "old");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult("[edit_file] 缺少 path/content 参数");
        try
        {
            var full = Path.GetFullPath(path);
            switch (action)
            {
                case "write":
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllText(full, content);
                    return Task.FromResult($"[edit_file] 已写入 {full}（{content.Length} 字符）");
                case "append":
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.AppendAllText(full, content);
                    return Task.FromResult($"[edit_file] 已追加到 {full}（{content.Length} 字符）");
                case "replace":
                    if (string.IsNullOrEmpty(old))
                        return Task.FromResult("[edit_file] replace 需要 old 参数");
                    if (!File.Exists(full))
                        return Task.FromResult($"[edit_file] 文件不存在: {full}");
                    var text = File.ReadAllText(full);
                    var idx = text.IndexOf(old, StringComparison.Ordinal);
                    if (idx < 0)
                        return Task.FromResult("[edit_file] 未找到 old 文本（请检查大小写/空白是否精确一致）");
                    File.WriteAllText(full, text[..idx] + content + text[(idx + old.Length)..]);
                    return Task.FromResult($"[edit_file] 已替换 {old.Length} 字符 → {content.Length} 字符");
                default:
                    return Task.FromResult($"[edit_file] 未知 action: {action}（write|append|replace）");
            }
        }
        catch (Exception e)
        {
            return Task.FromResult($"[edit_file] 失败: {e.Message}");
        }
    }
}
