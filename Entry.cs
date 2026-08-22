using WTangent.Core;

namespace WTangent.Server;

/// <summary>serve 组件入口（[AgentEntry] 元数据 + [EntryStart]/[EntryStop] 钩子；
/// 命令由生成器从 [AgentCommand] 收集）。</summary>
[AgentEntry("serve", "serve 服务", false)]
public sealed partial class Entry : IEntry
{
    /// <summary>宿主运行时上下文（StartAsync 注入；组件内部静态访问）</summary>
    public static Application? App { get; private set; }

    [EntryStart]
    private static void OnStart(Application app)
    {
        App = app;
        app.Logger.Info("serve 组件已启动");
    }

    [EntryStop]
    private static void OnStop() => App = null;
}
