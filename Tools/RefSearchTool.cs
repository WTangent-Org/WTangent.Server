using System.Text;
using System.Text.RegularExpressions;

namespace WTangent.Server.Tools;

/// <summary>符号引用搜索（近似 Shift+F12）：找 symbol 的定义与引用，按文件:行号输出。
/// 定义 = class/record/struct/interface/enum/方法/属性声明行；引用 = 其余出现。排除 obj/bin/.git 等。</summary>
public sealed class RefSearchTool : ITool
{
    public string Name => "refs";

    public object Definition => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = "符号引用搜索（定义+引用，Shift+F12 近似）：找符号（类/方法/属性/字段名）在代码中的所有出现。" +
                          "输出 文件:行号: [定义|引用] 行内容。path 为项目目录（缺省当前目录）。",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    symbol = new { type = "string", description = "符号名（类/方法/属性/字段名）" },
                    path = new { type = "string", description = "项目目录，可省（缺省当前目录）" },
                    include = new { type = "string", description = "文件名过滤器，缺省 *.cs" },
                    limit = new { type = "integer", description = "最多返回行数，缺省 80" },
                },
                required = new[] { "symbol" },
            },
        },
    };

    private static readonly string[] ExcludedDirs = ["obj", "bin", ".git", "node_modules", "dist", ".vs"];

    public Task<string> RunAsync(string arguments, CancellationToken ct = default)
    {
        var symbol = ToolArgs.GetString(arguments, "symbol");
        var path = ToolArgs.GetString(arguments, "path");
        var include = ToolArgs.GetString(arguments, "include");
        var limit = int.TryParse(ToolArgs.GetString(arguments, "limit"), out var l) ? l : 80;
        if (string.IsNullOrWhiteSpace(symbol)) return Task.FromResult("[refs] 缺少 symbol 参数");
        var root = string.IsNullOrWhiteSpace(path) ? Directory.GetCurrentDirectory() : Path.GetFullPath(path);
        if (!Directory.Exists(root)) return Task.FromResult($"[refs] 目录不存在: {root}");
        try
        {
            var esc = Regex.Escape(symbol);
            var declRe = new Regex(
                $@"\b(?:class|record|struct|interface|enum|delegate)\s+{esc}\b|\b(?:public|private|internal|protected)\s+(?:static\s+|readonly\s+|sealed\s+|abstract\s+|virtual\s+)*[\w<>\[\],\?\. ]+\s+{esc}\s*\(",
                RegexOptions.IgnoreCase);
            var useRe = new Regex($@"\b{esc}\b");
            var count = 0;
            var sb = new StringBuilder();
            foreach (var file in Directory.EnumerateFiles(root, include is { Length: > 0 } ? include : "*.cs",
                         new EnumerationOptions
                         {
                             RecurseSubdirectories = true,
                             IgnoreInaccessible = true,
                             AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                         }).TakeWhile(_ => !ct.IsCancellationRequested).TakeWhile(_ => count < limit))
            {
                var rel = Path.GetRelativePath(root, file);
                if (ExcludedDirs.Any(d => rel.Split(Path.DirectorySeparatorChar).Contains(d))) continue;
                try
                {
                    var lines = File.ReadAllLines(file);
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (count >= limit) break;
                        var kind = declRe.IsMatch(lines[i]) ? "定义" : useRe.IsMatch(lines[i]) ? "引用" : null;
                        if (kind is null) continue;
                        sb.AppendLine($"{rel}:{i + 1}: [{kind}] {lines[i].Trim()}");
                        count++;
                    }
                }
                catch { /* 跳过不可读文件 */ }
            }
            return Task.FromResult(count == 0 ? "[refs] 无匹配" : sb.ToString());
        }
        catch (Exception e)
        {
            return Task.FromResult($"[refs] 失败: {e.Message}");
        }
    }
}
