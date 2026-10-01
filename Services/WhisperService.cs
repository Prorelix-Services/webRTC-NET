using System.Text.RegularExpressions;
using Whisper.net;
using Whisper.net.Ggml;

namespace WebrtcMeet;

public sealed partial class WhisperService : IDisposable
{
    private readonly AppSettings _cfg;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly SemaphoreSlim _inferLock = new(1, 1);
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private volatile string _state = "loading";
    private volatile string _error = "";
    private long _lastTryMs;

    public WhisperService(AppSettings cfg) => _cfg = cfg;
    public string State => _state;
    public string Error => _error;
    public string ModelName => _cfg.WhisperModel;
    public double SecondsSinceLastTry => (Environment.TickCount64 - Interlocked.Read(ref _lastTryMs)) / 1000.0;

    [GeneratedRegex(@"^\s*[\[\(][^\]\)]*[\]\)]\s*$")]
    private static partial Regex NonSpeechMarker();

    public void StartLoad() => _ = Task.Run(async () =>
    {
        try { await GetProcessorAsync(); } catch { }
    });

    public void RetryIfFailed()
    {
        if (_state == "error" && SecondsSinceLastTry > 30) StartLoad();
    }

    private async Task<WhisperProcessor> GetProcessorAsync()
    {
        await _loadLock.WaitAsync();
        try
        {
            if (_processor is not null)
            {
                _state = "ready";
                return _processor;
            }

            _state = "loading";
            Interlocked.Exchange(ref _lastTryMs, Environment.TickCount64);

            try
            {
                var modelPath = await EnsureModelFileAsync();
                _factory = WhisperFactory.FromPath(modelPath);

                var builder = _factory.CreateBuilder()
                    .WithLanguage(string.IsNullOrEmpty(_cfg.WhisperLanguage) ? "auto" : _cfg.WhisperLanguage)
                    .WithNoContext();

                if (_cfg.WhisperThreads > 0)
                    builder = builder.WithThreads(_cfg.WhisperThreads);

                _processor = builder.Build();
                _state = "ready";
                _error = "";
                Console.WriteLine("[meet] Whisper ready");
            }
            catch (Exception e)
            {
                _state = "error";
                _error = e.Message.Length > 300 ? e.Message[..300] : e.Message;
                Console.WriteLine("[meet] Whisper failed: " + e.Message);
                throw;
            }

            return _processor;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<string> EnsureModelFileAsync()
    {
        var configured = _cfg.WhisperModel;
        if (File.Exists(configured)) return configured;

        var key = configured.Replace("-", "").Replace(".", "").Replace("_", "");
        if (key.Equals("large", StringComparison.OrdinalIgnoreCase)) key = "LargeV3";

        if (!Enum.TryParse<GgmlType>(key, true, out var type))
            throw new InvalidOperationException(
                $"Unknown WHISPER_MODEL '{configured}'. Use tiny, base, small, medium, large-v3 or a local model path.");

        var target = Path.Combine(_cfg.ModelDir, $"ggml-{configured.ToLowerInvariant()}.bin");
        if (File.Exists(target) && new FileInfo(target).Length > 0) return target;

        Console.WriteLine($"[meet] downloading Whisper model '{configured}'...");
        var tmp = target + ".part";

        await using var src = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(type);
        await using (var dst = File.Create(tmp))
        {
            await src.CopyToAsync(dst);
            await dst.FlushAsync();
        }
        File.Move(tmp, target, true);

        return target;
    }

    public async Task<string> TranscribeWavAsync(byte[] wav, CancellationToken ct = default)
    {
        if (wav.Length < 44) return "";

        var processor = await GetProcessorAsync();
        await _inferLock.WaitAsync(ct);

        try
        {
            using var stream = new MemoryStream(wav, writable: false);
            var parts = new List<string>();

            await foreach (var segment in processor.ProcessAsync(stream, ct))
            {
                var text = segment.Text.Trim();
                if (text.Length == 0 || NonSpeechMarker().IsMatch(text)) continue;
                parts.Add(text);
            }

            return string.Join(" ", parts).Trim();
        }
        finally
        {
            _inferLock.Release();
        }
    }

    public void Dispose()
    {
        _processor?.Dispose();
        _factory?.Dispose();
        _loadLock.Dispose();
        _inferLock.Dispose();
    }
}
