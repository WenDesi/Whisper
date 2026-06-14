using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace WhisperDesk.Services;

/// <summary>
/// Injects text into whatever window currently has keyboard focus by synthesizing
/// Unicode keyboard input via Win32 <c>SendInput</c> with <c>KEYEVENTF_UNICODE</c>.
/// Avoids the clipboard entirely. Works across virtually any focusable surface
/// (Notepad, terminals, browsers, Electron apps, Office, IDEs).
/// </summary>
public sealed partial class TextInjectionService
{
    private const ushort INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private readonly ILogger<TextInjectionService> _logger;

    public TextInjectionService(ILogger<TextInjectionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Synchronously injects <paramref name="text"/> as a stream of Unicode keystrokes
    /// into the currently focused window. Safe to call from any thread.
    /// </summary>
    public void InjectText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
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
