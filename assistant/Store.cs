using Microsoft.Data.Sqlite;

namespace Halisland.Assistant;

/// <summary>
/// Sohbetler, mesajlar ve hatırlananlar (SQLite). Dosya:
/// <c>%APPDATA%\com.halil.halisland\assistant.db</c> (ya da HALISLAND_DATA_DIR).
/// Her işlem kendi bağlantısını açar; WAL kipinde aynı anda okuma/yazma güvenli.
/// </summary>
public sealed class Store
{
    private readonly string _connectionString;

    public Store(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        DbPath = Path.Combine(dataDir, "assistant.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
        Migrate();
    }

    public string DbPath { get; }

    public static string DefaultDataDir() =>
        Environment.GetEnvironmentVariable("HALISLAND_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "com.halil.halisland");

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 3000;";
        pragma.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS conversations (
                id          INTEGER PRIMARY KEY,
                title       TEXT,
                created     INTEGER NOT NULL,
                updated     INTEGER NOT NULL,
                -- Bağlama sığmayan eski mesajların özeti ve özetin kapsadığı son mesaj.
                summary     TEXT,
                summary_upto INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS messages (
                id          INTEGER PRIMARY KEY,
                conversation_id INTEGER NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                role        TEXT NOT NULL,
                content     TEXT NOT NULL,
                thinking    TEXT,
                error       TEXT,
                speed       REAL,
                created     INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS messages_conversation ON messages(conversation_id, id);
            CREATE TABLE IF NOT EXISTS memories (
                id          INTEGER PRIMARY KEY,
                text        TEXT NOT NULL,
                created     INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ---------- Sohbetler ----------

    public long CreateConversation()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var now = Now();
        cmd.CommandText = "INSERT INTO conversations (created, updated) VALUES ($now, $now) RETURNING id";
        cmd.Parameters.AddWithValue("$now", now);
        return (long)cmd.ExecuteScalar()!;
    }

    public bool ConversationExists(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM conversations WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is not null;
    }

    public ConversationInfo[] ListConversations()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        // Başlık yoksa ilk kullanıcı mesajı gösterilir.
        cmd.CommandText = """
            SELECT c.id,
                   COALESCE(c.title, (SELECT substr(content, 1, 60) FROM messages
                                      WHERE conversation_id = c.id AND role = 'user'
                                      ORDER BY id LIMIT 1), ''),
                   c.updated,
                   (SELECT count(*) FROM messages WHERE conversation_id = c.id)
            FROM conversations c
            WHERE EXISTS (SELECT 1 FROM messages WHERE conversation_id = c.id)
            ORDER BY c.updated DESC
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<ConversationInfo>();
        while (r.Read())
            list.Add(new ConversationInfo(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt32(3)));
        return [.. list];
    }

    public void DeleteConversation(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM conversations WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteAllConversations()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM conversations; VACUUM;";
        cmd.ExecuteNonQuery();
    }

    public void SetTitle(long id, string title)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET title = $t WHERE id = $id";
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public bool HasTitle(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT title IS NOT NULL FROM conversations WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is long v && v == 1;
    }

    public (string? Summary, long Upto) GetSummary(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT summary, summary_upto FROM conversations WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.IsDBNull(0) ? null : r.GetString(0), r.GetInt64(1)) : (null, 0);
    }

    public void SetSummary(long id, string summary, long upto)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET summary = $s, summary_upto = $u WHERE id = $id";
        cmd.Parameters.AddWithValue("$s", summary);
        cmd.Parameters.AddWithValue("$u", upto);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ---------- Mesajlar ----------

    public long AddMessage(long conversationId, string role, string content)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        var now = Now();
        cmd.CommandText = """
            INSERT INTO messages (conversation_id, role, content, created) VALUES ($c, $r, $t, $now) RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$c", conversationId);
        cmd.Parameters.AddWithValue("$r", role);
        cmd.Parameters.AddWithValue("$t", content);
        cmd.Parameters.AddWithValue("$now", now);
        var id = (long)cmd.ExecuteScalar()!;
        using var touch = c.CreateCommand();
        touch.CommandText = "UPDATE conversations SET updated = $now WHERE id = $c";
        touch.Parameters.AddWithValue("$now", now);
        touch.Parameters.AddWithValue("$c", conversationId);
        touch.ExecuteNonQuery();
        tx.Commit();
        return id;
    }

    /// <summary>Akış biterken (ya da durdurulunca) asistan mesajının son hâli.</summary>
    public void FinishMessage(long id, string content, string? thinking, string? error, double? speed)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE messages SET content = $t, thinking = $th, error = $e, speed = $s WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$t", content);
        cmd.Parameters.AddWithValue("$th", (object?)thinking ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", (object?)speed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public StoredMessage[] GetMessages(long conversationId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, role, content, thinking, error, speed FROM messages
            WHERE conversation_id = $c ORDER BY id
            """;
        cmd.Parameters.AddWithValue("$c", conversationId);
        using var r = cmd.ExecuteReader();
        var list = new List<StoredMessage>();
        while (r.Read())
        {
            list.Add(new StoredMessage(
                r.GetInt64(0),
                r.GetString(1),
                r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetDouble(5)));
        }
        return [.. list];
    }

    // ---------- Hatırlananlar ----------

    public Memory[] ListMemories()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, text, created FROM memories ORDER BY id";
        using var r = cmd.ExecuteReader();
        var list = new List<Memory>();
        while (r.Read()) list.Add(new Memory(r.GetInt64(0), r.GetString(1), r.GetInt64(2)));
        return [.. list];
    }

    public Memory AddMemory(string text)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var now = Now();
        cmd.CommandText = "INSERT INTO memories (text, created) VALUES ($t, $now) RETURNING id";
        cmd.Parameters.AddWithValue("$t", text);
        cmd.Parameters.AddWithValue("$now", now);
        return new Memory((long)cmd.ExecuteScalar()!, text, now);
    }

    public bool DeleteMemory(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM memories WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }
}
