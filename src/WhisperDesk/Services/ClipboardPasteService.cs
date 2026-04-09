using System.Diagnostics;
using System.Windows;
using System.Windows.Forms;
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

    public void PasteToActiveWindow()
    {
        using var activity = DiagnosticSources.UI.StartActivity("ClipboardPaste.PasteToActiveWindow");
        activity?.SetTag("thread.id", Environment.CurrentManagedThreadId);

        try
        {
            // Small delay to let the hotkey keys release
            using (var delayStep = DiagnosticSources.UI.StartActivity("ClipboardPaste.PasteToActiveWindow.BlockingDelay"))
            {
                delayStep?.SetTag("thread.id", Environment.CurrentManagedThreadId);
                delayStep?.SetTag("delay.ms", 100);
                Task.Delay(100).Wait();
            }

            // Use SendKeys to simulate Ctrl+V
            using (var sendKeysStep = DiagnosticSources.UI.StartActivity("ClipboardPaste.PasteToActiveWindow.SendKeys"))
            {
                sendKeysStep?.SetTag("thread.id", Environment.CurrentManagedThreadId);
                SendKeys.SendWait("^v");
            }

            _logger.LogDebug("Paste simulated to active window");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to paste to active window");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddTag("exception.type", ex.GetType().FullName);
            activity?.AddTag("exception.message", ex.Message);
        }
    }
}
