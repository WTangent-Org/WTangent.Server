namespace WTangent.Server;

/// <summary>serve 组件入口（[AgentEntry] 元数据 + [EntryStart] 钩子；
/// App/Commands 由生成器产出，App 经构造注入静态 Entry.App）。</summary>
[AgentEntry("serve", "serve 服务", false)]
public sealed partial class Entry : IEntry
{
    [EntryStart]
    private static void OnStart() => Log.Info("serve 组件已启动");
}
