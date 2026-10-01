using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace WebrtcMeet;

public sealed class RecordingInfo
{
    public required string Id { get; init; }
    public required string Room { get; init; }
    public required string Path { get; init; }
    public required string Ext { get; init; }
    public string Status { get; set; } = "recording";
    public long Bytes { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastChunkAt { get; set; }
    public readonly SemaphoreSlim Gate = new(1, 1);
}

public sealed class RecordingService
{
    private readonly AppSettings _cfg;
    private readonly SqliteStore _db;
    private readonly ConcurrentDictionary<string, RecordingInfo> _cache = new();

    public RecordingService(AppSettings cfg, SqliteStore db)
    {
        _cfg = cfg;
        _db = db;
    }

    public bool Contains(string id) =>
        _db.WithConnection(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM recordings WHERE id=$id LIMIT 1";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() is not null;
        });

    public bool TryGet(string id, out RecordingInfo rec)
    {
        if (_cache.TryGetValue(id, out rec!)) return true;

        rec = _db.WithConnection(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT id, room, path, ext, status, bytes, created_at, last_chunk_at
                FROM recordings WHERE id=$id
                """;
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null!;
            return FromReader(r);
        });

        if (rec is not null)
        {
            _cache[id] = rec;
            return true;
        }

        rec = null!;
        return false;
    }

    public RecordingInfo Create(string room, string ext)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var path = Path.Combine(_cfg.RecDir, $"{room}__{id}.{ext}");
        var now = DateTime.UtcNow;

        File.WriteAllBytes(path, Array.Empty<byte>());

        var rec = new RecordingInfo
        {
            Id = id, Room = room, Path = path, Ext = ext,
            CreatedAt = now, LastChunkAt = now
        };

        _db.WithConnection(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO recordings(id,room,path,ext,status,bytes,created_at,last_chunk_at)
                VALUES($id,$room,$path,$ext,'recording',0,$created,$last)
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$room", room);
            cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$ext", ext);
            cmd.Parameters.AddWithValue("$created", now.ToString("O"));
            cmd.Parameters.AddWithValue("$last", now.ToString("O"));
            cmd.ExecuteNonQuery();
            return 0;
        });

        _cache[id] = rec;
        return rec;
    }

    public async Task<long?> AppendAsync(RecordingInfo rec, Stream body, CancellationToken ct)
    {
        await rec.Gate.WaitAsync(ct);
        try
        {
            if (rec.Status != "recording") return null;

            await using var fs = new FileStream(
                rec.Path, FileMode.Append, FileAccess.Write, FileShare.Read,
                128 * 1024, useAsync: true);

            var before = fs.Length;
            await body.CopyToAsync(fs, ct);
            await fs.FlushAsync(ct);

            var added = fs.Length - before;
            rec.Bytes += added;
            rec.LastChunkAt = DateTime.UtcNow;

            _db.WithConnection(c =>
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    UPDATE recordings
                    SET bytes=$bytes, last_chunk_at=$last
                    WHERE id=$id AND status='recording'
                    """;
                cmd.Parameters.AddWithValue("$bytes", rec.Bytes);
                cmd.Parameters.AddWithValue("$last", rec.LastChunkAt.ToString("O"));
                cmd.Parameters.AddWithValue("$id", rec.Id);
                cmd.ExecuteNonQuery();
                return 0;
            });

            return rec.Bytes;
        }
        finally
        {
            rec.Gate.Release();
        }
    }

    public async Task FinalizeAsync(string id)
    {
        if (!TryGet(id, out var rec)) return;

        await rec.Gate.WaitAsync();
        try
        {
            if (rec.Status != "recording") return;

            rec.Status = "ready";
            _db.WithConnection(c =>
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "UPDATE recordings SET status='ready', bytes=$bytes WHERE id=$id";
                cmd.Parameters.AddWithValue("$bytes", new FileInfo(rec.Path).Length);
                cmd.Parameters.AddWithValue("$id", rec.Id);
                cmd.ExecuteNonQuery();
                return 0;
            });
        }
        finally
        {
            rec.Gate.Release();
        }
    }

    public async Task RecoverIfStaleAsync(string id, TimeSpan staleAfter)
    {
        if (!TryGet(id, out var rec) || rec.Status != "recording") return;
        if (DateTime.UtcNow - rec.LastChunkAt >= staleAfter)
            await FinalizeAsync(id);
    }

    public async Task RecoverStaleAsync(TimeSpan staleAfter)
    {
        var ids = _db.WithConnection(c =>
        {
            var result = new List<string>();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT id FROM recordings
                WHERE status='recording' AND last_chunk_at < $cutoff
                """;
            cmd.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.Subtract(staleAfter).ToString("O"));
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(r.GetString(0));
            return result;
        });

        foreach (var id in ids)
            await FinalizeAsync(id);
    }

    public string? FindFile(string id)
    {
        if (TryGet(id, out var rec) && File.Exists(rec.Path)) return rec.Path;
        return Directory.EnumerateFiles(_cfg.RecDir, $"*__{id}.*").FirstOrDefault();
    }

    public string StatusOf(string id) =>
        TryGet(id, out var rec) ? rec.Status : "ready";

    public List<object> ListForRoom(string room)
    {
        return _db.WithConnection(c =>
        {
            var items = new List<object>();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT id, path, ext, status, bytes, created_at
                FROM recordings WHERE room=$room
                ORDER BY created_at DESC
                """;
            cmd.Parameters.AddWithValue("$room", room);

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var path = r.GetString(1);
                var bytes = File.Exists(path) ? new FileInfo(path).Length : r.GetInt64(4);
                items.Add(new
                {
                    id = r.GetString(0),
                    status = r.GetString(3),
                    size = bytes,
                    created = DateTime.Parse(r.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ss"),
                    url = $"/api/recordings/{r.GetString(0)}/download"
                });
            }
            return items;
        });
    }

    private static RecordingInfo FromReader(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Room = r.GetString(1),
        Path = r.GetString(2),
        Ext = r.GetString(3),
        Status = r.GetString(4),
        Bytes = r.GetInt64(5),
        CreatedAt = DateTime.Parse(r.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
        LastChunkAt = DateTime.Parse(r.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind)
    };
}

