using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WTangent.Server.Store;

/// <summary>单个提供商：BaseUrl + 模型（API Key 单独加密文件存储，不入 json）</summary>
public sealed record ProviderEntry
{
    public string Name { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string Model { get; init; } = "";
    public string Variants { get; init; } = "Default";
    [JsonIgnore]
    public string ApiKey { get; init; } = "";
}

/// <summary>全局配置：多提供商，Active 指定当前使用的提供商</summary>
public sealed record AgentConfig
{
    public string Active { get; init; } = "deepseek";
    public List<ProviderEntry> Providers { get; init; } = [];
    /// <summary>收到 git push 后自动触发 agent 简单优化（默认关，省 token；WUI 设置里可开）</summary>
    public bool AutoOptimize { get; init; }
}

public static class ConfigStore
{
    private static readonly string Dir = AgentPaths.DataDir;
    private static readonly string JsonFile = Path.Combine(Dir, "config.json");

    private static string KeyFile(string name) => Path.Combine(Dir, $"apikey.{Sanitize(name)}");
    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    public static AgentConfig Load()
    {
        var cfg = new AgentConfig();
        if (!File.Exists(JsonFile)) return cfg;
        try
        {
            var stored = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(JsonFile));
            if (stored != null)
                cfg = stored with { Providers = [.. stored.Providers.Select(p => p with { ApiKey = ReadKey(p.Name) })] };
        }
        catch { }
        return cfg;
    }

    /// <summary>当前激活的提供商（含 API Key），无则返回 null</summary>
    public static ProviderEntry? LoadActive()
    {
        var cfg = Load();
        return cfg.Providers.FirstOrDefault(p => p.Name == cfg.Active) ?? cfg.Providers.FirstOrDefault();
    }

    public static void Save(AgentConfig cfg)
    {
        Directory.CreateDirectory(Dir);
        var publicPart = cfg with
        {
            Providers = [.. cfg.Providers.Select(p => p with { ApiKey = "" })],
        };
        File.WriteAllText(JsonFile, JsonSerializer.Serialize(publicPart));
        foreach (var p in cfg.Providers.Where(p => !string.IsNullOrEmpty(p.ApiKey)))
            WriteKey(p.Name, p.ApiKey);
    }

    private static string ReadKey(string name)
    {
        var file = KeyFile(name);
        if (!File.Exists(file)) return "";
        try
        {
            var data = File.ReadAllBytes(file);
            if (OperatingSystem.IsWindows())
                data = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch { return ""; }
    }

    private static void WriteKey(string name, string apiKey)
    {
        Directory.CreateDirectory(Dir);
        var data = Encoding.UTF8.GetBytes(apiKey);
        if (OperatingSystem.IsWindows())
            data = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(KeyFile(name), data);
    }
}
