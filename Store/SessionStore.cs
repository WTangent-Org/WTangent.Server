using WTangent.Server.Session;
using Microsoft.Data.Sqlite;

namespace WTangent.Server.Store;

/// <summary>持久化会话：SQLite 存会话+消息，支持续聊</summary>
public sealed class SessionStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string _activeProvider;

    public SessionStore(string? providerName = null)
    {
        _activeProvider = providerName ?? ConfigStore.LoadActive()?.Name ?? "default";
        var dir = AgentPaths.DataDir;
        Directory.CreateDirectory(dir);
        _conn = new SqliteConnection($"Data Source={Path.Combine(dir, "agent.db")}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS session (
                id TEXT PRIMARY KEY,
                provider TEXT NOT NULL,
                title TEXT NOT NULL DEFAULT '',
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS message (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                role TEXT NOT NULL,
                content TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                FOREIGN KEY(session_id) REFERENCES session(id)
            );
            CREATE INDEX IF NOT EXISTS idx_message_session ON message(session_id);
            """;
        cmd.ExecuteNonQuery();
    }

    public sealed record SessionInfo(string Id, string Title, long CreatedAt, long UpdatedAt, int Count);

    /// <summary>历史会话列表（当前提供商）</summary>
    public List<SessionInfo> ListSessions()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.title, s.created_at, s.updated_at,
                   (SELECT COUNT(*) FROM message m WHERE m.session_id = s.id) AS cnt
            FROM session s
            WHERE s.provider = $p
            ORDER BY s.updated_at DESC
            """;
        cmd.Parameters.AddWithValue("$p", _activeProvider);
        var list = new List<SessionInfo>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SessionInfo(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt32(4)));
        }
        return list;
    }

    public string NewSession(string title)
    {
        var id = Guid.NewGuid().ToString("N");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO session (id, provider, title, created_at, updated_at) VALUES ($id, $p, $t, $n, $n)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$p", _activeProvider);
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$n", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
        return id;
    }

    public void UpdateTitle(string sessionId, string title)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE session SET title = $t, updated_at = $n WHERE id = $id";
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$n", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.ExecuteNonQuery();
    }

    public void AddMessage(string sessionId, string role, string content)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO message (session_id, role, content, created_at) VALUES ($id, $r, $c, $n)";
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.Parameters.AddWithValue("$r", role);
        cmd.Parameters.AddWithValue("$c", content);
        cmd.Parameters.AddWithValue("$n", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    /// <summary>检查该 (会话,角色,内容) 是否已存在（import 去重用）</summary>
    public bool MessageExists(string sessionId, string role, string content)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM message WHERE session_id = $id AND role = $r AND content = $c";
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.Parameters.AddWithValue("$r", role);
        cmd.Parameters.AddWithValue("$c", content);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>导入用：按指定 id/时间建会话（已存在则忽略）</summary>
    public void CreateSession(string sessionId, string title, long createdAt)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO session (id, provider, title, created_at, updated_at) VALUES ($id, $p, $t, $c, $c)";
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.Parameters.AddWithValue("$p", _activeProvider);
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$c", createdAt);
        cmd.ExecuteNonQuery();
    }

    /// <summary>读取会话历史（不含 system），按时间正序</summary>
    public List<ChatMessage> LoadMessages(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT role, content FROM message WHERE session_id = $id ORDER BY id";
        cmd.Parameters.AddWithValue("$id", sessionId);
        var list = new List<ChatMessage>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ChatMessage { Role = ParseRole(r.GetString(0)), Content = r.GetString(1) });
        }
        return list;
    }
    
    private static MessageRole ParseRole(string role) => role.ToLower() switch
    {
        "system" => MessageRole.System,
        "user" => MessageRole.User,
        "assistant" => MessageRole.Assistant,
        "tool" => MessageRole.Tool,
        _ => throw new ArgumentException($"Unknown role: {role}")
    };

    public void Dispose() => _conn.Dispose();
}
