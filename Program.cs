// Simple WebRTC Meeting App - .NET 8 backend (port of the FastAPI main.py)
//
//  1. Signaling server (WebSocket /ws/{room}/{client}) - relays SDP offers/answers + ICE candidates.
//  2. REST API - instant rooms, scheduled meetings, recordings, transcription.
//  3. Offline speech-to-text with Whisper.net (whisper.cpp). Browser sends WAV audio; no FFmpeg required.
//
// Run with:  dotnet run        (then open http://localhost:8000)

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using WebrtcMeet;

var root = AppPaths.ResolveRoot();
DotEnv.Load(Path.Combine(root, ".env"));          // real environment variables win over .env
var settings = AppSettings.Load(root);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = root });

if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")) && !args.Any(a => a.StartsWith("--urls")))
{
    var host = Environment.GetEnvironmentVariable("HOST") is { Length: > 0 } h ? h : "0.0.0.0";
    var port = Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } p ? p : "8000";
    builder.WebHost.UseUrls($"http://{host}:{port}");
}

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = null);   // recording chunks / audio clips
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = null;                      // names exactly as declared
    o.SerializerOptions.Encoder = Json.Options.Encoder;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<SqliteStore>();
builder.Services.AddSingleton<WhisperService>();
builder.Services.AddSingleton<RecordingService>();
builder.Services.AddSingleton<TranscriptStore>();
builder.Services.AddSingleton<ScheduleStore>();
builder.Services.AddSingleton<RoomHub>();
builder.Services.AddHostedService<RecordingRecoveryService>();

var app = builder.Build();

// Start loading Whisper in the background (first run downloads the model once).
var whisper = app.Services.GetRequiredService<WhisperService>();
whisper.StartLoad();
Console.WriteLine("[meet] FFmpeg is not required. Browser WAV audio is sent directly to Whisper.net.");

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

// ---------------------------------------------------------------------------
// Static frontend (no-cache, same as the Python app)
// ---------------------------------------------------------------------------
var staticDir = Path.Combine(root, "static");
var staticFiles = new PhysicalFileProvider(staticDir);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = staticFiles });
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = staticFiles,
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

IResult Error(int status, string detail) => Results.Json(new { detail }, statusCode: status);

// ---------------------------------------------------------------------------
// REST: rooms + scheduling
// ---------------------------------------------------------------------------
app.MapGet("/api/new-room", () => Results.Json(new { room = Guid.NewGuid().ToString("N")[..8] }));

app.MapPost("/api/schedule", ([FromBody] ScheduleRequest req, ScheduleStore store) =>
{
    if (string.IsNullOrEmpty(req.Title) || req.Time is null)
        return Error(422, "title and time are required");
    return Results.Json(store.Add(req), Json.Options);
});

app.MapGet("/api/schedule", (ScheduleStore store) => Results.Json(store.List(), Json.Options));

app.MapDelete("/api/schedule/{meetingId}", (string meetingId, ScheduleStore store) =>
{
    store.Remove(meetingId);
    return Results.Json(new { ok = true });
});

// ---------------------------------------------------------------------------
// REST: recording
// ---------------------------------------------------------------------------
app.MapPost("/api/recordings", (string room, string? ext, RoomHub hub, RecordingService recs) =>
{
    ext ??= "webm";
    if (!Ids.IsValid(room)) return Error(400, "bad id");
    if (ext is not ("webm" or "mp4")) return Error(400, "bad ext");
    if (hub.IsRecording(room)) return Error(409, "This meeting is already being recorded");
    var rec = recs.Create(room, ext);
    return Results.Json(new { id = rec.Id });
});

app.MapPut("/api/recordings/{recId}/chunks", async (string recId, HttpRequest request, RecordingService recs) =>
{
    if (!Ids.IsValid(recId)) return Error(400, "bad id");
    if (!recs.TryGet(recId, out var rec) || rec.Status != "recording") return Error(404, "recording not open");
    var total = await recs.AppendAsync(rec, request.Body, request.HttpContext.RequestAborted);
    return total is null ? Error(404, "recording not open") : Results.Json(new { bytes = total.Value });
});

app.MapPost("/api/recordings/{recId}/complete", async (string recId, RecordingService recs) =>
{
    if (!Ids.IsValid(recId) || !recs.TryGet(recId, out var rec)) return Error(404, "unknown recording");
    await recs.FinalizeAsync(recId);
    return Results.Json(new { ok = true, status = rec.Status });
});

