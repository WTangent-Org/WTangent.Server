using System.Text.Json;

namespace WTangent.Server.Store;

/// <summary>服务器条目：Name 标签、Host IP、Port 端口（分离存储，http 自动拼）、EtCode EasyTier 加入码（可空）、Kind 传输类型。
/// lan = 局域网直连（无加入码）；et = 跨网（加入码）。名字唯一标识；账号凭据是**全局**的（AgentCredentials，不按服务器记账）。
/// default-server = last-used 缓存（ServerRegistry.GetLastUsed），不存这里。</summary>
public sealed record RemoteEntry(string Name, string Host, int Port, string? EtCode, string Kind = "lan")
{
    public string Url => $"http://{Host}:{Port}";
}

/// <summary>服务器注册表（remotes.json，[{name,host,port,etCode,kind}]）：agent remote 命令读写；
/// clone/run 按加入码或名字取地址；last-used 缓存记录最近用的服务器（default-server 语义）。</summary>
public sealed class ServerRegistry(string? path = null)
{
    private string StorePath => path ?? Path.Combine(AgentPaths.DataDir, "remotes.json");
    private static string LastUsedFile => Path.Combine(AgentPaths.DataDir, "last-remote.txt");

    public void Add(string name, string host, int port, string? code = null, string kind = "lan")
    {
        var all = Load();
        all.RemoveAll(r => r.Name == name);
        all.Add(new RemoteEntry(name, host, port, code, kind));
        Save(all);
    }

    public void Remove(string name)
    {
        var all = Load();
        all.RemoveAll(r => r.Name == name);
        Save(all);
    }

    public List<RemoteEntry> List() => Load();

    public string? Get(string name) => Load().FirstOrDefault(r => r.Name == name)?.Url;

    /// <summary>按 ET 加入码（优先）或名字查找；找不到返回 null</summary>
    public RemoteEntry? Find(string codeOrName) =>
        Load().FirstOrDefault(r => r.EtCode is { Length: > 0 } && r.EtCode == codeOrName)
        ?? Load().FirstOrDefault(r => r.Name == codeOrName);

    /// <summary>default-server = 缓存：上次成功使用的 remote（名字或 URL）</summary>
    public static string? GetLastUsed() =>
        File.Exists(LastUsedFile) ? File.ReadAllText(LastUsedFile).Trim() : null;

    public static void SetLastUsed(string nameOrUrl)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(LastUsedFile)!); File.WriteAllText(LastUsedFile, nameOrUrl); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private List<RemoteEntry> Load()
    {
        if (!File.Exists(StorePath)) return [];
        try
        {
            var json = File.ReadAllText(StorePath);
            using var doc = JsonDocument.Parse(json);
            // 兼容旧格式 {name:url} → 迁移为 [{name,host,port,etCode,kind}]
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var migrated = doc.RootElement.EnumerateObject()
                    .Select(p => EntryFromUrl(p.Name, p.Value.GetString() ?? ""))
                    .ToList();
                Save(migrated);
                return migrated;
            }
            // 兼容旧字段名 "Code" → "EtCode"、"Url" → Host+Port
            if (json.Contains("\"Code\":", StringComparison.Ordinal) && !json.Contains("\"EtCode\":", StringComparison.Ordinal))
                json = json.Replace("\"Code\":", "\"EtCode\":", StringComparison.Ordinal);
            if (!json.Contains("\"Url\":", StringComparison.Ordinal) || json.Contains("\"Host\":", StringComparison.Ordinal))
                return JsonSerializer.Deserialize<List<RemoteEntry>>(json) ?? [];
            var old = JsonSerializer.Deserialize<List<OldRemote>>(json) ?? [];
            var urlMigrated = old.Select(o => EntryFromUrl(o.Name, o.Url, o.EtCode, o.Kind)).ToList();
            Save(urlMigrated);
            return urlMigrated;
        }
        catch { return []; }
    }

    private static RemoteEntry EntryFromUrl(string name, string url, string? code = null, string kind = "lan")
    {
        var u = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : new Uri("http://" + url);
        return new RemoteEntry(name, u.Host, u.Port > 0 ? u.Port : 8890, code, kind);
    }

    private sealed record OldRemote(string Name, string Url, string? EtCode, string Kind);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private void Save(List<RemoteEntry> all) =>
        File.WriteAllText(StorePath, JsonSerializer.Serialize(all, JsonOpts));
}
