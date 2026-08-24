using System.Collections.Concurrent;

namespace WTangent.Server.Tools;

/// <summary>后台进程管理：bash 工具 background 模式启动的进程，供查询/杀</summary>
public static class BackgroundProcess
{
    private sealed class Entry
    {
        public required ProcessHandle Proc { get; init; }
        public long StartedAt { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static readonly ConcurrentDictionary<int, Entry> Procs = new();

    public static void Register(ProcessHandle proc) => Procs[proc.Id] = new Entry { Proc = proc };

    public static ProcessHandle? Get(int id) => Procs.TryGetValue(id, out var e) ? e.Proc : null;

    /// <summary>查状态：仍在运行 / 已退出（含退出码）+ 已收集输出</summary>
    public static string Status(int id)
    {
        if (!Procs.TryGetValue(id, out var e))
            return $"[后台 {id}] 不存在";
        var p = e.Proc;
        if (p.Running)
            return $"[后台 {id}] 运行中（PID {id}，启动 {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - e.StartedAt}ms）";
        Procs.TryRemove(id, out _);
        return $"[后台 {id}] 已退出 exit={p.ExitCode}\n{p.Output}";
    }

    /// <summary>杀掉后台进程（含树），返回结果</summary>
    public static string Kill(int id)
    {
        if (!Procs.TryRemove(id, out var e))
            return $"[后台 {id}] 不存在";
        e.Proc.Kill();
        return $"[后台 {id}] 已终止";
    }
}

/// <summary>后台进程句柄：持有进程引用 + 可查询输出</summary>
public sealed class ProcessHandle(System.Diagnostics.Process proc, Task<string> outTask, Task<string> errTask)
{
    public int Id => proc.Id;
    public bool Running => !proc.HasExited;
    public int ExitCode => proc.HasExited ? proc.ExitCode : -1;
    public string Output => $"{(outTask.IsCompleted ? outTask.Result : "")}{(errTask.IsCompleted ? errTask.Result : "")}";

    public void Kill()
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
