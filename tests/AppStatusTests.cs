using Xunit;
using WhisperDesk.Models;

namespace WhisperDesk.Tests;

public class AppStatusTests
{
    [Theory]
    [InlineData(AppStatus.Idle, "Ready")]
    [InlineData(AppStatus.Listening, "🎤 Listening...")]
    [InlineData(AppStatus.Transcribing, "📝 Transcribing...")]
    [InlineData(AppStatus.Cleaning, "✨ Cleaning up...")]
    [InlineData(AppStatus.Ready, "✅ Done - ready to paste")]
    [InlineData(AppStatus.Error, "❌ Error")]
    public void ToDisplayString_ReturnsExpectedText(AppStatus status, string expected)
    {
        Assert.Equal(expected, status.ToDisplayString());
    }

    [Fact]
    public void TranscriptionResult_DefaultValues()
    {
        var result = new TranscriptionResult
        {
            RawText = "test raw",
            CleanedText = "test clean"
        };

        Assert.Equal("test raw", result.RawText);
        Assert.Equal("test clean", result.CleanedText);
        Assert.Equal("zh", result.Language);
        Assert.Null(result.SourceFile);
    }

    [Fact]
    public void WhisperDeskSettings_DefaultValues()
    {
        var settings = new WhisperDeskSettings();

        // Azure OpenAI defaults
        Assert.Equal("gpt-5-mini", settings.AzureOpenAI.ChatDeployment);

        // Azure Speech defaults
        Assert.Equal("zh-CN", settings.AzureSpeech.Language);
        Assert.Equal("japaneast", settings.AzureSpeech.Region);

        // Hotkey defaults
        Assert.Equal("F9", settings.Hotkeys.PushToTalk);
        Assert.Equal("Ctrl+Shift+V", settings.Hotkeys.PasteTranscription);

        // Audio defaults
        Assert.Equal(16000, settings.Audio.SampleRate);

        // Transcription defaults - provider selection
        Assert.Equal("AzureSpeech", settings.Transcription.SpeechProvider);
        Assert.Equal("AzureOpenAI", settings.Transcription.CleanupProvider);
        Assert.Equal("zh", settings.Transcription.Language);
    }
}
