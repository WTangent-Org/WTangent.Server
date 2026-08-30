using System.Text;
using System.Text.RegularExpressions;

namespace WTangent.Server.Tools;

/// <summary>read 工具：读文件内容（cat -n 式行号，offset/limit 分页）。
/// 对齐 Claude/pi/kimi 的 read 规范：搜索用 grep、目录浏览用 glob、报错可恢复。</summary>
public sealed class ReadFileTool : ITool
{
    public string Name => "read";

    public object Definition => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = @"读取文件内容，返回带行号的文本（cat -n 格式）。

Tips:
- 支持 offset（起始行，1 起）与 limit（行数）分页读取大文件。
- 内容搜索请用 grep，文件名查找请用 glob，目录列表用 bash（Get-ChildItem）。
- 文件不存在或路径非法会返回错误，不会中断会话。
- 只能读文本文件；二进制文件请用 bash 对应工具。".Replace("\r\n", "\n"),
            parameters = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "文件路径（相对或绝对）" },
                    offset = new { type = "integer", description = "起始行号（1 起），可省" },
                    limit = new { type = "integer", description = "最多读取行数，可省" },
                },
                required = new[] { "path" },
            },
        },
    };

    public Task<string> RunAsync(string arguments, CancellationToken ct = default)
    {
        var path = ToolArgs.GetString(arguments, "path");
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult("[read] 缺少 path 参数");
        if (!File.Exists(path)) return Task.FromResult($"[read] 文件不存在: {path}");
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
            return Task.FromResult($"[read] 读取失败: {e.Message}");
        }
    }
}

/// <summary>glob 工具：按 glob 模式查找文件（** 递归；Windows searchPattern 不吃路径分隔符，内部拆分）</summary>
public sealed class GlobTool : ITool
{
    public string Name => "glob";

    public object Definition => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = @"按 glob 模式查找文件，返回相对路径列表。

Tips:
- 模式支持 ** 跨目录递归（如 **/*.cs）与字面目录前缀（如 src/*.ts）。
- path 为起始目录（缺省当前目录）；结果最多 200 个，超出会截断提示。
- 按内容搜索请用 grep。".Replace("\r\n", "\n"),
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
            // Windows searchPattern 不接受路径分隔符（**/*.cs 整串传入会直接抛参数错误）：
            // 拆出目录前缀与文件名模式；前缀或模式含 ** → 递归，字面前缀 → 拼进扫描根
            var prefix = pattern;
            var search = "*";
            var sep = pattern.LastIndexOfAny(['/', '\\']);
            if (sep >= 0)
            {
                prefix = pattern[..sep];
                search = pattern[(sep + 1)..];
                if (search.Length == 0) search = "*";
            }
            var wildcardPrefix = prefix.Contains('*') || prefix.Contains('?');
            var recurse = pattern.Contains("**") || wildcardPrefix;
            var scanRoot = prefix.Length > 0 && !wildcardPrefix ? Path.Combine(root, prefix.Replace('\\', '/')) : root;
            const int maxFiles = 200;
            var files = Directory.EnumerateFiles(scanRoot, search, new EnumerationOptions
            {
                RecurseSubdirectories = recurse,
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

/// <summary>grep 工具：正则搜索文件内容（对齐 Claude grep：上下文行/大小写开关/output_mode）</summary>
public sealed class GrepTool : ITool
{
    public string Name => "grep";

    public object Definition => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = @"在目录内按正则搜索文件内容。

Tips:
- 输出 文件:行号:匹配行；output_mode=count 每文件匹配数；files_with_matches 只列文件名。
- context 为每个匹配附加的前后行数；include 过滤文件名（如 *.cs）。
- 找文件名用 glob；读文件用 read。递归全目录、跳过隐藏/系统文件。".Replace("\r\n", "\n"),
            parameters = new
            {
                type = "object",
                properties = new
                {
                    pattern = new { type = "string", description = "正则表达式" },
                    path = new { type = "string", description = "起始目录，可省" },
                    include = new { type = "string", description = "文件名过滤器，如 *.cs，可省" },
                    case_insensitive = new { type = "boolean", description = "忽略大小写（缺省 true）" },
                    context = new { type = "integer", description = "每个匹配附带的前后行数，可省" },
                    output_mode = new { type = "string", description = "content（缺省）| count | files_with_matches" },
                    limit = new { type = "integer", description = "最多返回匹配数，缺省 50" },
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
        var ci = !bool.TryParse(ToolArgs.GetString(arguments, "case_insensitive"), out var ciRaw) || ciRaw;
        var context = int.TryParse(ToolArgs.GetString(arguments, "context"), out var ctx) ? Math.Max(0, ctx) : 0;
        var mode = ToolArgs.GetString(arguments, "output_mode");
        if (mode is not ("count" or "files_with_matches")) mode = "content";
        var limit = int.TryParse(ToolArgs.GetString(arguments, "limit"), out var l) ? l : 50;
        if (string.IsNullOrWhiteSpace(pattern)) return Task.FromResult("[grep] 缺少 pattern 参数");
        var root = string.IsNullOrWhiteSpace(path) ? Directory.GetCurrentDirectory() : Path.GetFullPath(path);
        if (!Directory.Exists(root)) return Task.FromResult($"[grep] 目录不存在: {root}");
        try
        {
            var regex = new Regex(pattern, ci ? RegexOptions.IgnoreCase : RegexOptions.None);
            var count = 0;
            var sb = new StringBuilder();
            var fileMatches = new List<string>();
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
                    var hits = new List<int>();
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (regex.IsMatch(lines[i])) hits.Add(i);
                    }
                    if (hits.Count == 0) continue;
                    fileMatches.Add(rel);
                    switch (mode)
                    {
                        case "files_with_matches":
                            sb.AppendLine(rel);
                            break;
                        case "count":
                            sb.AppendLine($"{rel}:{hits.Count}");
                            break;
                        default:
                            // 带上下文：合并相邻匹配的窗口后输出
                            var emitted = new HashSet<int>();
                            foreach (var hit in hits.TakeWhile(_ => count < limit))
                            {
                                for (var i = Math.Max(0, hit - context); i <= Math.Min(lines.Length - 1, hit + context); i++)
                                {
                                    if (emitted.Add(i)) sb.AppendLine($"{rel}:{i + 1}: {lines[i]}");
                                }
                                count++;
                                if (count >= limit) break;
                            }
                            break;
                    }
                    if (count >= limit && mode == "content") break;
                }
                catch { /* 跳过不可读文件 */ }
            }
            if (fileMatches.Count == 0) return Task.FromResult("[grep] 无匹配");
            if (mode != "content") sb.Insert(0, $"[grep] {fileMatches.Count} 个文件命中\n");
            return Task.FromResult(sb.ToString().TrimEnd());
        }
        catch (Exception e)
        {
            return Task.FromResult($"[grep] 失败: {e.Message}");
        }
    }
}

/// <summary>edit 工具：精确字符串替换（对齐 Claude Edit：old_string 唯一性校验 + replace_all）</summary>
public sealed class EditFileTool : ITool
{
    public string Name => "edit";

