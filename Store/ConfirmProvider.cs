namespace WTangent.Server.Store;

/// <summary>交互确认提供者：单槽替换式确认处理器，UI 层（TUI/serve）设置后退出时必须置回 null（恢复 Console 默认），
/// 避免静态引用悬空已释放的 UI 上下文。无处理器时回退 Console 版（y/N）。</summary>
public static class ConfirmProvider
{
    /// <summary>确认处理器：替换式赋值；null = 使用 Console 默认。UI 层退出时置回 null。</summary>
    public static Func<string, bool> Confirm { get; set; } = DefaultConfirm;

    /// <summary>发起确认：有处理器走处理器，无则走 Console 默认（y/N）</summary>
    public static bool Ask(string prompt) => Confirm(prompt);

    public static bool DefaultConfirm(string prompt)
    {
        Console.Error.Write($"{prompt} [y/N] ");
        var line = Console.ReadLine();
        return line?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true;
    }
}
