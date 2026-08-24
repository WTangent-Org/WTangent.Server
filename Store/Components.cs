using System.Text.Json;

namespace WTangent.Server.Store;

/// <summary>组件安装标记（%APPDATA%\agent\components.json）：agent install tui/gui 写入；agent tui/gui 检查。
/// 版本字段记录安装时的 release tag（版本化：同版本跳过下载；"dev" = 本地目录安装）。
/// Channel 为默认安装频道（release/debug，agent install channel 查看/设置）。</summary>
public sealed class Components
{
    public bool Tui { get; set; }
    public bool Gui { get; set; }   // WPF 桌面端（TODO #3，未实现）
    public bool Web { get; set; }   // Web UI（install web 安装到 %APPDATA%\agent\web，serve 检测到就托管）
    public string TuiVersion { get; set; } = "";
    public string WebVersion { get; set; } = "";
    public string Channel { get; set; } = "stable";

    private static string Path => System.IO.Path.Combine(AgentPaths.DataDir, "components.json");

    public static Components Load()
    {
        if (!File.Exists(Path)) return new Components();
        try { return JsonSerializer.Deserialize<Components>(File.ReadAllText(Path)) ?? new Components(); }
        catch { return new Components(); }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public void Save() =>
        File.WriteAllText(Path, JsonSerializer.Serialize(this, JsonOpts));
}
