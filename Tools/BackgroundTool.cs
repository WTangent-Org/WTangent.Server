namespace WTangent.Server.Tools;

/// <summary>后台进程管理工具：status 查状态，kill 杀进程</summary>
public sealed class BackgroundTool : ITool
{
    public string Name => "background";

    public object Definition => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = "管理后台进程。action=status 查状态/输出，action=kill 杀进程。id 为后台进程 PID。",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    action = new { type = "string", description = "status 或 kill" },
                    id = new { type = "integer", description = "后台进程 PID" },
                },
                required = new[] { "action", "id" },
            },
        },
    };

    public Task<string> RunAsync(string arguments, CancellationToken ct = default)
    {
        var action = ToolArgs.GetString(arguments, "action");
        var id = int.TryParse(ToolArgs.GetString(arguments, "id"), out var i) ? i : -1;
        return id < 0
            ? Task.FromResult("[background] 缺少有效 id")
            : Task.FromResult(action switch
        {
            "status" => BackgroundProcess.Status(id),
            "kill" => BackgroundProcess.Kill(id),
            _ => $"[background] 未知 action: {action}",
        });
    }
}
