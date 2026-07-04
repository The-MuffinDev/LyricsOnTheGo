using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LyricsOnTheGo.Services;

/// <summary>
/// Cleans YouTube-style video titles for lyric search: strips bracketed decorations like
/// "(Official Video)" or "[Letra]" and pipe-separated decoration segments, then collapses the
/// leftovers. It never splits artist from title — the cleaned string is searched whole as free
/// text, since LRCLIB's FTS matches its words across both the name and artist columns.
/// </summary>
public static class TitleCleaner
{
    // Words that mark a bracketed group / pipe segment as decoration rather than song content.
    private const string DecorWords =
        @"(official|oficial|video|audio|lyric\w*|letra|visuali[sz]er|m/v|mv|hd|hq|4k|8k" +
        @"|remaster\w*|explicit|videoclip|karaoke|sub\w*|topic)";

    // "(…official…)", "[…letra…]", "{…mv…}" — removed only when a decoration word appears inside.
    private static readonly Regex Bracketed = new(
        $@"[(\[{{][^()\[\]{{}}]*\b{DecorWords}\b[^()\[\]{{}}]*[)\]}}]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DecorSegment = new(
        $@"\b{DecorWords}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Bare trailing decorations without brackets ("Song M/V", "Song Official Audio"): if they
    // survived, PrepareInput would turn "m/v" into the FTS terms "m" AND "v", killing the match.
    private static readonly Regex TrailingDecor = new(
        $@"(\s+{DecorWords})+\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MultiSpace = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>Returns the cleaned title, or "" when nothing usable remains.</summary>
    public static string Clean(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";

        string s = Bracketed.Replace(title, " ");

        // Pipe-separated decoration ("Song | Official Video"): drop only the segments containing
        // a decoration word — some channels use '|' between artist and song, which must survive.
        if (s.IndexOf('|') >= 0)
        {
            var kept = new List<string>();
            foreach (var part in s.Split('|'))
                if (!DecorSegment.IsMatch(part))
                    kept.Add(part.Trim());
            if (kept.Count > 0)
                s = string.Join(" ", kept);
        }

        s = TrailingDecor.Replace(s, " ");
        s = MultiSpace.Replace(s, " ").Trim();
        return s.Trim(' ', '-', '–', '—', '|');
    }
}
