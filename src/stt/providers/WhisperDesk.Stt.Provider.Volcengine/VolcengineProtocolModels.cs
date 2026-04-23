using System.Text.Json.Serialization;

namespace WhisperDesk.Stt.Provider.Volcengine;

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

    [JsonPropertyName("corpus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VolcengineCorpus? Corpus { get; set; }
}

internal class VolcengineCorpus
{
    [JsonPropertyName("context")]
    public string Context { get; set; } = "";
}

internal class VolcengineContext
{
    [JsonPropertyName("hotwords")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<VolcengineHotword>? Hotwords { get; set; }

    [JsonPropertyName("context_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContextType { get; set; }

    [JsonPropertyName("context_data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<VolcengineContextItem>? ContextData { get; set; }
}

internal class VolcengineHotword
{
    [JsonPropertyName("word")]
    public string Word { get; set; } = "";
}

internal class VolcengineContextItem
{
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; set; }
}

internal class VolcengineResponse
{
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

[JsonSerializable(typeof(VolcengineRequest))]
[JsonSerializable(typeof(VolcengineResponse))]
[JsonSerializable(typeof(VolcengineContext))]
internal partial class VolcengineJsonContext : JsonSerializerContext;
