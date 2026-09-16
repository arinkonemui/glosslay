using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Glosslay.Input;

/// <summary>
/// ホットキーの組み合わせ。<c>"Ctrl+Shift+T"</c> のような文字列と相互に変換する（FR-INP-01）。
/// </summary>
/// <param name="VirtualKey">主キーの仮想キーコード。</param>
/// <param name="Control">Ctrl を要求するか。</param>
/// <param name="Shift">Shift を要求するか。</param>
/// <param name="Alt">Alt を要求するか。</param>
/// <remarks>
/// <para>修飾キーは<b>完全一致</b>で判定する。Ctrl+Shift+T の設定で Ctrl+Shift+Alt+T が押されても発動しない。
/// ゲーム側の別の操作と重なる可能性を少しでも減らすため。</para>
/// <para>キーは Raw Input で読むだけで、ゲームにはそのまま届く（FR-INP-02）。
/// したがって<b>主キーも修飾キーもゲームに入る</b>。既定値に「ゲームで使われにくい組み合わせ」を
/// 選ぶ必要があるのはこのため。</para>
/// </remarks>
public readonly record struct HotkeyGesture(ushort VirtualKey, bool Control, bool Shift, bool Alt)
{
    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["End"] = 0x23,
        ["Home"] = 0x24,
        ["Insert"] = 0x2D,
        ["Delete"] = 0x2E,
        ["Pause"] = 0x13,
        ["ScrollLock"] = 0x91,
    };

    /// <summary>
    /// 文字列から組み合わせを作る。
    /// </summary>
    /// <param name="text"><c>"Ctrl+Shift+T"</c> / <c>"Alt+F9"</c> / <c>"Pause"</c> など。大文字小文字は問わない。</param>
    /// <param name="gesture">成功時の組み合わせ。</param>
    /// <returns>解釈できたか。主キーが無い・2 つ以上ある・知らない名前の場合は失敗。</returns>
    public static bool TryParse(string? text, [NotNullWhen(true)] out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var control = false;
        var shift = false;
        var alt = false;
        ushort? key = null;

        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                control = true;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                shift = true;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                alt = true;
            }
            else if (key is not null || !TryParseKey(part, out var parsed))
            {
                // 主キーが 2 つある、または知らない名前。
                return false;
            }
            else
            {
                key = parsed;
            }
        }

        if (key is null)
        {
            return false;
        }

        gesture = new HotkeyGesture(key.Value, control, shift, alt);
        return true;
    }

    /// <summary>
    /// 修飾キー単体かどうか。
    /// </summary>
    /// <remarks>
    /// Raw Input は左右を区別するコード（VK_LSHIFT など）を返すことがあるため、両方を扱う。
    /// </remarks>
    public static bool IsModifier(ushort virtualKey, out ModifierKey modifier)
    {
        modifier = virtualKey switch
        {
            0x11 or 0xA2 or 0xA3 => ModifierKey.Control,  // VK_CONTROL / VK_LCONTROL / VK_RCONTROL
            0x10 or 0xA0 or 0xA1 => ModifierKey.Shift,    // VK_SHIFT / VK_LSHIFT / VK_RSHIFT
            0x12 or 0xA4 or 0xA5 => ModifierKey.Alt,      // VK_MENU / VK_LMENU / VK_RMENU
            _ => ModifierKey.None,
        };

        return modifier != ModifierKey.None;
    }

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Control)
        {
            parts.Add("Ctrl");
        }

        if (Shift)
        {
            parts.Add("Shift");
        }

        if (Alt)
        {
            parts.Add("Alt");
        }

        parts.Add(KeyName(VirtualKey));
        return string.Join('+', parts);
    }

    private static bool TryParseKey(string name, out ushort virtualKey)
    {
        virtualKey = 0;

        // A〜Z / 0〜9 は仮想キーコードが ASCII と一致する。
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]))
        {
            virtualKey = char.ToUpperInvariant(name[0]);
            return true;
        }

        // F1〜F24
        if (name.Length is >= 2 and <= 3
            && (name[0] is 'F' or 'f')
            && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number is >= 1 and <= 24)
        {
            virtualKey = (ushort)(0x70 + number - 1);
            return true;
        }

        return NamedKeys.TryGetValue(name, out virtualKey);
    }

    private static string KeyName(ushort virtualKey)
    {
        if (virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"F{virtualKey - 0x70 + 1}";
        }

        foreach (var (name, code) in NamedKeys)
        {
            if (code == virtualKey)
            {
                return name;
            }
        }

        return $"0x{virtualKey:X2}";
    }
}

/// <summary>修飾キーの種類。</summary>
public enum ModifierKey
{
    None,
    Control,
    Shift,
    Alt,
}
