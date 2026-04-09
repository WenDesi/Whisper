using System.Windows.Forms;
using MethodTimer;
using Microsoft.Extensions.Logging;
using WhisperDesk.Core.Diagnostics;

namespace WhisperDesk.Services;

/// <summary>
/// Simulates keyboard paste (Ctrl+V) into the currently focused application.
/// </summary>
public class ClipboardPasteService
{
    private readonly ILogger<ClipboardPasteService> _logger;

    public ClipboardPasteService(ILogger<ClipboardPasteService> logger)
    {
        _logger = logger;
    }

    [Time]
    public void PasteToActiveWindow()
    {
        using var _span = MethodTimeLogger.BeginSpan();

        try
        {
            // Small delay to let the hotkey keys release
            Task.Delay(100).Wait();

            // Use SendKeys to simulate Ctrl+V
            SendKeys.SendWait("^v");

            _logger.LogDebug("Paste simulated to active window");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to paste to active window");
        }
    }
}
