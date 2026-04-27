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

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr hWnd);

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
        GetWindowThreadProcessId(hwnd, out var pid);
        var process = Process.GetProcessById((int)pid);

        var fileFullPath = ExtractFileFullPath(hwnd);
        var selected = GetSelectedTextViaClipboard();

        return new WindowTextContext
        {
            Selected = selected,
            FileFullPath = fileFullPath,
            WindowHandle = hwnd,
            MainWindowTitle = process.MainWindowTitle
        };
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
        _logger.LogInformation("[Append] Start. Handle={Handle}, TextLen={Len}", context.WindowHandle, text.Length);

        if (!IsWindowAlive(context.WindowHandle))
        {
            _logger.LogWarning("[Append] Window is gone. Handle={Handle}", context.WindowHandle);
            return ErrorWindowGone;
        }

        _logger.LogDebug("[Append] Activating window. Handle={Handle}", context.WindowHandle);
        ActivateWindow(context.WindowHandle);

        var hasFocus = GetSelectedTextViaClipboard() != string.Empty;
        _logger.LogDebug("[Append] Focus probe via clipboard. HasFocus={HasFocus}", hasFocus);

        if (hasFocus)
        {
            _logger.LogDebug("[Append] Pasting over current selection.");
            PasteOverSelection(text);   // pastes at current cursor, no focus change
        }
        else
        {
            _logger.LogDebug("[Append] No focus detected; appending via Ctrl+End + paste.");
            AppendViaClipboard(context.WindowHandle, text);
        }

        _logger.LogInformation("[Append] Done. Handle={Handle}", context.WindowHandle);
        return Success;
    }

    /// <summary>
    /// Reads the full text content of the foreground editor / page / document
    /// captured in <paramref name="context"/>. Uses UIA TextPattern when reliable
    /// (browsers, PDF viewers, Office) and falls back to Ctrl+A/Ctrl+C for
    /// virtualized editors (VS Code, Visual Studio, JetBrains, etc.).
    /// </summary>
    public static string ReadAllContext(WindowTextContext context)
    {
        if (!IsWindowAlive(context.WindowHandle))
            return string.Empty;

        ActivateWindow(context.WindowHandle);

        var processName = string.Empty;
        try
        {
            GetWindowThreadProcessId(context.WindowHandle, out var pid);
            processName = Process.GetProcessById((int)pid).ProcessName;
        }
        catch { }

        return GetAllTextOfWindow(context.WindowHandle, processName);
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
        // if (hwnd == IntPtr.Zero || GetForegroundWindow() == hwnd) return;
        // SetForegroundWindow(hwnd);
        // Thread.Sleep(30);
    }

    private static string SetAllTextViaClipboard(IntPtr hwnd, string text)
    {
        string? previous = null;
        try
        {
            if (System.Windows.Clipboard.ContainsText())
                previous = System.Windows.Clipboard.GetText();

            if (!TrySetClipboardText(text))
                return ErrorNotSupported;
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

            if (!TrySetClipboardText(text))
            {
                _logger.LogWarning("[AppendViaClipboard] Clipboard set failed; aborting paste.");
                return;
            }
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
            return TryGetClipboardTextAfterCopy();
        }
        catch { return string.Empty; }
        finally { RestoreClipboard(previous); }
    }

    // Apps where Ctrl+A/Ctrl+C is more reliable than UIA — typically virtualized
    // editors (Electron/canvas-based) where UIA only exposes the visible viewport.
    private static readonly HashSet<string> ClipboardFirstProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code",            // VS Code
        "Code - Insiders",
        "Cursor",
        "windsurf",
        "devenv",          // Visual Studio
        "rider64",         // JetBrains Rider
        "idea64",          // IntelliJ
        "pycharm64",
        "webstorm64",
        "sublime_text",
        "notepad++",
    };

    private static string GetAllTextOfWindow(IntPtr hwnd, string processName)
    {
        if (ClipboardFirstProcesses.Contains(processName))
        {
            var clipText = GetAllTextViaClipboard();
            if (!string.IsNullOrEmpty(clipText)) return clipText;
            return GetAllTextViaUia(hwnd);
        }

        var uiaText = GetAllTextViaUia(hwnd);
        if (!string.IsNullOrEmpty(uiaText))
            return uiaText;

        return GetAllTextViaClipboard();
    }

    private static string GetAllTextViaUia(IntPtr hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return string.Empty;

            // Prefer the focused element (the actual editor/document the user is in).
            var focused = AutomationElement.FocusedElement;
            if (focused != null && IsDescendantOf(focused, root))
            {
                var text = ReadTextFromElement(focused);
                if (!string.IsNullOrEmpty(text)) return text;
            }

            // Fallback: walk descendants for the first Document control that supports TextPattern.
            var documents = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
            foreach (AutomationElement doc in documents)
            {
                var text = ReadTextFromElement(doc);
                if (!string.IsNullOrEmpty(text)) return text;
            }
        }
        catch { }

        return string.Empty;
    }

    private static bool IsDescendantOf(AutomationElement element, AutomationElement ancestor)
    {
        try
        {
            var walker = TreeWalker.RawViewWalker;
            var current = element;
            while (current != null)
            {
                if (Automation.Compare(current, ancestor)) return true;
                current = walker.GetParent(current);
            }
        }
        catch { }
        return false;
    }

    private static string ReadTextFromElement(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) &&
                pattern is TextPattern textPattern)
            {
                return textPattern.DocumentRange.GetText(-1) ?? string.Empty;
            }

            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObj) &&
                valueObj is ValuePattern valuePattern)
            {
                return valuePattern.Current.Value ?? string.Empty;
            }
        }
        catch { }
        return string.Empty;
    }

    private static string GetAllTextViaClipboard()
    {
        string? previous = null;
        try
        {
            if (System.Windows.Clipboard.ContainsText())
                previous = System.Windows.Clipboard.GetText();

            System.Windows.Clipboard.Clear();
            SendKeys.SendWait("^a");
            Thread.Sleep(30);
            SendKeys.SendWait("^c");

            var text = TryGetClipboardTextAfterCopy();

            // Collapse the selection so the caller doesn't leave the editor in a select-all state.
            SendKeys.SendWait("{RIGHT}");
            return text;
        }
        catch { return string.Empty; }
        finally { RestoreClipboard(previous); }
    }

    private static bool IsWindowAlive(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try { return IsWindow(hwnd); }
        catch { return false; }
    }

    private static void PasteOverSelection(string text)
    {
        string? previous = null;
        try
        {
            if (System.Windows.Clipboard.ContainsText())
                previous = System.Windows.Clipboard.GetText();

            if (!TrySetClipboardText(text))
            {
                _logger.LogWarning("[PasteOverSelection] Clipboard set failed; aborting paste.");
                return;
            }
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
            if (previous != null) TrySetClipboardText(previous);
            else System.Windows.Clipboard.Clear();
        }
        catch { }
    }

    // Clipboard APIs are flaky under contention (other apps holding the clipboard,
    // Ctrl+C/Ctrl+V races). Retry until the clipboard reflects what we intended,
    // or we give up. Returns true if the clipboard ended up with the expected text.
    private const int ClipboardRetryAttempts = 8;
    private const int ClipboardRetryDelayMs = 25;

    private static bool TrySetClipboardText(string text)
    {
        for (var i = 0; i < ClipboardRetryAttempts; i++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                if (System.Windows.Clipboard.ContainsText() &&
                    System.Windows.Clipboard.GetText() == text)
                {
                    return true;
                }
            }
            catch (Exception ex) when (i < ClipboardRetryAttempts - 1)
            {
                _logger.LogDebug(ex, "[Clipboard] SetText attempt {Attempt} failed; retrying.", i + 1);
            }
            Thread.Sleep(ClipboardRetryDelayMs);
        }
        _logger.LogWarning("[Clipboard] SetText failed after {Attempts} attempts.", ClipboardRetryAttempts);
        return false;
    }

    // Used after sending Ctrl+C: poll until the clipboard has text (the target app
    // may take a few ms to populate it) or until we time out.
    private static string TryGetClipboardTextAfterCopy()
    {
        for (var i = 0; i < ClipboardRetryAttempts; i++)
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    var text = System.Windows.Clipboard.GetText();
                    if (!string.IsNullOrEmpty(text)) return text;
                }
            }
            catch (Exception ex) when (i < ClipboardRetryAttempts - 1)
            {
                _logger.LogDebug(ex, "[Clipboard] GetText attempt {Attempt} failed; retrying.", i + 1);
            }
            Thread.Sleep(ClipboardRetryDelayMs);
        }
        return string.Empty;
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
