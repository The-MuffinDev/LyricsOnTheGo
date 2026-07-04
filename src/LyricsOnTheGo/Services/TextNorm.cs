using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LyricsOnTheGo.Services;

/// <summary>
/// Port of the LRCLIB server's prepare_input normalization: unaccent, punctuation to spaces,
/// strip apostrophes, lowercase, collapse whitespace. Must match the server exactly — the local
/// dump's name_lower/artist_name_lower columns store text normalized this way, so the
/// equality-based exact query only hits when we produce identical strings. Also provides
/// token-level helpers for browser mode's title-containment ranking.
/// </summary>
public static class TextNorm
{
    private static readonly HashSet<char> PunctToSpace = new(new[]
    {
        '`', '~', '!', '@', '#', '$', '%', '^', '&', '*', '(', ')', '_', '|', '+', '-', '=', '?',
        ';', ':', '"', ',', '.', '<', '>', '{', '}', '[', ']', '\\', '/', ' ', '\n',
    });

    public static string Normalize(string input)
    {
        if (string.IsNullOrEmpty(input))
            return "";

        string deburred = RemoveDiacritics(input);
        var sb = new StringBuilder(deburred.Length);
        foreach (char c in deburred)
        {
            if (c == '\'' || c == '’')
                continue; // apostrophes are removed, not spaced
            sb.Append(PunctToSpace.Contains(c) ? ' ' : c);
        }

        return CollapseWhitespace(sb.ToString().ToLowerInvariant());
    }

    /// <summary>Normalized token set of a string, for order-insensitive containment checks.</summary>
    public static HashSet<string> TokenSet(string s)
    {
        var set = new HashSet<string>();
        foreach (var token in Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            set.Add(token);
        return set;
    }

    /// <summary>True when every normalized token of <paramref name="entryName"/> appears in
    /// <paramref name="titleTokens"/>. Browser videos almost always embed the real track name in
    /// the title, so a candidate whose name isn't contained is likely a different song that FTS
    /// ranked high on decoration words ("Official Music Video") or artist tokens alone.</summary>
    public static bool TitleContains(HashSet<string> titleTokens, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
            return false;

        var tokens = Normalize(entryName).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        foreach (var t in tokens)
            if (!titleTokens.Contains(t))
                return false;
        return true;
    }

    private static string RemoveDiacritics(string text)
    {
        string norm = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(norm.Length);
        foreach (char c in norm)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool prevSpace = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!prevSpace && sb.Length > 0)
                    sb.Append(' ');
                prevSpace = true;
            }
            else
            {
                sb.Append(c);
                prevSpace = false;
            }
        }
        if (sb.Length > 0 && sb[^1] == ' ')
            sb.Length--;
        return sb.ToString();
    }
}
