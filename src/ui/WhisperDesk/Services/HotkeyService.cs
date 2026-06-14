using H.Hooks;
using WhisperDesk.Core.Contract;
using WhisperDesk.Models;
using Microsoft.Extensions.Logging;
using HKey = H.Hooks.Key;

namespace WhisperDesk.Services;

public class HotkeyService : IDisposable
{
    private readonly ILogger<HotkeyService> _logger;
    private readonly HotkeySettings _settings;
    private LowLevelKeyboardHook? _keyboardHook;

    private bool _recordingActive;
    private readonly HashSet<HKey> _pressedKeys = new();

    public event EventHandler<SessionMode>? RecordPressed;
    public event EventHandler<SessionMode>? RecordReleased;

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

        _logger.LogInformation("Hotkey service started. Transcribe: {Transcribe}, Instruct: {Instruct}",
            _settings.Transcribe, _settings.Instruct);
    }

    private void OnKeyDown(object? sender, KeyboardEventArgs e)
    {
        foreach (var key in e.Keys.Values)
        {
            _pressedKeys.Add(key);
        }

        // Recording activates when EITHER hotkey is pressed.
        // Instruct (the more specific binding) is checked first so RightAlt+Shift
        // is recognized as Instruct rather than as Transcribe + extra modifier.
        if (!_recordingActive)
        {
            if (IsHotkeyPressed(_settings.Instruct))
            {
                _recordingActive = true;
                e.IsHandled = true;
                _logger.LogDebug("Recording activated (mode=Instruct)");
                RecordPressed?.Invoke(this, SessionMode.Instruct);
                return;
            }
            if (IsHotkeyPressed(_settings.Transcribe))
            {
                _recordingActive = true;
                e.IsHandled = true;
                _logger.LogDebug("Recording activated (mode=Transcribe)");
                RecordPressed?.Invoke(this, SessionMode.Transcribe);
                return;
            }
        }

        // While recording is active, swallow repeat events for any key that
        // is part of either hotkey so the keystroke doesn't leak.
        if (_recordingActive && ContainsAnyRecordingKey(e))
        {
            e.IsHandled = true;
        }
    }

    private void OnKeyUp(object? sender, KeyboardEventArgs e)
    {
        bool containsRecordingKey = ContainsAnyRecordingKey(e);

        foreach (var key in e.Keys.Values)
        {
            _pressedKeys.Remove(key);
        }

        if (_recordingActive)
        {
            // Recording ends only once neither hotkey is held anymore.
            var instructHeld = IsHotkeyPressed(_settings.Instruct);
            var transcribeHeld = IsHotkeyPressed(_settings.Transcribe);

            if (!instructHeld && !transcribeHeld)
            {
                _recordingActive = false;
                e.IsHandled = true;
                // Re-evaluate mode at release time: if Shift was held when the
                // last key went up, we treat the session as Instruct.
                // ContainsAnyRecordingKey already consumed the released keys
                // from _pressedKeys, so we check what was held just before.
                var releaseMode = DetermineReleaseMode(e);
                _logger.LogDebug("Recording released (mode={Mode})", releaseMode);
                RecordReleased?.Invoke(this, releaseMode);
                return;
            }

            // Still holding part of a hotkey — swallow this release.
            if (containsRecordingKey)
            {
                e.IsHandled = true;
            }
        }
    }

    /// <summary>
    /// Decide the release-time mode. The keys in <paramref name="e"/> were just
    /// released and have already been removed from _pressedKeys. Reconstruct the
    /// pressed-set as it was at the moment of release and test against Instruct.
    /// </summary>
    private SessionMode DetermineReleaseMode(KeyboardEventArgs e)
    {
        var atReleaseInstant = new HashSet<HKey>(_pressedKeys);
        foreach (var key in e.Keys.Values)
        {
            atReleaseInstant.Add(key);
        }
        return IsHotkeyPressedAgainst(_settings.Instruct, atReleaseInstant)
            ? SessionMode.Instruct
            : SessionMode.Transcribe;
    }

    /// <summary>
    /// Check if the keyboard event contains any key that is part of EITHER
    /// recording hotkey. Used to swallow repeat down/up events while recording.
    /// </summary>
    private bool ContainsAnyRecordingKey(KeyboardEventArgs e)
    {
        return ContainsHotkeyKey(_settings.Transcribe, e)
            || ContainsHotkeyKey(_settings.Instruct, e);
    }

    private static bool ContainsHotkeyKey(string hotkeyString, KeyboardEventArgs e)
    {
        var parts = hotkeyString.Split('+').Select(p => p.Trim().ToLowerInvariant());
        foreach (var part in parts)
        {
            var targetKey = ParseKey(part);
            if (targetKey == null) continue;

            foreach (var eventKey in e.Keys.Values)
            {
                if (eventKey == targetKey.Value) return true;
                if (targetKey.Value == HKey.RightAlt && (eventKey == HKey.Alt || eventKey == HKey.RightAlt)) return true;
                if (targetKey.Value == HKey.LeftAlt && (eventKey == HKey.Alt || eventKey == HKey.LeftAlt)) return true;
                if (targetKey.Value == HKey.Alt && (eventKey == HKey.Alt || eventKey == HKey.LeftAlt || eventKey == HKey.RightAlt)) return true;
                if (targetKey.Value == HKey.Shift && (eventKey == HKey.Shift || eventKey == HKey.LeftShift || eventKey == HKey.RightShift)) return true;
                if (targetKey.Value == HKey.Ctrl && (eventKey == HKey.Ctrl || eventKey == HKey.LeftCtrl || eventKey == HKey.RightCtrl)) return true;
            }
        }
        return false;
    }

    private bool IsHotkeyPressed(string hotkeyString) => IsHotkeyPressedAgainst(hotkeyString, _pressedKeys);

    private static bool IsHotkeyPressedAgainst(string hotkeyString, IReadOnlySet<HKey> pressed)
    {
        var parts = hotkeyString.Split('+').Select(p => p.Trim()).ToArray();

        foreach (var part in parts)
        {
            var key = ParseKey(part.ToLowerInvariant());
            if (key == null) return false;

            bool isPressed = key.Value switch
            {
                HKey.Ctrl => pressed.Contains(HKey.Ctrl) || pressed.Contains(HKey.LeftCtrl) || pressed.Contains(HKey.RightCtrl),
                HKey.Shift => pressed.Contains(HKey.Shift) || pressed.Contains(HKey.LeftShift) || pressed.Contains(HKey.RightShift),
                HKey.Alt => pressed.Contains(HKey.Alt) || pressed.Contains(HKey.LeftAlt) || pressed.Contains(HKey.RightAlt),
                _ => pressed.Contains(key.Value)
            };

            if (!isPressed) return false;
        }

        return true;
    }

    private static HKey? ParseKey(string part) => part switch
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
