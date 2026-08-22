using System.Reflection;
using System.Text.Json;
using WTangent.Core;

namespace WTangent.Server.Tools;

/// <summary>工具加载器：serve 启动时扫描已装组件目录，加载含 IEntry 的组件 dll，
/// 取其 Entry.Tools（非空才收集）合并进工具列表。单组件失败跳过并警告，不阻断 serve。</summary>
public static class ToolComponentLoader
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>已装组件目录（%APPDATA%\agent\components）</summary>
    private static string ComponentsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "agent", "components");

    /// <summary>加载所有组件提供的工具（IEntry.Tools 非空的组件）；单组件失败跳过并警告，不阻断 serve</summary>
    public static List<ITool> Load()
    {
        var result = new List<ITool>();
        var app = Entry.App;   // 壳启动时已注入（StartAsync）
        if (app is null || !Directory.Exists(ComponentsDir)) return result;
        foreach (var dir in Directory.GetDirectories(ComponentsDir))
        {
            var manifestFile = Path.Combine(dir, "agent-component.json");
            ManifestInfo? manifest = null;
            try
            {
                if (File.Exists(manifestFile))
                    manifest = JsonSerializer.Deserialize<ManifestInfo>(File.ReadAllText(manifestFile), JsonOpts);
            }
            catch { }
            if (manifest is null) continue;

            var dll = Path.Combine(dir, manifest.Asset + ".dll");
            if (!File.Exists(dll)) continue;
            try
            {
                var asm = Assembly.LoadFrom(dll);
                var entryType = asm.GetTypes().FirstOrDefault(t => t is { IsPublic: true, IsAbstract: false }
                    && typeof(IEntry).IsAssignableFrom(t));
                if (entryType is null) continue;
                var entry = (IEntry)Activator.CreateInstance(entryType)!;
                entry.StartAsync(app).GetAwaiter().GetResult();
                var tools = entry.Tools;
                if (tools.Count > 0)
                {
                    Console.WriteLine($"[serve] 已加载 {manifest.Name} 工具：{tools.Count} 个（{string.Join(", ", tools.Select(t => t.Name))}）");
                    result.AddRange(tools);
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[serve] 加载组件 {manifest.Name} 工具失败：{e.Message}");
            }
        }
        return result;
    }

    private sealed record ManifestInfo(string Name, string Asset);
}
