using Xunit;
using WhisperDesk.Models;

namespace WhisperDesk.Tests;

public class AppStatusTests
{
    [Theory]
    [InlineData(AppStatus.Idle, "Ready")]
    [InlineData(AppStatus.Listening, "\U0001f3a4 Listening...")]
    [InlineData(AppStatus.Transcribing, "\U0001f4dd Transcribing...")]
    [InlineData(AppStatus.Cleaning, "\u2728 Cleaning up...")]
    [InlineData(AppStatus.Ready, "\u2705 Done - ready to paste")]
    [InlineData(AppStatus.Error, "\u274c Error")]
    public void ToDisplayString_ReturnsExpectedText(AppStatus status, string expected)
    {
        Assert.Equal(expected, status.ToDisplayString());
    }

    [Fact]
    public void WhisperDeskSettings_DefaultValues()
    {
        var settings = new WhisperDeskSettings();

        // Azure OpenAI defaults
        Assert.Equal("gpt-5-mini", settings.AzureOpenAI.ChatDeployment);

        // Azure Speech defaults
        Assert.Equal("zh-CN", settings.AzureSpeech.Language);

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
