using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace WebrtcMeet;

/// <summary>
/// One connected browser. WebSocket.SendAsync must not be called concurrently, so every outgoing message
/// goes through a queue that a single loop drains. Send() never blocks and never throws.
/// </summary>
public sealed class Peer
{
    private readonly WebSocket _ws;
    private readonly Channel<string> _out = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private volatile bool _dead;
    private Task _loop = Task.CompletedTask;

    public Peer(WebSocket ws) => _ws = ws;

    public void Start() => _loop = Task.Run(RunAsync);

    public void Send(string json)
    {
        if (!_dead) _out.Writer.TryWrite(json);
    }

    public void Complete() => _out.Writer.TryComplete();

    public async Task DrainAsync()
    {
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var msg in _out.Reader.ReadAllAsync())
            {
                if (_ws.State != WebSocketState.Open) break;
                await _ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        catch { /* socket gone: the receive loop notices and cleans up */ }
        finally { _dead = true; }
    }
}

/// <summary>Rooms (room id -> client id -> peer) plus the per-room recording / captions flags.</summary>
public sealed class RoomHub
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, Peer>> _rooms = new();
    private readonly Dictionary<string, RoomFlags> _flags = new();

    private RoomFlags FlagsLocked(string roomId)
    {
        if (!_flags.TryGetValue(roomId, out var f)) _flags[roomId] = f = new RoomFlags();
        return f;
    }

    private string StatusJsonLocked(string roomId)
    {
        var f = FlagsLocked(roomId);
        return Json.Serialize(new
        {
            type = "status",
            recording = f.Recording,
            recordingId = f.RecordingId,
            transcription = f.Transcription,
        });
    }

    public bool IsRecording(string roomId)
    {
        lock (_gate) return _flags.TryGetValue(roomId, out var f) && f.Recording;
    }

    public bool IsTranscriptionOn(string roomId)
    {
        lock (_gate) return _flags.TryGetValue(roomId, out var f) && f.Transcription;
    }

    public string StatusJson(string roomId)
    {
        lock (_gate) return StatusJsonLocked(roomId);
    }

    public bool TryGetPeer(string roomId, string clientId, out Peer peer)
    {
        lock (_gate)
        {
            if (_rooms.TryGetValue(roomId, out var room) && room.TryGetValue(clientId, out var p))
            {
                peer = p;
                return true;
            }
        }
        peer = null!;
        return false;
    }

    public void Broadcast(string roomId, string json, string? exclude = null)
    {
        List<Peer> targets;
        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomId, out var room)) return;
            targets = room.Where(kv => kv.Key != exclude).Select(kv => kv.Value).ToList();
        }
        foreach (var p in targets) p.Send(json);
    }

    public void BroadcastStatus(string roomId) => Broadcast(roomId, StatusJson(roomId));

    /// <summary>
    /// 1) tell the newcomer who is here + current recording/caption state, 2) register it, 3) tell everyone else.
    /// Steps 1-2 happen under one lock, so a second joiner can never be missed.
    /// </summary>
    public void Join(string roomId, string clientId, Peer peer)
    {
        List<Peer> others;
        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomId, out var room)) _rooms[roomId] = room = new();

            peer.Send(Json.Serialize(new { type = "existing-peers", peers = room.Keys.ToArray() }));
            peer.Send(StatusJsonLocked(roomId));

            room[clientId] = peer;
            others = room.Where(kv => kv.Key != clientId).Select(kv => kv.Value).ToList();
        }

        var msg = Json.Serialize(new { type = "new-peer", peer = clientId });
        foreach (var o in others) o.Send(msg);
    }

    public void Leave(string roomId, string clientId, Peer peer, RecordingService recs)
    {
        List<Peer> remaining;
        string? finalizeId = null;
        bool empty;

        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomId, out var room)) return;
            // only remove if this socket is still the registered one (a quick reconnect with the same id must survive)
            if (!room.TryGetValue(clientId, out var current) || !ReferenceEquals(current, peer)) return;
            room.Remove(clientId);

            var f = FlagsLocked(roomId);
            if (f.RecOwner == clientId)      // the browser that was recording left: close the file
            {
                finalizeId = f.RecordingId;
                f.Recording = false;
                f.RecordingId = null;
                f.RecOwner = null;
            }

            remaining = room.Values.ToList();
            empty = room.Count == 0;
            if (empty)
            {
                _rooms.Remove(roomId);
                // Keep room transcription preference for reconnects.
            }
        }

        if (finalizeId is not null)
            _ = Task.Run(() => recs.FinalizeAsync(finalizeId));

        var left = Json.Serialize(new { type = "peer-left", peer = clientId });
        foreach (var p in remaining) p.Send(left);

        if (!empty)
        {
            var status = StatusJson(roomId);
            foreach (var p in remaining) p.Send(status);
        }
    }

    public void SetTranscription(string roomId, bool active)
    {
        lock (_gate) FlagsLocked(roomId).Transcription = active;
        BroadcastStatus(roomId);
    }

    public void SetRecording(string roomId, string clientId, bool active, string recordingId, RecordingService recs)
    {
        lock (_gate)
        {
            var f = FlagsLocked(roomId);
            if (active)
            {
                if (!f.Recording && Ids.IsValid(recordingId) && recs.Contains(recordingId))
                {
                    f.Recording = true;
                    f.RecordingId = recordingId;
                    f.RecOwner = clientId;
                }
            }
            else
            {
                f.Recording = false;       // the recording browser sees this and finishes uploading
                f.RecordingId = null;
            }
        }
        BroadcastStatus(roomId);
    }
}

