using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WebrtcMeet;

public static class Json
{
    /// <summary>Property names are written exactly as declared; non-ASCII (Hindi, Marathi...) stays readable.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}

public static partial class Ids
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SafeIdRegex();

    public static bool IsValid(string? value) => value is not null && SafeIdRegex().IsMatch(value);
}

public sealed class ScheduleRequest
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("time")] public string? Time { get; set; }          // ISO string from <input type="datetime-local">
    [JsonPropertyName("host_name")] public string HostName { get; set; } = "";
}

public sealed class Meeting
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("time")] public string Time { get; set; } = "";
    [JsonPropertyName("host_name")] public string HostName { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
}

public sealed class Segment
{
    [JsonPropertyName("name")] public string Name { get; set; } = "Guest";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("time")] public double Time { get; set; }           // unix seconds
}

public sealed class RoomFlags
{
    public bool Recording;
    public string? RecordingId;
    public string? RecOwner;
    public bool Transcription;
}
