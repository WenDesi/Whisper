using System.Globalization;
using WhisperDesk.Services;
using Xunit;

namespace WhisperDesk.Tests;

public class TextInjectionTests
{
    [Fact]
    public void LongTextIsPacedWithoutLosingOrReorderingCharacters()
    {
        var text = string.Concat(Enumerable.Repeat("Long dictation \u4e2d\u6587\r\n", 200));
        var batches = new List<string>();
        var waits = 0;

        TextInjectionService.SendPacedText(text, batch =>
        {
            Assert.Equal(batches.Count, waits);
            batches.Add(batch.ToString());
        }, () => waits++);

        Assert.Equal(text, string.Concat(batches));
        Assert.All(batches, batch => Assert.InRange(new StringInfo(batch).LengthInTextElements, 1, 2));
        Assert.Equal((new StringInfo(text).LengthInTextElements + 1) / 2, batches.Count);
        Assert.Equal(batches.Count - 1, waits);
    }

    [Fact]
    public void BatchesDoNotSplitSurrogatesCombiningMarksOrEmojiSequences()
    {
        const string text = "a\U0001F600e\u0301\U0001F469\u200d\U0001F4BB\u4e2d\r\nz";
        var batches = new List<string>();

        TextInjectionService.SendPacedText(text, batch => batches.Add(batch.ToString()), () => { });

        Assert.Equal(new[]
        {
            "a\U0001F600",
            "e\u0301\U0001F469\u200d\U0001F4BB",
            "\u4e2d\r\n",
            "z"
        }, batches);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("ab")]
    public void EmptyAndSingleBatchTextDoNotWait(string text)
    {
        var output = "";

        TextInjectionService.SendPacedText(text, batch => output += batch.ToString(),
            () => Assert.Fail("No delay is needed without a following batch."));

        Assert.Equal(text, output);
    }

    [Fact]
    public void SendFailureStopsRemainingBatchesWithoutRetryingDeliveredText()
    {
        var calls = 0;
        var waits = 0;
        var error = new InvalidOperationException("Target changed or input failed.");

        var actual = Assert.Throws<InvalidOperationException>(() =>
            TextInjectionService.SendPacedText("abcdefgh", _ =>
            {
                if (++calls == 2)
                {
                    throw error;
                }
            }, () => waits++));

        Assert.Same(error, actual);
        Assert.Equal(2, calls);
        Assert.Equal(1, waits);
    }
}
