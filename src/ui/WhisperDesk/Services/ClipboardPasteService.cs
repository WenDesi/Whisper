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

    /// <summary>
    /// Undo the previous paste (Ctrl+Z) then paste the corrected text (Ctrl+V).
    /// </summary>
    public async Task UndoAndPasteAsync()
    {
        try
        {
            _logger.LogDebug("[PasteService] Sending Ctrl+Z to undo previous paste");
            await Task.Delay(100);
            SendKeys.SendWait("^z");

            _logger.LogDebug("[PasteService] Waiting before paste...");
            await Task.Delay(200);

            SendKeys.SendWait("^v");
            _logger.LogDebug("[PasteService] Undo and paste completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PasteService] Failed to undo and paste");
        }
    }
}
