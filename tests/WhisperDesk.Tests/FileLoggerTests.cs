using System.IO;
using Microsoft.Extensions.Logging;
using WhisperDesk.Logging;
using Xunit;

namespace WhisperDesk.Tests;

public class FileLoggerTests
{
    [Fact]
    public async Task BackgroundWriterDrainsMessagesInOrderOnAsyncDisposal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"whisperdesk-log-{Guid.NewGuid():N}.log");
        var provider = new FileLoggerProvider(path);
        try
        {
            var logger = provider.CreateLogger("Test");
            for (var i = 0; i < 200; i++)
                logger.LogInformation("Entry {Index}", i);

            await provider.DisposeAsync();
            var entries = (await File.ReadAllLinesAsync(path))
                .Where(line => line.Contains("Entry ", StringComparison.Ordinal))
                .Select(line => line[line.IndexOf("Entry ", StringComparison.Ordinal)..]);
            Assert.Equal(Enumerable.Range(0, 200).Select(i => $"Entry {i}"), entries);
        }
        finally
        {
            await provider.DisposeAsync();
            File.Delete(path);
        }
    }
}
