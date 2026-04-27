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

    public async Task PasteToActiveWindowAsync()
    {
        try
        {
            _logger.LogDebug("[PasteService] Waiting for hotkey keys to release before pasting");
            // Small delay to let the hotkey keys release
            await Task.Delay(100);

            // Use SendKeys to simulate Ctrl+V
            SendKeys.SendWait("^v");

            _logger.LogDebug("[PasteService] Paste simulated to active window");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PasteService] Failed to paste to active window");
        }
    }
}
