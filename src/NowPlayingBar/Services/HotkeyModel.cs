using System.Text;
using System.Windows.Input;
using NowPlayingBar.Native;

namespace NowPlayingBar.Services;

public class HotkeyGesture
{
    public ModifierKeys Modifiers { get; set; } = ModifierKeys.None;
    public Key Key { get; set; } = Key.None;

    public bool IsEmpty => Key == Key.None;

    public HotkeyGesture() { }

    public HotkeyGesture(ModifierKeys modifiers, Key key)
    {
        Modifiers = modifiers;
        Key = key;
    }

    public static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl
                   or Key.LeftAlt or Key.RightAlt
                   or Key.LeftShift or Key.RightShift
                   or Key.LWin or Key.RWin;
    }

    public uint ToWin32Modifiers()
    {
        uint mod = NativeMethods.MOD_NOREPEAT;
        if (Modifiers.HasFlag(ModifierKeys.Alt)) mod |= NativeMethods.MOD_ALT;
        if (Modifiers.HasFlag(ModifierKeys.Control)) mod |= NativeMethods.MOD_CONTROL;
        if (Modifiers.HasFlag(ModifierKeys.Shift)) mod |= NativeMethods.MOD_SHIFT;
        if (Modifiers.HasFlag(ModifierKeys.Windows)) mod |= NativeMethods.MOD_WIN;
        return mod;
    }

    public uint ToVirtualKey()
    {
        return (uint)KeyInterop.VirtualKeyFromKey(Key);
    }

    public override string ToString()
    {
        if (IsEmpty) return string.Empty;

        var sb = new StringBuilder();
        if (Modifiers.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl + ");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) sb.Append("Alt + ");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) sb.Append("Shift + ");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) sb.Append("Win + ");

        sb.Append(FormatKey(Key));
        return sb.ToString();
    }

    public static string FormatKey(Key key)
    {
        return key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => $"Num {key - Key.NumPad0}",
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.OemQuestion => "?",
            Key.OemPeriod => ".",
            Key.OemComma => ",",
            Key.MediaPlayPause => "PlayPause",
            Key.MediaNextTrack => "NextTrack",
            Key.MediaPreviousTrack => "PrevTrack",
            Key.MediaStop => "Stop",
            Key.VolumeUp => "VolUp",
            Key.VolumeDown => "VolDown",
            Key.VolumeMute => "Mute",
            _ => key.ToString()
        };
    }

    public static bool TryParse(string? text, out HotkeyGesture? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split(new[] { '+', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        var mods = ModifierKeys.None;
        Key key = Key.None;

        foreach (var rawPart in parts)
        {
            var part = rawPart.Trim();
            if (string.Equals(part, "Ctrl", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "Control", StringComparison.OrdinalIgnoreCase))
            {
                mods |= ModifierKeys.Control;
            }
            else if (string.Equals(part, "Alt", StringComparison.OrdinalIgnoreCase))
            {
                mods |= ModifierKeys.Alt;
            }
            else if (string.Equals(part, "Shift", StringComparison.OrdinalIgnoreCase))
            {
                mods |= ModifierKeys.Shift;
            }
            else if (string.Equals(part, "Win", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(part, "Windows", StringComparison.OrdinalIgnoreCase))
            {
                mods |= ModifierKeys.Windows;
            }
            else
            {
                // Key parse
                if (Enum.TryParse<Key>(part, true, out var parsedKey))
                {
                    key = parsedKey;
                }
                else if (int.TryParse(part, out var digit) && digit is >= 0 and <= 9)
                {
                    key = Key.D0 + digit;
                }
                else
                {
                    key = part.ToUpperInvariant() switch
                    {
                        "+" => Key.OemPlus,
                        "-" => Key.OemMinus,
                        "." => Key.OemPeriod,
                        "," => Key.OemComma,
                        "?" => Key.OemQuestion,
                        "PLAYPAUSE" => Key.MediaPlayPause,
                        "NEXTTRACK" => Key.MediaNextTrack,
                        "PREVTRACK" => Key.MediaPreviousTrack,
                        _ => Key.None
                    };
                }
            }
        }

        if (key != Key.None)
        {
            result = new HotkeyGesture(mods, key);
            return true;
        }

        return false;
    }
}
