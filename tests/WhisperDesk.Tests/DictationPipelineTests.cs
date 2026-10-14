using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WhisperDesk.Core.Configuration;
using WhisperDesk.Core.Contract;
using WhisperDesk.Core.Pipeline;
using WhisperDesk.Core.Services;
using WhisperDesk.Core.Stages.PostProcessing;
using WhisperDesk.Llm.Contract;
using WhisperDesk.Stt.Contract;
using WhisperDesk.Transcript.Contract;
using Xunit;

namespace WhisperDesk.Tests;

public class DictationPipelineTests
{
    [Fact]
    public async Task OrdinaryDictationReturnsExactSttWithoutPostProcessingOrLlmCalls()
    {
        const string transcript = "嗯，那个不是靠，是 Core C O R E。你再 review 这个。";
        var stt = new Mock<IStreamingSttProvider>();
        stt.Setup(s => s.EndSessionAsync()).ReturnsAsync(transcript);
        var llm = new Mock<ILlmProvider>(MockBehavior.Strict);
        var stage = new Mock<IPostProcessingStage>(MockBehavior.Strict);
        stage.SetupGet(s => s.Order).Returns(1);
        var history = new Mock<ITranscriptionHistoryService>();
        TranscriptionHistoryEntry? saved = null;
        history.Setup(h => h.WriteEntryAsync(It.IsAny<TranscriptionHistoryEntry>()))
            .Callback<TranscriptionHistoryEntry>(entry => saved = entry).Returns(Task.CompletedTask);
        using var audio = new AudioRouter(NullLogger<AudioRouter>.Instance, []);
        using var devices = new AudioDeviceService();
        using var pipeline = new StreamingPipeline(NullLogger<StreamingPipeline>.Instance, new PipelineConfig(),
            audio, stt.Object, devices, history.Object, [], [stage.Object], [], llm.Object);
        var states = new List<PipelineState>();
        pipeline.StateChanged += (_, state) => states.Add(state);
        // Enter the post-capture state without opening a real microphone or contacting STT.
        typeof(StreamingPipeline).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(pipeline, PipelineState.Listening);

        var result = await pipeline.StopSessionAsync();

        Assert.NotNull(result);
        Assert.Equal(transcript, result.RawTranscript);
        Assert.Equal(transcript, result.ProcessedText);
        Assert.DoesNotContain(PipelineState.PostProcessing, states);
        Assert.Equal("", saved?.LlmProvider);
        Assert.Empty(llm.Invocations);
        stage.Verify(s => s.ProcessAsync(It.IsAny<string>(), It.IsAny<PostProcessingContext>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void RegistrationNeverInstallsCleanupEvenWithLegacyConfigEnabled()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Pipeline:EnableTextCleanup"] = "true" }).Build();
        services.AddWhisperDeskPipeline(new PipelineConfig(), config);
        Assert.DoesNotContain(services, s => s.ImplementationType == typeof(LlmTextCleanupStage));
        Assert.Contains(services, s => s.ImplementationType == typeof(LlmCommandStage));
    }
}
