using System.Text.Json;

namespace WTangent.Server.Store;

/// <summary>全局客户端凭据（%APPDATA%\agent\credentials.json）：User/Passwd 所有 remote 共用（鉴权用）。
/// 由 agent remote user &lt;name&gt; / agent remote passwd &lt;密码&gt; 写入；明文存储，未来加密。</summary>
public sealed class AgentCredentials
{
    public string? User { get; set; }
    public string? Passwd { get; set; }

    private static string Path => System.IO.Path.Combine(AgentPaths.DataDir, "credentials.json");

    public static AgentCredentials Load()
    {
        if (!File.Exists(Path)) return new AgentCredentials();
        try { return JsonSerializer.Deserialize<AgentCredentials>(File.ReadAllText(Path)) ?? new AgentCredentials(); }
        catch { return new AgentCredentials(); }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public void Save() =>
        File.WriteAllText(Path, JsonSerializer.Serialize(this, JsonOpts));
}
