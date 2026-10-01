using Microsoft.Data.Sqlite;

namespace WebrtcMeet;

public sealed class SqliteStore
{
    private readonly string _connectionString;

    public SqliteStore(AppSettings settings)
    {
        var db = Path.Combine(settings.DataDir, "meetings.db");
        Directory.CreateDirectory(settings.DataDir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = db,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        using var c = Open();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
        PRAGMA journal_mode=WAL;

        CREATE TABLE IF NOT EXISTS meetings (
            id TEXT PRIMARY KEY,
            title TEXT NOT NULL,
            time TEXT NOT NULL,
            host_name TEXT NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS recordings (
            id TEXT PRIMARY KEY,
            room TEXT NOT NULL,
            path TEXT NOT NULL,
            ext TEXT NOT NULL,
            status TEXT NOT NULL,
            bytes INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL,
            last_chunk_at TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_recordings_room ON recordings(room);
        CREATE INDEX IF NOT EXISTS idx_recordings_status ON recordings(status);

        CREATE TABLE IF NOT EXISTS transcripts (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            room TEXT NOT NULL,
            recording_id TEXT,
            name TEXT NOT NULL,
            text TEXT NOT NULL,
            time REAL NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_transcripts_room ON transcripts(room, time);
        """;
        cmd.ExecuteNonQuery();
    }

    public SqliteConnection Open() => new(_connectionString);

    public T WithConnection<T>(Func<SqliteConnection, T> action)
    {
        using var c = Open();
        c.Open();
        return action(c);
    }

    public void Execute(string sql, Action<SqliteCommand>? configure = null)
    {
        using var c = Open();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        configure?.Invoke(cmd);
        cmd.ExecuteNonQuery();
    }
}
