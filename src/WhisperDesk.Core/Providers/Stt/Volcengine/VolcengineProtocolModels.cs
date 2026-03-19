using System.Text.Json.Serialization;

namespace WhisperDesk.Core.Providers.Stt.Volcengine;

// These classes are internal so the System.Text.Json source generator can access them.
// They are only used for Volcengine WebSocket protocol serialization.

internal class VolcengineRequest
{
    [JsonPropertyName("user")]
    public VolcengineUserInfo User { get; set; } = new();

    [JsonPropertyName("audio")]
    public VolcengineAudioInfo Audio { get; set; } = new();

    [JsonPropertyName("request")]
    public VolcengineRequestInfo Request { get; set; } = new();
}

internal class VolcengineUserInfo
{
    [JsonPropertyName("uid")]
    public string Uid { get; set; } = "default";
}

internal class VolcengineAudioInfo
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = "pcm";

    [JsonPropertyName("codec")]
    public string Codec { get; set; } = "raw";

    [JsonPropertyName("rate")]
    public int Rate { get; set; } = 16000;

    [JsonPropertyName("bits")]
    public int Bits { get; set; } = 16;

    [JsonPropertyName("channel")]
    public int Channel { get; set; } = 1;
}

internal class VolcengineRequestInfo
{
    [JsonPropertyName("model_name")]
    public string ModelName { get; set; } = "bigmodel";

    [JsonPropertyName("enable_itn")]
    public bool EnableItn { get; set; } = true;

    [JsonPropertyName("enable_punc")]
    public bool EnablePunc { get; set; } = true;

    [JsonPropertyName("result_type")]
    public string ResultType { get; set; } = "single";

    [JsonPropertyName("show_utterances")]
    public bool ShowUtterances { get; set; } = true;
}

internal class VolcengineResponse
{
    // Actual server response structure: {"audio_info":{...},"result":{...}}
    // Some endpoints may wrap in payload_msg -- support both.

    [JsonPropertyName("audio_info")]
    public VolcengineAudioInfoResponse? AudioInfo { get; set; }

    [JsonPropertyName("result")]
    public VolcengineRecognitionResult? Result { get; set; }

    [JsonPropertyName("payload_msg")]
    public VolcenginePayloadMessage? PayloadMsg { get; set; }

    [JsonPropertyName("is_last_package")]
    public bool IsLastPackage { get; set; }
}

internal class VolcengineAudioInfoResponse
{
    [JsonPropertyName("duration")]
    public int Duration { get; set; }
}

internal class VolcenginePayloadMessage
{
    [JsonPropertyName("is_end")]
    public bool IsEnd { get; set; }

    [JsonPropertyName("result")]
    public VolcengineRecognitionResult? Result { get; set; }
}

internal class VolcengineRecognitionResult
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("utterances")]
    public List<VolcengineUtterance>? Utterances { get; set; }
}

internal class VolcengineUtterance
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("start_time")]
    public int StartTime { get; set; }

    [JsonPropertyName("end_time")]
    public int EndTime { get; set; }

    [JsonPropertyName("definite")]
    public bool Definite { get; set; }
}

/// <summary>
/// Source-generated JSON serialization context for Volcengine protocol models.
/// Avoids reflection-based serialization for AOT compatibility and performance.
/// </summary>
[JsonSerializable(typeof(VolcengineRequest))]
[JsonSerializable(typeof(VolcengineResponse))]
internal partial class VolcengineJsonContext : JsonSerializerContext;
