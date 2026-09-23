using Xunit;
using WhisperDesk.Models;

namespace WhisperDesk.Tests;

public class AppStatusTests
{
    [Theory]
    [InlineData(AppStatus.Idle, "准备就绪")]
    [InlineData(AppStatus.Listening, "正在录音")]
    [InlineData(AppStatus.Transcribing, "正在转写")]
    [InlineData(AppStatus.Cleaning, "正在整理")]
    [InlineData(AppStatus.Ready, "文字已就绪")]
    [InlineData(AppStatus.Error, "处理未完成")]
    public void ToDisplayString_ReturnsExpectedText(AppStatus status, string expected)
    {
        Assert.Equal(expected, status.ToDisplayString());
    }

    [Theory]
    [InlineData(AppStatus.Idle)]
    [InlineData(AppStatus.Listening)]
    [InlineData(AppStatus.Transcribing)]
    [InlineData(AppStatus.Cleaning)]
    [InlineData(AppStatus.Ready)]
    [InlineData(AppStatus.Error)]
    public void ToTrayTooltip_UsesTheSameStatusLanguage(AppStatus status)
    {
        Assert.Equal($"WhisperDesk - {status.ToDisplayString()}", status.ToTrayTooltip());
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
        Assert.Equal("RightAlt", settings.Hotkeys.Transcribe);
        Assert.Equal("RightAlt+Shift", settings.Hotkeys.Instruct);
        Assert.Equal(300, settings.Hotkeys.DraftCorrectionHoldMs);

        // Audio defaults
        Assert.Equal(16000, settings.Audio.SampleRate);

        // Transcription defaults - provider selection
        Assert.Equal("AzureSpeech", settings.Transcription.SpeechProvider);
        Assert.Equal("AzureOpenAI", settings.Transcription.CleanupProvider);
        Assert.Equal("zh", settings.Transcription.Language);
    }
}
