using System.Globalization;
using System.Text;

namespace NorthIsland.Server.Net;

/// <summary>Makes text safe to write to a log line: text a client sent may hold line breaks, terminal codes or bidirectional overrides.</summary>
public static class LogText
{
    /// <summary>
    /// Escapes the characters that could break a log line or change how it shows: control characters (line breaks and terminal escapes among
    /// them), the line and paragraph separators and the bidirectional embedding, override and isolate marks, each as <c>\uXXXX</c>.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The text with those characters escaped; the same instance when it has none.</returns>
    public static string Printable(string text)
    {
        var first = text.AsSpan().IndexOfAny(Unsafe);
        if (first < 0)
            return text;

        var sb = new StringBuilder(text.Length + 16); // room for a few escapes
        sb.Append(text, 0, first);
        foreach (var c in text.AsSpan(first))
        {
            if (Unsafe.Contains(c))
                sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The characters <see cref="Printable"/> escapes.</summary>
    private static readonly System.Buffers.SearchValues<char> Unsafe = System.Buffers.SearchValues.Create(UnsafeChars());

    /// <summary>
    /// Lists the characters to escape: C0 controls, DEL, C1 controls, U+2028/U+2029 and the bidi marks U+202A..U+202E and U+2066..U+2069.
    /// </summary>
    /// <returns>The characters.</returns>
    private static string UnsafeChars()
    {
        var sb = new StringBuilder();
        for (var c = '\u0000'; c <= '\u001F'; c++) // C0 controls
            sb.Append(c);
        for (var c = '\u007F'; c <= '\u009F'; c++) // DEL and the C1 controls
            sb.Append(c);
        sb.Append('\u2028').Append('\u2029'); // line and paragraph separators
        for (var c = '\u202A'; c <= '\u202E'; c++) // bidi embeddings and overrides
            sb.Append(c);
        for (var c = '\u2066'; c <= '\u2069'; c++) // bidi isolates
            sb.Append(c);
        return sb.ToString();
    }
}