public sealed class TranscriptStore
{
    private readonly SqliteStore _db;
    private readonly string _exportDir;

    public TranscriptStore(SqliteStore db, AppSettings settings)
    {
        _db = db;
        _exportDir = settings.TrDir;
        Directory.CreateDirectory(_exportDir);
    }

    public void Append(string room, Segment seg, string? recordingId = null)
    {
        _db.WithConnection(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO transcripts(room,recording_id,name,text,time)
                VALUES($room,$recording,$name,$text,$time)
                """;
            cmd.Parameters.AddWithValue("$room", room);
            cmd.Parameters.AddWithValue("$recording", (object?)recordingId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$name", seg.Name);
            cmd.Parameters.AddWithValue("$text", seg.Text);
            cmd.Parameters.AddWithValue("$time", seg.Time);
            cmd.ExecuteNonQuery();
            return 0;
        });

        // Keep simple human-readable exports too. SQLite is the source of truth.
        ExportFiles(room);
    }

    private void ExportFiles(string room)
    {
        Directory.CreateDirectory(_exportDir);
        var segments = Read(room);
        var json = System.Text.Json.JsonSerializer.Serialize(segments, Json.Options);
        File.WriteAllText(Path.Combine(_exportDir, room + ".json"), json, new System.Text.UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_exportDir, room + ".txt"), ToText(room), new System.Text.UTF8Encoding(false));
    }

    public List<Segment> Read(string room) =>
        _db.WithConnection(c =>
        {
            var result = new List<Segment>();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT name,text,time FROM transcripts WHERE room=$room ORDER BY time";
            cmd.Parameters.AddWithValue("$room", room);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new Segment { Name = r.GetString(0), Text = r.GetString(1), Time = r.GetDouble(2) });
            return result;
        });

    public string ToText(string room) =>
        string.Join(Environment.NewLine, Read(room).Select(s =>
            $"[{DateTimeOffset.FromUnixTimeMilliseconds((long)(s.Time * 1000)).ToLocalTime():HH:mm:ss}] {s.Name}: {s.Text}"));
}

public sealed class ScheduleStore
{
    private readonly SqliteStore _db;
    public ScheduleStore(SqliteStore db) => _db = db;

    public Meeting Add(ScheduleRequest req)
    {
        var m = new Meeting
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Title = req.Title ?? "",
            Time = req.Time ?? "",
            HostName = req.HostName ?? "",
            CreatedAt = DateTime.UtcNow.ToString("O")
        };

        _db.WithConnection(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO meetings(id,title,time,host_name,created_at)
                VALUES($id,$title,$time,$host,$created)
                """;
            cmd.Parameters.AddWithValue("$id", m.Id);
            cmd.Parameters.AddWithValue("$title", m.Title);
            cmd.Parameters.AddWithValue("$time", m.Time);
            cmd.Parameters.AddWithValue("$host", m.HostName);
            cmd.Parameters.AddWithValue("$created", m.CreatedAt);
            cmd.ExecuteNonQuery();
            return 0;
        });

        return m;
    }

    public List<Meeting> List() =>
        _db.WithConnection(c =>
        {
            var result = new List<Meeting>();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id,title,time,host_name,created_at FROM meetings ORDER BY time";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new Meeting
                {
                    Id = r.GetString(0), Title = r.GetString(1), Time = r.GetString(2),
                    HostName = r.GetString(3), CreatedAt = r.GetString(4)
                });
            return result;
        });

    public void Remove(string id)
    {
        _db.Execute("DELETE FROM meetings WHERE id=$id", cmd => cmd.Parameters.AddWithValue("$id", id));
    }
}

public sealed class RecordingRecoveryService : BackgroundService
{
    private readonly RecordingService _recordings;
    public RecordingRecoveryService(RecordingService recordings) => _recordings = recordings;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _recordings.RecoverStaleAsync(TimeSpan.FromSeconds(30));

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await _recordings.RecoverStaleAsync(TimeSpan.FromSeconds(30)); }
            catch (Exception e) { Console.WriteLine("[meet] recovery: " + e.Message); }
        }
    }
}
