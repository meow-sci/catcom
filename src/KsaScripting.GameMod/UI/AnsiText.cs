using System.Text;

namespace KsaScripting.GameMod.UI;

/// <summary>
///     Turns the raw bytes of a script terminal's output ring buffer into plain text for the
///     Script Manager's mini preview: UTF-8 is decoded leniently and every escape sequence (CSI, OSC, DCS,
///     APC, PM, SOS, two-byte escapes) and control byte is removed. Screen-clearing and cursor-positioning
///     sequences are interpreted just enough to keep a full-screen app readable: a clear starts the text
///     over, a cursor move starts a new line. Pure and allocation-light; never throws.
/// </summary>
internal static class AnsiText
{
    /// <summary>
    ///     Strips <paramref name="bytes"/> and keeps at most the last <paramref name="maxChars"/> characters
    ///     and <paramref name="maxLines"/> lines.
    /// </summary>
    public static string Strip(ReadOnlySpan<byte> bytes, int maxChars = 2048, int maxLines = 60)
    {
        try
        {
            return StripText(Encoding.UTF8.GetString(bytes), maxChars, maxLines);
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>Strips already-decoded text (see <see cref="Strip"/>).</summary>
    public static string StripText(string text, int maxChars = 2048, int maxLines = 60)
    {
        var sb = new StringBuilder(Math.Min(text.Length, 8192));
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\u001b')
            {
                i = SkipEscape(text, i, sb);
                continue;
            }

            switch (c)
            {
                case '\n':
                    sb.Append('\n');
                    break;
                case '\t':
                    sb.Append("    ");
                    break;
                case '\r':
                case '\u0007':
                case '\b':
                    break;
                default:
                    if (c >= ' ' && c != '\u007f' && (c < '\u0080' || c > '\u009f'))
                    {
                        sb.Append(c);
                    }

                    break;
            }

            i++;
        }

        return Trim(sb.ToString(), maxChars, maxLines);
    }

    /// <summary>Consumes the escape sequence that starts at <paramref name="start"/> and returns the index after it.</summary>
    private static int SkipEscape(string s, int start, StringBuilder sb)
    {
        int i = start + 1;
        if (i >= s.Length)
        {
            return i;
        }

        char kind = s[i];
        switch (kind)
        {
            case '[':
            {
                // CSI: parameters 0x30-0x3F, intermediates 0x20-0x2F, one final byte 0x40-0x7E.
                int p = i + 1;
                int paramStart = p;
                while (p < s.Length && s[p] >= ' ' && s[p] <= '?')
                {
                    p++;
                }

                if (p >= s.Length)
                {
                    return p;
                }

                char final = s[p];
                string parameters = s.Substring(paramStart, p - paramStart);
                switch (final)
                {
                    case 'J' when parameters is "2" or "3":
                        sb.Clear();
                        break;
                    case 'H':
                    case 'f':
                    case 'E':
                    case 'B':
                    case 'd':
                        NewLineIfNeeded(sb);
                        break;
                }

                return p + 1;
            }

            case ']':
            case 'P':
            case '_':
            case '^':
            case 'X':
            {
                // String sequences end with ST (ESC \); OSC may also end with BEL.
                int p = i + 1;
                while (p < s.Length)
                {
                    if (s[p] == '\u0007' && kind == ']')
                    {
                        return p + 1;
                    }

                    if (s[p] == '\u001b' && p + 1 < s.Length && s[p + 1] == '\\')
                    {
                        return p + 2;
                    }

                    p++;
                }

                return p;
            }

            default:
            {
                // ESC <intermediates 0x20-0x2F>* <final 0x30-0x7E>, e.g. "ESC ( B" or "ESC 7".
                int p = i;
                while (p < s.Length && s[p] >= ' ' && s[p] <= '/')
                {
                    p++;
                }

                return p < s.Length ? p + 1 : p;
            }
        }
    }

    private static void NewLineIfNeeded(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[sb.Length - 1] != '\n')
        {
            sb.Append('\n');
        }
    }

    private static string Trim(string text, int maxChars, int maxLines)
    {
        string[] lines = text.Split('\n');
        int first = 0;
        int last = lines.Length - 1;
        while (last >= first && lines[last].AsSpan().Trim().IsEmpty)
        {
            last--;
        }

        while (first <= last && lines[first].AsSpan().Trim().IsEmpty)
        {
            first++;
        }

        if (last - first + 1 > maxLines)
        {
            first = last - maxLines + 1;
        }

        var sb = new StringBuilder();
        for (int i = first; i <= last; i++)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(lines[i].TrimEnd());
        }

        if (sb.Length > maxChars)
        {
            sb.Remove(0, sb.Length - maxChars);
        }

        return sb.ToString();
    }
}
