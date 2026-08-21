using System.Reflection;

namespace WTangent.Server.Store;

/// <summary>组件插件加载（install 之后）：用 McMaster.NETCore.Plugins 从组件目录加载程序集并调用入口。
/// TUI 插件：Agent.Tui.dll 的 TuiRepl.RunAsync(url)（LLM 仍在 serve，TUI 只是客户端）。
/// PreferSharedTypes=true：Agent.Core 等宿主类型用宿主的（避免双份类型冲突），插件目录里的 Terminal.Gui 等就地加载。</summary>
public static class PluginLoader
{
    public static string ComponentsDir => Path.Combine(AgentPaths.DataDir, "components");

    /// <summary>组件是否已安装（components.json 标记 + 目录存在）</summary>
    public static bool Installed(string name)
    {
        var enabled = name switch { "tui" => Components.Load().Tui, "web" => Components.Load().Web, _ => false };
        return enabled && Directory.Exists(Path.Combine(ComponentsDir, name));
    }

    /// <summary>运行 TUI 插件（TuiRepl.RunAsync(url)），异步返回；找不到/加载失败抛异常。</summary>
    public static async Task RunTuiAsync(string url)
    {
        var dir = Path.Combine(ComponentsDir, "tui");
        var dll = Path.Combine(dir, "Agent.Tui.dll");
        if (!File.Exists(dll)) throw new InvalidOperationException($"TUI 组件未安装（缺 {dll}），先 agent install tui --from <Tui 构建输出>");
        using var loader = McMaster.NETCore.Plugins.PluginLoader.CreateFromAssemblyFile(dll, config => config.PreferSharedTypes = true);
        var asm = loader.LoadDefaultAssembly();
        var type = asm.GetType("Agent.Tui.TuiRepl")
            ?? throw new InvalidOperationException("TUI 插件缺少 Agent.Tui.TuiRepl");
        var method = type.GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Static, [typeof(string)])
            ?? throw new InvalidOperationException("TUI 插件缺少 TuiRepl.RunAsync(string)");
        if (method.Invoke(null, [url]) is not Task task)
            throw new InvalidOperationException("TuiRepl.RunAsync 未返回 Task");
        await task;
    }
}
