using System.Reflection;
using System.Text.Json;

namespace WTangent.Server.Tools;

/// <summary>tool 类型组件加载器：serve 启动时扫描已装组件目录，
/// 加载 manifest Type=="tool" 的组件，取其入口 Entry.Tools（ITool 列表）合并进工具列表。</summary>
public static class ToolComponentLoader
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>已装组件目录（%APPDATA%\agent\components）</summary>
    private static string ComponentsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "agent", "components");

    /// <summary>加载所有 tool 组件提供的工具；单组件失败跳过并警告，不阻断 serve</summary>
    public static List<ITool> Load()
    {
        var result = new List<ITool>();
        if (!Directory.Exists(ComponentsDir)) return result;
        foreach (var dir in Directory.GetDirectories(ComponentsDir))
        {
            ManifestInfo? manifest = null;
            try
            {
                var manifestFile = Path.Combine(dir, "agent-component.json");
                if (File.Exists(manifestFile))
                    manifest = JsonSerializer.Deserialize<ManifestInfo>(File.ReadAllText(manifestFile), JsonOpts);
            }
            catch { }
            if (manifest is not { Type: "tool" }) continue;

            var dll = Path.Combine(dir, manifest.Asset + ".dll");
            if (!File.Exists(dll))
            {
                Console.Error.WriteLine($"[serve] tool 组件 {manifest.Name} 缺少 {manifest.Asset}.dll，跳过");
                continue;
            }
            try
            {
                var asm = Assembly.LoadFrom(dll);
                var entry = asm.GetTypes().FirstOrDefault(t => t is { Name: "Entry", IsPublic: true, IsAbstract: true, IsSealed: true });
                if (entry is null)
                {
                    Console.Error.WriteLine($"[serve] tool 组件 {manifest.Name} 缺少入口类型（public static class Entry），跳过");
                    continue;
                }
                var prop = entry.GetProperty("Tools", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new InvalidOperationException("工具组件入口 Tools 属性缺失");
                if (prop.GetValue(null) is IEnumerable<ITool> tools)
                {
                    var list = tools.ToList();
                    Console.WriteLine($"[serve] 已加载 tool 组件 {manifest.Name}：{list.Count} 个工具（{string.Join(", ", list.Select(t => t.Name))}）");
                    result.AddRange(list);
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[serve] 加载 tool 组件 {manifest.Name} 失败：{e.Message}");
            }
        }
        return result;
    }

    private sealed record ManifestInfo(string Name, string Type, string Asset);
}
