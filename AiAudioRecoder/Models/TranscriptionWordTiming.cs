using System.Text.Json.Serialization;

namespace AiAudioRecoder.Models;

public class TranscriptionWordTiming
{
    [JsonPropertyName("w")]
    public string Word { get; set; } = string.Empty;

    [JsonPropertyName("s")]
    public double Start { get; set; }

    [JsonPropertyName("e")]
    public double End { get; set; }
}