public static class Signaling
{
    private const int MaxMessageBytes = 1024 * 1024;

    private static string? StringProp(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool BoolProp(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>
    /// /ws/{roomId}/{clientId}: the browsers decide who offers; this server only relays offer / answer / ice-candidate
    /// to the "target" client, and handles the room-wide recording / caption switches.
    /// </summary>
    public static async Task HandleAsync(HttpContext ctx, string roomId, string clientId, RoomHub hub, RecordingService recs)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var ct = ctx.RequestAborted;
        var peer = new Peer(ws);
        peer.Start();
        hub.Join(roomId, clientId, peer);

        try
        {
            var buffer = new byte[16 * 1024];
            using var ms = new MemoryStream();
            var open = true;

            while (open && !ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buffer, ct);
                    if (r.MessageType == WebSocketMessageType.Close) { open = false; break; }
                    ms.Write(buffer, 0, r.Count);
                    if (ms.Length > MaxMessageBytes) { open = false; break; }
                } while (!r.EndOfMessage);

                if (!open || r.MessageType != WebSocketMessageType.Text) continue;

                JsonObject? message;
                try { message = JsonNode.Parse(ms.ToArray()) as JsonObject; }
                catch { continue; }                     // ignore malformed JSON
                if (message is null) continue;

                switch (StringProp(message, "type"))
                {
                    // --- recording / transcription switches (visible to everyone in the room) ---
                    case "set-transcription":
                        hub.SetTranscription(roomId, BoolProp(message, "active"));
                        continue;
                    case "set-recording":
                        hub.SetRecording(roomId, clientId, BoolProp(message, "active"),
                            StringProp(message, "recordingId") ?? "", recs);
                        continue;
                    case "get-status":
                        peer.Send(hub.StatusJson(roomId));
                        continue;
                    case "hello":
                        continue;
                }

                // --- relay offer / answer / ice-candidate to the target peer ---
                var target = StringProp(message, "target");
                message["sender"] = clientId;
                if (target is not null && hub.TryGetPeer(roomId, target, out var targetPeer))
                    targetPeer.Send(message.ToJsonString(Json.Options));
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            hub.Leave(roomId, clientId, peer, recs);
            peer.Complete();
            await peer.DrainAsync();

            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closeCts.Token);
                }
                catch { }
            }
        }
    }
}
