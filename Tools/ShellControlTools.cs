using System.Text.Json;

namespace WTangent.Server.Tools;

/// <summary>bash_output 工具：查询后台任务的增量输出与状态（对齐 Claude BashOutput；infra 复用 BackgroundProcess）</summary>
public sealed class BashOutputTool : ITool
{
    public string Name => "bash_output";

    public object Definition => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = @"查询后台任务的输出与运行状态（bash 的 run_in_background=true 启动的任务）。

Tips:
- 返回运行中/已退出状态 + 累计输出（大输出截断）。
- 结束长任务用 kill_shell。".Replace("\r\n", "\n"),
            parameters = new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "integer", description = "后台任务 PID（bash 后台启动时返回）" },
                },
                required = new[] { "id" },
            },
        },
    };

    public Task<string> RunAsync(string arguments, CancellationToken ct = default)
    {
        var id = int.TryParse(ToolArgs.GetString(arguments, "id"), out var i) ? i : -1;
        return Task.FromResult(id < 0 ? "[bash_output] 缺少有效 id" : BackgroundProcess.Status(id));
    }
}

/// <summary>kill_shell 工具：终止后台任务进程树（对齐 Claude KillShell）</summary>
public sealed class KillShellTool : ITool
{
    public string Name => "kill_shell";

    public object Definition => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = "终止指定后台任务（进程树）。id 为 bash 后台启动时返回的 PID。",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "integer", description = "后台任务 PID" },
                },
                required = new[] { "id" },
            },
        },
    };

    public Task<string> RunAsync(string arguments, CancellationToken ct = default)
    {
        var id = int.TryParse(ToolArgs.GetString(arguments, "id"), out var i) ? i : -1;
        return Task.FromResult(id < 0 ? "[kill_shell] 缺少有效 id" : BackgroundProcess.Kill(id));
    }
}
