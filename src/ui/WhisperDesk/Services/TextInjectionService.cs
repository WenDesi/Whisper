using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace WhisperDesk.Services;

/// <summary>
/// Injects text into whatever window currently has keyboard focus.
/// <para>
/// Default path synthesizes Unicode keyboard input via Win32 <c>SendInput</c> with
/// <c>KEYEVENTF_UNICODE</c>. This avoids the clipboard and works across virtually any
/// focusable surface (Notepad, terminals, browsers, Electron apps, Office, IDEs).
/// </para>
/// <para>
/// Exception: when the focused window belongs to a Remote Desktop client (mstsc/msrdc),
/// Unicode-only synthetic input carries no scan code and mstsc does not forward it into
/// the remote session. There we fall back to clipboard redirection (which mstsc shares
/// with the remote host by default) plus a scan-code Ctrl+V, restoring the prior
/// clipboard afterwards.
/// </para>
/// </summary>
public sealed partial class TextInjectionService
{
    private const ushort INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    // Hardware scan codes (set 1) for a scan-code Ctrl+V. Real scan codes are what
    // mstsc forwards into the remote session; KEYEVENTF_UNICODE events are not.
    private const ushort SCAN_ESC = 0x01;
    private const ushort SCAN_LCTRL = 0x1D;
    private const ushort SCAN_V = 0x2F;
    private const int RemoteClipboardPropagationDelayMs = 200;
    private const int RemoteMenuDismissDelayMs = 30;
    private const int RemotePasteCompletionDelayMs = 200;

    private readonly ILogger<TextInjectionService> _logger;

    public TextInjectionService(ILogger<TextInjectionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Synchronously injects <paramref name="text"/> into the currently focused window.
    /// Uses Unicode <c>SendInput</c> normally; switches to clipboard paste when the
    /// foreground window is a Remote Desktop client. Safe to call from any thread.
    /// </summary>
    public void InjectText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (IsForegroundRemoteDesktop())
        {
            _logger.LogInformation("[TextInjection] Remote Desktop foreground detected; using clipboard paste.");
            InjectViaClipboardPaste(text);
            return;
        }

        // Two INPUT events per UTF-16 code unit: down then up.
        // Surrogate pairs occupy two code units; Windows reassembles them.
        var inputs = new INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            inputs[i * 2] = NewKeyInput(ch, isKeyUp: false);
            inputs[i * 2 + 1] = NewKeyInput(ch, isKeyUp: true);
        }

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            var err = Marshal.GetLastWin32Error();
            _logger.LogWarning("[TextInjection] SendInput sent {Sent}/{Expected}, lastError={Err}",
                sent, inputs.Length, err);
        }
        else
        {
            _logger.LogInformation("[TextInjection] Injected {Chars} chars.", text.Length);
        }
    }

    // Remote Desktop client process names. mstsc = classic Windows RDP client,
    // msrdc = the newer "Remote Desktop" / Azure Virtual Desktop client.
    private static readonly HashSet<string> RemoteDesktopProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "mstsc",
        "msrdc",
    };

    private static bool IsForegroundRemoteDesktop()
    {
        var (processName, _) = ForegroundWindowInfo.Get();
        return RemoteDesktopProcesses.Contains(processName);
    }

    /// <summary>
    /// Places <paramref name="text"/> on the clipboard, sends a scan-code Ctrl+V, then
    /// restores the previous clipboard. Used for Remote Desktop targets where Unicode
    /// SendInput is dropped at the RDP boundary but clipboard redirection works.
    /// </summary>
    private void InjectViaClipboardPaste(string text)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            InjectViaClipboardPasteCore(text);
            return;
        }

        Exception? threadException = null;
        var thread = new Thread(() =>
        {
            try
            {
                InjectViaClipboardPasteCore(text);
            }
            catch (Exception ex)
            {
                threadException = ex;
            }
        })
        {
            IsBackground = true,
            Name = "WhisperDeskRdpClipboardPaste"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (threadException != null)
        {
            _logger.LogWarning(threadException, "[TextInjection] RDP clipboard paste STA worker failed.");
        }
    }

    private void InjectViaClipboardPasteCore(string text)
    {
        string? previous = null;
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                previous = System.Windows.Clipboard.GetText();
            }

            if (!TrySetClipboardText(text))
            {
                _logger.LogWarning("[TextInjection] Clipboard set failed; aborting RDP paste.");
                return;
            }

            // Give RDP clipboard redirection a moment to propagate to the remote host
            // before the remote session processes the paste.
            Thread.Sleep(RemoteClipboardPropagationDelayMs);
            SendScanCodeKey(SCAN_ESC, "Esc");
            Thread.Sleep(RemoteMenuDismissDelayMs);
            SendScanCodePaste();
            Thread.Sleep(RemotePasteCompletionDelayMs);
            _logger.LogInformation("[TextInjection] Pasted {Chars} chars via clipboard (RDP).", text.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TextInjection] RDP clipboard paste failed.");
        }
        finally
        {
            RestoreClipboard(previous);
        }
    }

    private void SendScanCodeKey(ushort scanCode, string keyName)
    {
        var inputs = new[]
        {
            NewScanInput(scanCode, isKeyUp: false),
            NewScanInput(scanCode, isKeyUp: true),
        };

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            var err = Marshal.GetLastWin32Error();
            _logger.LogWarning("[TextInjection] {Key} SendInput sent {Sent}/{Expected}, lastError={Err}",
                keyName, sent, inputs.Length, err);
        }
    }

    private void SendScanCodePaste()
    {
        var inputs = new[]
        {
            NewScanInput(SCAN_LCTRL, isKeyUp: false),
            NewScanInput(SCAN_V, isKeyUp: false),
            NewScanInput(SCAN_V, isKeyUp: true),
            NewScanInput(SCAN_LCTRL, isKeyUp: true),
        };

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            var err = Marshal.GetLastWin32Error();
            _logger.LogWarning("[TextInjection] Ctrl+V SendInput sent {Sent}/{Expected}, lastError={Err}",
                sent, inputs.Length, err);
        }
    }

    // Clipboard SetText is flaky under contention; retry until it reflects what we set.
    private const int ClipboardRetryAttempts = 8;
    private const int ClipboardRetryDelayMs = 25;

    private bool TrySetClipboardText(string text)
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
                _logger.LogDebug(ex, "[TextInjection] Clipboard SetText attempt {Attempt} failed; retrying.", i + 1);
            }
            Thread.Sleep(ClipboardRetryDelayMs);
        }
        _logger.LogWarning("[TextInjection] Clipboard SetText failed after {Attempts} attempts.", ClipboardRetryAttempts);
        return false;
    }

    private void RestoreClipboard(string? previous)
    {
        try
        {
            if (previous != null)
            {
                TrySetClipboardText(previous);
            }
            else
            {
                System.Windows.Clipboard.Clear();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[TextInjection] Clipboard restore failed.");
        }
    }

    private static INPUT NewScanInput(ushort scanCode, bool isKeyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = scanCode,
                dwFlags = KEYEVENTF_SCANCODE | (isKeyUp ? KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    private static INPUT NewKeyInput(char ch, bool isKeyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = ch,
                dwFlags = KEYEVENTF_UNICODE | (isKeyUp ? KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint cInputs, [In] INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        // Size padding so the union matches the largest variant (MOUSEINPUT on x64 is the biggest).
        [FieldOffset(0)] private MOUSEINPUT _mi;
        [FieldOffset(0)] private HARDWAREINPUT _hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
