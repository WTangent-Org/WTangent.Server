using WTangent.Core;

namespace WTangent.Server;

/// <summary>serve 组件入口（[AgentEntry] 元数据 + [EntryStart]/[EntryStop] 钩子；
/// App/Current/Commands 由生成器产出）。</summary>
[AgentEntry("serve", "serve 服务", false)]
public sealed partial class Entry : IEntry
{
    [EntryStart]
    private static void OnStart(Application app) => app.Logger.Info("serve 组件已启动");

    [EntryStop]
    private static void OnStop() { }
}