app.MapGet("/api/rooms/{room}/recordings", (string room, RecordingService recs) =>
    !Ids.IsValid(room) ? Error(400, "bad id") : Results.Json(recs.ListForRoom(room)));

app.MapGet("/api/recordings/{recId}/download", async (string recId, RecordingService recs) =>
{
    if (!Ids.IsValid(recId)) return Error(400, "bad id");
    await recs.RecoverIfStaleAsync(recId, TimeSpan.FromSeconds(settings.RecordingStaleSeconds));
    var path = recs.FindFile(recId);
    var status = recs.StatusOf(recId);
    if (path is null || !File.Exists(path) || status == "recording") return Error(404, "recording is still active");

    var name = Path.GetFileName(path);
    var contentType = Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ? "video/mp4" : "video/webm";
    return Results.File(path, contentType, $"meeting-{name.Replace("__", "-")}", enableRangeProcessing: true);
});

// ---------------------------------------------------------------------------
// REST: transcription
// Every participant sends short microphone WAV clips while a recording is active.
// Whisper.net reads the WAV directly, then the transcript is persisted and broadcast.
// ---------------------------------------------------------------------------
app.MapGet("/api/whisper-status", () =>
{
    whisper.RetryIfFailed();
    return Results.Json(new { state = whisper.State, error = whisper.Error, model = whisper.ModelName });
});

app.MapPost("/api/transcribe", async (
    HttpRequest request,
    string room,
    string? name,
    string? recordingId,
    [FromQuery(Name = "start")] double? startTime,
    RoomHub hub,
    TranscriptStore store) =>
{
    if (!Ids.IsValid(room)) return Error(400, "bad id");
    if (!string.IsNullOrWhiteSpace(recordingId) && !Ids.IsValid(recordingId))
        return Error(400, "bad recording id");

    if (whisper.State != "ready")
    {
        whisper.RetryIfFailed();
        return Results.Json(new { skipped = "model " + whisper.State, error = whisper.Error });
    }

    if (Interlocked.Increment(ref TranscribeGate.Pending) > settings.MaxPendingClips)
    {
        Interlocked.Decrement(ref TranscribeGate.Pending);
        return Results.Json(new { skipped = "busy" });
    }

    try
    {
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, request.HttpContext.RequestAborted);
        var wav = ms.ToArray();

        if (wav.Length < 44)
            return Results.Json(new { skipped = "empty" });

        string text;
        try
        {
            text = await whisper.TranscribeWavAsync(wav, request.HttpContext.RequestAborted);
        }
        catch (Exception e)
        {
            var msg = e.Message.Length > 200 ? e.Message[..200] : e.Message;
            Console.WriteLine("[meet] transcription error: " + msg);
            return Results.Json(new { error = msg }, statusCode: 502);
        }

        if (string.IsNullOrWhiteSpace(text))
            return Results.Json(new { skipped = "no speech" });

        var speaker = string.IsNullOrWhiteSpace(name) ? "Guest" : name.Trim();
        if (speaker.Length > 40) speaker = speaker[..40];

        var seg = new Segment
        {
            Name = speaker,
            Text = text,
            Time = startTime ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0
        };

        store.Append(room, seg, recordingId);
        hub.Broadcast(room, Json.Serialize(new { type = "transcript", segment = seg }));

        return Results.Json(seg, Json.Options);
    }
    finally
    {
        Interlocked.Decrement(ref TranscribeGate.Pending);
    }
});

app.MapGet("/api/rooms/{room}/transcript", (string room, string? format, int? download, HttpContext ctx, TranscriptStore store) =>
{
    if (!Ids.IsValid(room)) return Error(400, "bad id");

    if (format == "text")
    {
        if (download is > 0)
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"transcript-{room}.txt\"";
        return Results.Text(store.ToText(room), "text/plain; charset=utf-8");
    }
    return Results.Json(store.Read(room), Json.Options);
});

// ---------------------------------------------------------------------------
// WebSocket signaling
// ---------------------------------------------------------------------------
app.Map("/ws/{roomId}/{clientId}", (HttpContext ctx, string roomId, string clientId, RoomHub hub, RecordingService recs) =>
    Signaling.HandleAsync(ctx, roomId, clientId, hub, recs));

app.Run();

internal static class TranscribeGate
{
    public static int Pending;
}
