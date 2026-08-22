using System.CommandLine;
using WTangent.Core;

namespace WTangent.Server;

/// <summary>serve 组件入口（手写实现 IEntry；生成器收集的命令经 CollectedCommands 接线）。
/// 生命周期：StartAsync 存宿主注入的 Application（组件内静态访问 Entry.App）。</summary>
public sealed partial class Entry : IEntry
{
    /// <summary>宿主运行时上下文（StartAsync 注入；组件内部静态访问）</summary>
    public static Application? App { get; private set; }

    public string Identifier => "serve";
    public string Name => "serve 服务";
    public (Command Command, string? ParentPath)[] Commands => CollectedCommands;

    public Task StartAsync(Application app)
    {
        App = app;
        app.Logger.Info("serve 组件已启动");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        App = null;
        return Task.CompletedTask;
    }
}
