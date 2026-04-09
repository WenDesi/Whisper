using System.IO;
using H.Hooks;
using WhisperDesk.Core.Diagnostics;
using WhisperDesk.Models;
using Microsoft.Extensions.Logging;
using HKey = H.Hooks.Key;

namespace WhisperDesk.Services;

public class HotkeyService : IDisposable
{
    private readonly ILogger<HotkeyService> _logger;
    private readonly HotkeySettings _settings;
    private LowLevelKeyboardHook? _keyboardHook;

    private bool _pushToTalkActive;
    private readonly HashSet<HKey> _pressedKeys = new();

    public event EventHandler? PushToTalkPressed;
    public event EventHandler? PushToTalkReleased;
    public event EventHandler? PasteHotkeyPressed;

    public HotkeyService(ILogger<HotkeyService> logger, HotkeySettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    [Trace]
    public void Start()
    {
        _keyboardHook = new LowLevelKeyboardHook
        {
            HandleModifierKeys = true
        };
        _keyboardHook.Down += OnKeyDown;
        _keyboardHook.Up += OnKeyUp;
        _keyboardHook.Start();

        _logger.LogInformation("Hotkey service started. Push-to-talk: {PTT}, Paste: {Paste}",
            _settings.PushToTalk, _settings.PasteTranscription);
    }

    [Trace]
    private void OnKeyDown(object? sender, KeyboardEventArgs e)
    {
        foreach (var key in e.Keys.Values)
        {
            _pressedKeys.Add(key);
        }

        // Check push-to-talk
        if (!_pushToTalkActive && IsHotkeyPressed(_settings.PushToTalk))
        {
            _pushToTalkActive = true;
            _logger.LogDebug("Push-to-talk activated");
            PushToTalkPressed?.Invoke(this, EventArgs.Empty);
        }

        // Check paste hotkey
        if (IsHotkeyPressed(_settings.PasteTranscription))
        {
            _logger.LogDebug("Paste hotkey activated");
            PasteHotkeyPressed?.Invoke(this, EventArgs.Empty);
        }
    }

    [Trace]
    private void OnKeyUp(object? sender, KeyboardEventArgs e)
    {
        foreach (var key in e.Keys.Values)
        {
            _pressedKeys.Remove(key);
        }

        // Check if push-to-talk was released
        if (_pushToTalkActive && !IsHotkeyPressed(_settings.PushToTalk))
        {
            _pushToTalkActive = false;
            _logger.LogDebug("Push-to-talk released");
            PushToTalkReleased?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool IsHotkeyPressed(string hotkeyString)
    {
        var parts = hotkeyString.Split('+').Select(p => p.Trim()).ToArray();

        foreach (var part in parts)
        {
            var key = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HKey.Ctrl,
                "shift" => HKey.Shift,
                "alt" => HKey.Alt,
                _ => Enum.TryParse<HKey>(part, true, out var k) ? k : (HKey?)null
            };

            if (key == null) return false;

            // For modifier keys, check both left and right variants
            bool isPressed = key.Value switch
            {
                HKey.Ctrl => _pressedKeys.Contains(HKey.Ctrl) || _pressedKeys.Contains(HKey.LeftCtrl) || _pressedKeys.Contains(HKey.RightCtrl),
                HKey.Shift => _pressedKeys.Contains(HKey.Shift) || _pressedKeys.Contains(HKey.LeftShift) || _pressedKeys.Contains(HKey.RightShift),
                HKey.Alt => _pressedKeys.Contains(HKey.Alt) || _pressedKeys.Contains(HKey.LeftAlt) || _pressedKeys.Contains(HKey.RightAlt),
                _ => _pressedKeys.Contains(key.Value)
            };

            if (!isPressed) return false;
        }

        return true;
    }

    public void Stop()
    {
        _keyboardHook?.Dispose();
        _keyboardHook = null;
        _logger.LogInformation("Hotkey service stopped");
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
