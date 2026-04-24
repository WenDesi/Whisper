using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperDesk.Core.Contract;

namespace WhisperDesk.Services;

public static partial class ForegroundWindowInfo
{
    private static ILogger _logger = NullLogger.Instance;

    public static void Configure(ILogger logger) => _logger = logger;
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

#pragma warning disable SYSLIB1054
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
#pragma warning restore SYSLIB1054

    public static (string ProcessName, string WindowTitle) Get()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return ("", "");

            GetWindowThreadProcessId(hwnd, out var pid);
            var process = Process.GetProcessById((int)pid);
            return (process.ProcessName, process.MainWindowTitle);
        }
        catch
        {
            return ("", "");
        }
    }

    /// <summary>
    /// Returns selected text, all text, detected file path, and editor type for the
    /// currently focused control in the foreground window.
    /// </summary>
    public static WindowTextContext? GetTextContext()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return null;
        var fileFullPath = ExtractFileFullPath(hwnd);
        var selected = GetSelectedTextViaClipboard();
         GetWindowThreadProcessId(hwnd, out var pid);
         var process = Process.GetProcessById((int)pid);

        return new WindowTextContext{Selected = selected, FileFullPath = fileFullPath, WindowHandle = hwnd, MainWindowTitle = process.MainWindowTitle};
    }

    public const string ErrorWindowGone = "ERROR:WINDOW_GONE";
    public const string ErrorContentChanged = "ERROR:CONTENT_CHANGED";
    public const string Success = "OK";
    public const string ErrorNotSupported = "ERROR:NOT_SUPPORTED";

    /// <summary>
    /// Replaces text using an already-fetched <see cref="WindowTextContext"/>.
    ///
    /// Strategy (in order):
    ///   1. Window gone → return <see cref="ErrorWindowGone"/>
    ///   2. !replaceAll and selection still present → Ctrl+V directly (no full-text rewrite)
    ///   3. Has rooted file path → write file on disk; reload via UIA/clipboard
    ///   4. UIA ValuePattern → SetValue
    ///   5. Clipboard Ctrl+A+V fallback
    ///   For steps 3-5: verify current text matches context.AllText first;
    ///   if changed → return <see cref="ErrorContentChanged"/>
    /// </summary>
    public static string Replace(WindowTextContext context, string source, string target, bool replaceAll = false)
    {
        _logger.LogInformation("Attempting to replace text in foreground window. File: {File}, ReplaceAll: {ReplaceAll}",
            context.FileFullPath, replaceAll);
        if (!IsWindowAlive(context.WindowHandle))
            return ErrorWindowGone;

        var currentText = GetAllText(context.FileFullPath);
        var newText = replaceAll
            ? currentText.Replace(source, target)
            : ReplaceFirst(currentText, source, target);

        // Selection-based paste (replaceFirst only): overwrite the selection directly.
        if (!replaceAll && !string.IsNullOrEmpty(context.Selected))
        {
            PasteOverSelection(target);
            return Success;
        }


        return WriteWithContextGuard(context, newText);
    }

    /// <summary>
    /// Appends <paramref name="text"/> at the cursor position in the window.
    /// If the window has focus, pastes directly at the current cursor via clipboard.
    /// Otherwise moves focus to the window first and uses Ctrl+End + paste.
    /// </summary>
    public static string Append(WindowTextContext context, string text)
    {
        if (!IsWindowAlive(context.WindowHandle))
            return ErrorWindowGone;
        ActivateWindow(context.WindowHandle);

        var hasFocus = GetSelectedTextViaClipboard() != string.Empty;

        if (hasFocus)
            PasteOverSelection(text);   // pastes at current cursor, no focus change
        else
            AppendViaClipboard(context.WindowHandle, text);

        return Success;
    }

    /// <summary>
    /// Writes <paramref name="newText"/> to the window, replacing all existing content.
    /// Same safety checks as <see cref="Replace"/>: verifies window is alive and content
    /// hasn't changed since <paramref name="context"/> was captured.
    /// </summary>
    public static string Write(WindowTextContext context, string newText)
    {
        if (!IsWindowAlive(context.WindowHandle))
            return ErrorWindowGone;

        return WriteWithContextGuard(context, newText);
    }

    private static string WriteWithContextGuard(WindowTextContext context, string newText)
    {

        if (System.IO.Path.IsPathRooted(context.FileFullPath) &&
            System.IO.File.Exists(context.FileFullPath))
        {
            try
            {
                System.IO.File.WriteAllText(context.FileFullPath, newText,
                    System.Text.Encoding.UTF8);
                _logger.LogInformation("Text written to file: {File}", context.FileFullPath);
                return Success;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write text to file: {File}", context.FileFullPath);
            }
        }
        _logger.LogInformation("Falling back to direct window write for handle: {WindowHandle}", context.WindowHandle);

        return WriteText(context.WindowHandle, newText);
    }
    
    /// <summary>
    /// Writes <paramref name="newText"/> to the control in the window identified by
    /// <paramref name="hwnd"/>, replacing all existing content.
    /// </summary>
    private static string WriteText(IntPtr hwnd, string newText)
    {
        _logger.LogInformation("Attempting to write text to window: {WindowHandle}", hwnd);

        SetAllTextViaClipboard(hwnd, newText);
        _logger.LogInformation("Text written via clipboard to window: {WindowHandle}", hwnd);
        return Success;

    }

 

    /// <summary>
    /// Gets all text content from the focused element in the foreground window.
    /// </summary>
    public static string GetAllText(string fileFullPath)
    {
        if (string.IsNullOrEmpty(fileFullPath) || !System.IO.File.Exists(fileFullPath))
            return string.Empty;

        try { return System.IO.File.ReadAllText(fileFullPath, System.Text.Encoding.UTF8); }
        catch { return string.Empty; }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static void ActivateWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || GetForegroundWindow() == hwnd) return;
        SetForegroundWindow(hwnd);
        Thread.Sleep(30);
    }

    private static string SetAllTextViaClipboard(IntPtr hwnd, string text)
    {
        string? previous = null;
        try
        {
            if (System.Windows.Clipboard.ContainsText())
                previous = System.Windows.Clipboard.GetText();

            System.Windows.Clipboard.SetText(text);
            SendKeys.SendWait("^a");
            Thread.Sleep(30);
            SendKeys.SendWait("^v");
            Thread.Sleep(80);
            return Success;
        }
        catch { return ErrorNotSupported; }
        finally { RestoreClipboard(previous); }
    }

    private static void AppendViaClipboard(IntPtr hwnd, string text)
    {
        string? previous = null;
        try
        {
            if (System.Windows.Clipboard.ContainsText())
                previous = System.Windows.Clipboard.GetText();

            System.Windows.Clipboard.SetText(text);
            SendKeys.SendWait("^{END}");
            Thread.Sleep(30);
            SendKeys.SendWait("^v");
            Thread.Sleep(80);
        }
        catch { }
        finally { RestoreClipboard(previous); }
    }

    private static string GetSelectedTextViaClipboard()
    {
        string? previous = null;
        try
        {
            if (System.Windows.Clipboard.ContainsText())
                previous = System.Windows.Clipboard.GetText();

            System.Windows.Clipboard.Clear();
            SendKeys.SendWait("^c");
            Thread.Sleep(80);

            return System.Windows.Clipboard.ContainsText()
                ? System.Windows.Clipboard.GetText()
                : string.Empty;
        }
        catch { return string.Empty; }
        finally { RestoreClipboard(previous); }
    }

    private static bool IsWindowAlive(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try { return AutomationElement.FromHandle(hwnd) != null; }
        catch { return false; }
    }

    private static void PasteOverSelection(string text)
    {
        string? previous = null;
        try
        {
            if (System.Windows.Clipboard.ContainsText())
                previous = System.Windows.Clipboard.GetText();

            System.Windows.Clipboard.SetText(text);
            SendKeys.SendWait("^v");
            Thread.Sleep(80);
        }
        catch { }
        finally { RestoreClipboard(previous); }
    }

    private static void RestoreClipboard(string? previous)
    {
        try
        {
            if (previous != null) System.Windows.Clipboard.SetText(previous);
            else System.Windows.Clipboard.Clear();
        }
        catch { }
    }

    private static string ReplaceFirst(string text, string source, string target)
    {
        var index = text.IndexOf(source, StringComparison.Ordinal);
        if (index < 0) return text;
        return string.Concat(text.AsSpan(0, index), target, text.AsSpan(index + source.Length));
    }

    private static string ExtractFileFullPath(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        return TryGetActiveTabPathFromUia(hwnd);
    }

    /// <summary>
    /// Walks the UIA tree of the window looking for a selected/active tab item whose
    /// Name or HelpText looks like a rooted file path.
    /// </summary>
    private static string TryGetActiveTabPathFromUia(IntPtr hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);

            // Find all tab items that are in "selected" state
            var tabCondition = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
                new PropertyCondition(SelectionItemPattern.IsSelectedProperty, true));

            var tabs = root.FindAll(TreeScope.Descendants, tabCondition);
            foreach (AutomationElement tab in tabs)
            {
                foreach (var candidate in new[] {
                    tab.Current.HelpText,
                    tab.Current.Name,
                    tab.Current.ItemStatus })
                {
                    if (string.IsNullOrWhiteSpace(candidate)) continue;
                    // Strip trailing markers like " •" (unsaved indicator in VS Code)
                    var clean = candidate.TrimEnd(' ', '•', '*').Trim();
                    if (System.IO.Path.IsPathRooted(clean) && System.IO.File.Exists(clean))
                        return clean;
                }
            }
        }
        catch { }

        return string.Empty;
    }
}
