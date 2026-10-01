using System.Text;

namespace WebrtcMeet;

public static class DotEnv
{
    public static void Load(string path)
    {
        if (!File.Exists(path)) return;

        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var i = line.IndexOf('=');
            if (i < 0) continue;

            var key = line[..i].Trim();
            var value = line[(i + 1)..].Trim().Trim('"').Trim('\'');

            if (key.Length > 0 && value.Length > 0 &&
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}

public static class AppPaths
{
    public static string ResolveRoot()
    {
        foreach (var candidate in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            if (Directory.Exists(Path.Combine(candidate, "static")))
                return Path.GetFullPath(candidate);

        return Path.GetFullPath(AppContext.BaseDirectory);
    }
}

public sealed class AppSettings
{
    public string Root { get; init; } = "";
    public string DataDir { get; init; } = "";
    public string RecDir { get; init; } = "";
    public string TrDir { get; init; } = "";
    public string ModelDir { get; init; } = "";

    public string WhisperModel { get; init; } = "small";
    public string? WhisperLanguage { get; init; }
    public int WhisperThreads { get; init; }
    public int MaxPendingClips { get; init; } = 4;
    public int RecordingStaleSeconds { get; init; } = 30;

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Env(name), out var value) ? value : fallback;

    public static AppSettings Load(string root)
    {
        var data = Env("DATA_DIR") is { } d
            ? Path.GetFullPath(d, root)
            : Path.Combine(root, "data");

        var settings = new AppSettings
        {
            Root = root,
            DataDir = data,
            RecDir = Path.Combine(data, "recordings"),
            TrDir = Path.Combine(data, "transcripts"),
            ModelDir = Path.Combine(data, "models"),
            WhisperModel = Env("WHISPER_MODEL") ?? "small",
            WhisperLanguage = Env("WHISPER_LANGUAGE"),
            WhisperThreads = EnvInt("WHISPER_CPU_THREADS", 0),
            MaxPendingClips = EnvInt("MAX_PENDING_CLIPS", 4),
            RecordingStaleSeconds = EnvInt("RECORDING_STALE_SECONDS", 30)
        };

        Directory.CreateDirectory(settings.RecDir);
        Directory.CreateDirectory(settings.TrDir);
        Directory.CreateDirectory(settings.ModelDir);

        return settings;
    }
}
