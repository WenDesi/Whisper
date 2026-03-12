using System.Windows;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;

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

    public void PasteToActiveWindow()
    {
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
