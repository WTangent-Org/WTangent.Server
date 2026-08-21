namespace WTangent.Server.Store;

/// <summary>运行时数据目录（%APPDATA%\agent）：ConfigStore / SessionStore 共用，无 ApplicationData 时回退用户主目录</summary>
public static class AgentPaths
{
    public static string DataDir => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } appData
        ? Path.Combine(appData, "agent")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "agent");
}
