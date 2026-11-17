using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SMDWin.Services
{
    /// <summary>Language pack checks, used by the self-test.</summary>
    public static class LanguageAudit
    {
        /// <summary>The placeholders in a text ("{0}", "{1:F1}" → 0, 1), without repeats, in order.</summary>
        public static IReadOnlyList<int> Placeholders(string? text)
            => Regex.Matches(text ?? "", @"\{(\d+)(?:[,:][^}]*)?\}")
                    .Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(n => n).ToList();

        /// <summary>
        /// Keys whose translations do not have the same placeholders as the English text: the code fills them in English order,
        /// so a translation with different placeholders shows the values wrong or not at all.
        /// </summary>
        public static List<string> PlaceholderMismatches(IReadOnlyDictionary<string, string> english,
                                                         IReadOnlyDictionary<string, string> translated)
        {
            var bad = new List<string>();
            foreach (var (key, text) in english)
                if (translated.TryGetValue(key, out var other) && !Placeholders(text).SequenceEqual(Placeholders(other)))
                    bad.Add(key);
            bad.Sort(StringComparer.Ordinal);
            return bad;
        }
    }
}
