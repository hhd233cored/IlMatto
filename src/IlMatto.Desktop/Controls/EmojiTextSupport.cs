using System.Text;

namespace IlMatto.Desktop.Controls;

/// <summary>
/// Detects the Unicode ranges that need Emoji.Wpf's color-glyph path.
/// CJK punctuation deliberately does not qualify: treating characters such as
/// 、 and 。 as emoji makes ordinary Chinese prose needlessly expensive.
/// </summary>
internal static class EmojiTextSupport
{
    public static bool ContainsEmoji(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (IsEmojiScalar(rune.Value)) return true;
        }
        return false;
    }

    private static bool IsEmojiScalar(int value) =>
        value is >= 0x1F000 and <= 0x1FAFF ||
        value is 0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139 or
            0x231A or 0x231B or 0x2328 or 0x23CF or 0x24C2 or 0x3030 or
            0x303D or 0x3297 or 0x3299 ||
        value is >= 0x23E9 and <= 0x23FA ||
        value is >= 0x25AA and <= 0x25AB ||
        value is 0x25B6 or 0x25C0 ||
        value is >= 0x25FB and <= 0x25FE ||
        value is >= 0x2600 and <= 0x27BF ||
        value is >= 0x2B05 and <= 0x2B07 ||
        value is 0x2B1B or 0x2B1C or 0x2B50 or 0x2B55;
}
