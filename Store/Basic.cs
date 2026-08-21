namespace WTangent.Server.Store;

/// <summary>全局开发模式开关（IsDebug）：控制组件来源等开发/发布差异。
/// 编译期 DEBUG 符号默认开启（Debug 构建 = true，Release = false），可用环境变量 AGENT_DEBUG 运行时覆盖（"1"/"true" = 开）。</summary>
public static class Basic
{
    /// <summary>当前是否为开发模式（Debug 构建，或 AGENT_DEBUG 环境变量覆盖）</summary>
    public static bool IsDebug
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("AGENT_DEBUG");
            if (env is not null)
                return env is "1" or "true" or "TRUE";
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }
}