    public object Definition => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = @"精确替换文件中的文本。

Tips:
- old_string 必须与文件内容**精确一致**（含缩进/空白）；在文件中不唯一时会报错，此时扩大上下文或用 replace_all。
- replace_all=true 替换全部出现（适合重命名标识符）。
- 新建文件用 write；追加内容可让 old_string 锚定文件结尾。".Replace("\r\n", "\n"),
            parameters = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "文件路径" },
                    old_string = new { type = "string", description = "被替换的原文（精确匹配）" },
                    new_string = new { type = "string", description = "替换后的新文本（可为空串=删除）" },
                    replace_all = new { type = "boolean", description = "替换全部出现（缺省 false，仅替换唯一一处）" },
                },
                required = new[] { "path", "old_string", "new_string" },
            },
        },
    };

    public Task<string> RunAsync(string arguments, CancellationToken ct = default)
    {
        var path = ToolArgs.GetString(arguments, "path");
        var old = ToolArgs.GetString(arguments, "old_string");
        var newStr = ToolArgs.GetString(arguments, "new_string");
        var all = bool.TryParse(ToolArgs.GetString(arguments, "replace_all"), out var a) && a;
        if (string.IsNullOrWhiteSpace(path) || old is null || newStr is null)
            return Task.FromResult("[edit] 缺少 path/old_string/new_string 参数");
        if (!File.Exists(path)) return Task.FromResult($"[edit] 文件不存在: {path}");
        try
        {
            var text = File.ReadAllText(path);
            var occurrences = CountOccurrences(text, old);
            if (occurrences == 0)
                return Task.FromResult("[edit] 未找到 old_string（检查大小写/缩进/空白是否与文件精确一致）");
            if (occurrences > 1 && !all)
                return Task.FromResult($"[edit] old_string 出现 {occurrences} 次而不唯一：扩大上下文使其唯一，或 replace_all=true");
            File.WriteAllText(path, all ? text.Replace(old, newStr) : text.Replace(old, newStr, StringComparison.Ordinal));
            return Task.FromResult($"[edit] 已替换 {occurrences} 处（{old.Length} → {newStr.Length} 字符）: {path}");
        }
        catch (Exception e)
        {
            return Task.FromResult($"[edit] 失败: {e.Message}");
        }
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
