using H.Hooks;
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
    private bool _correctionActive;
    private readonly HashSet<HKey> _pressedKeys = new();

    public event EventHandler? PushToTalkPressed;
    public event EventHandler? PushToTalkReleased;
    public event EventHandler? PasteHotkeyPressed;
    public event EventHandler? CorrectionPressed;
    public event EventHandler? CorrectionReleased;

    public HotkeyService(ILogger<HotkeyService> logger, HotkeySettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public void Start()
    {
        _keyboardHook = new LowLevelKeyboardHook
        {
            HandleModifierKeys = true,
            // Handling=true makes event dispatch synchronous on the hook thread,
            // so e.IsHandled=true causes the hook to return LRESULT(-1) and
            // actually suppress the keystroke before it reaches the message queue.
            // Without this, H.Hooks dispatches events via ThreadPool (async) and
            // reads IsHandled immediately after, always seeing false.
            Handling = true
        };
        _keyboardHook.Down += OnKeyDown;
        _keyboardHook.Up += OnKeyUp;
        _keyboardHook.Start();

        _logger.LogInformation("Hotkey service started. Push-to-talk: {PTT}, Paste: {Paste}, Correction: {Correction}",
            _settings.PushToTalk, _settings.PasteTranscription, _settings.CorrectionHotkey);
    }

    private void OnKeyDown(object? sender, KeyboardEventArgs e)
    {
        foreach (var key in e.Keys.Values)
        {
            _pressedKeys.Add(key);
        }

        // Check push-to-talk
        if (!_pushToTalkActive && !_correctionActive && IsHotkeyPressed(_settings.PushToTalk))
        {
            _pushToTalkActive = true;
            e.IsHandled = true;
            _logger.LogDebug("Push-to-talk activated");
            PushToTalkPressed?.Invoke(this, EventArgs.Empty);
            return;
        }

        // While push-to-talk is held, swallow repeat events for the PTT key
        if (_pushToTalkActive && ContainsPushToTalkKey(e))
        {
            e.IsHandled = true;
            return;
        }

        // Check correction hotkey
        if (!_correctionActive && !_pushToTalkActive && IsHotkeyPressed(_settings.CorrectionHotkey))
        {
            _correctionActive = true;
            e.IsHandled = true;
            _logger.LogDebug("Correction hotkey activated");
            CorrectionPressed?.Invoke(this, EventArgs.Empty);
            return;
        }

        // While correction is held, swallow repeat events
        if (_correctionActive && ContainsHotkeyKey(e, _settings.CorrectionHotkey))
        {
            e.IsHandled = true;
            return;
        }

        // Check paste hotkey
        if (IsHotkeyPressed(_settings.PasteTranscription))
        {
            e.IsHandled = true;
            _logger.LogDebug("Paste hotkey activated");
            PasteHotkeyPressed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnKeyUp(object? sender, KeyboardEventArgs e)
    {
        bool containsPttKey = ContainsPushToTalkKey(e);
        bool containsCorrectionKey = ContainsHotkeyKey(e, _settings.CorrectionHotkey);

        foreach (var key in e.Keys.Values)
        {
            _pressedKeys.Remove(key);
        }

        // Check if push-to-talk was released
        if (_pushToTalkActive && !IsHotkeyPressed(_settings.PushToTalk))
        {
            _pushToTalkActive = false;
            e.IsHandled = true;
            _logger.LogDebug("Push-to-talk released");
            PushToTalkReleased?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Swallow any PTT-related key release while PTT is still active
        if (_pushToTalkActive && containsPttKey)
        {
            e.IsHandled = true;
        }

        // Check if correction hotkey was released
        if (_correctionActive && !IsHotkeyPressed(_settings.CorrectionHotkey))
        {
            _correctionActive = false;
            e.IsHandled = true;
            _logger.LogDebug("Correction hotkey released");
            CorrectionReleased?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Swallow any correction-related key release while correction is still active
        if (_correctionActive && containsCorrectionKey)
        {
            e.IsHandled = true;
        }
    }

    /// <summary>
    /// Check if the keyboard event contains any key that is part of the push-to-talk hotkey.
    /// </summary>
    private bool ContainsPushToTalkKey(KeyboardEventArgs e) => ContainsHotkeyKey(e, _settings.PushToTalk);

    /// <summary>
    /// Check if the keyboard event contains any key that is part of the specified hotkey string.
    /// </summary>
    private bool ContainsHotkeyKey(KeyboardEventArgs e, string hotkeyString)
    {
        var pttParts = hotkeyString.Split('+').Select(p => p.Trim().ToLowerInvariant());
        foreach (var part in pttParts)
        {
            HKey? targetKey = part switch
            {
                "ctrl" or "control" => HKey.Ctrl,
                "lctrl" or "leftctrl" => HKey.LeftCtrl,
                "rctrl" or "rightctrl" => HKey.RightCtrl,
                "shift" => HKey.Shift,
                "lshift" or "leftshift" => HKey.LeftShift,
                "rshift" or "rightshift" => HKey.RightShift,
                "alt" => HKey.Alt,
                "lalt" or "leftalt" => HKey.LeftAlt,
                "ralt" or "rightalt" => HKey.RightAlt,
                _ => Enum.TryParse<HKey>(part, true, out var k) ? k : null
            };

            if (targetKey == null) continue;

            foreach (var eventKey in e.Keys.Values)
            {
                if (eventKey == targetKey.Value) return true;
                if (targetKey.Value == HKey.RightAlt && (eventKey == HKey.Alt || eventKey == HKey.RightAlt)) return true;
                if (targetKey.Value == HKey.LeftAlt && (eventKey == HKey.Alt || eventKey == HKey.LeftAlt)) return true;
                if (targetKey.Value == HKey.Alt && (eventKey == HKey.Alt || eventKey == HKey.LeftAlt || eventKey == HKey.RightAlt)) return true;
            }
        }
        return false;
    }

    private bool IsHotkeyPressed(string hotkeyString)
    {
        var parts = hotkeyString.Split('+').Select(p => p.Trim()).ToArray();

        foreach (var part in parts)
        {
            var key = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HKey.Ctrl,
                "lctrl" or "leftctrl" => HKey.LeftCtrl,
                "rctrl" or "rightctrl" => HKey.RightCtrl,
                "shift" => HKey.Shift,
                "lshift" or "leftshift" => HKey.LeftShift,
                "rshift" or "rightshift" => HKey.RightShift,
                "alt" => HKey.Alt,
                "lalt" or "leftalt" => HKey.LeftAlt,
                "ralt" or "rightalt" => HKey.RightAlt,
                _ => Enum.TryParse<HKey>(part, true, out var k) ? k : (HKey?)null
            };

            if (key == null) return false;

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
