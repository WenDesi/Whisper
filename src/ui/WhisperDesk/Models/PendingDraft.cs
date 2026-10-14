using WhisperDesk.Core.Contract;

namespace WhisperDesk.Models;

public sealed record PendingDraft(
    string OriginalText,
    string Text,
    WindowTextContext Context,
    TimeSpan CommitDelay,
    IReadOnlyList<DraftCorrectionTurn> Corrections)
{
    public WindowTextContext CreateCorrectionContext() => Context with
    {
        Selected = string.Empty,
        DraftText = Text,
        OriginalDraftText = OriginalText,
        DraftCorrections = Corrections.ToArray()
    };

    public PendingDraft ApplyCorrection(PipelineResult result, TimeSpan commitDelay) => this with
    {
        Text = result.ProcessedText,
        CommitDelay = commitDelay,
        Corrections = [.. Corrections, new DraftCorrectionTurn
        {
            RawInstruction = result.RawTranscript,
            Output = result.ProcessedText,
            Timestamp = new DateTimeOffset(result.Timestamp)
        }]
    };
}
