using System.Text.Json;
using System.Text.RegularExpressions;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // Every text written as PT("English text") in the code must have a translation in ro / de / fr:
    // otherwise the screen would silently stay in English in those languages.
    public class TranslationCoverageTests
    {
        private static string? FindRoot()
        {
            string? dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "Languages"))) dir = Path.GetDirectoryName(dir);
            return dir;
        }

        [Theory]
        [InlineData("ro")]
        [InlineData("de")]
        [InlineData("fr")]
        public void EveryPtText_HasATranslation(string lang)
        {
            string? root = FindRoot();
            if (root == null) return;   // the sources are not next to the test run
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Languages", lang + ".json")),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var keys = new HashSet<string>();
            if (doc.RootElement.TryGetProperty(LiteralTexts.Section, out var page))
                foreach (var p in page.EnumerateObject()) keys.Add(p.Name);

            var pt = new Regex("\\bPTs?\\(\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
            var missing = new List<string>();
            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                    file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                    file.Contains("SMDWin.Tests")) continue;
                foreach (Match m in pt.Matches(File.ReadAllText(file)))
                {
                    string text = Regex.Unescape(m.Groups[1].Value);
                    if (!LiteralTexts.IsTranslatable(text)) continue;
                    if (!keys.Contains(LiteralTexts.Key(text))) missing.Add(Path.GetFileName(file) + ": " + text);
                }
            }
            Assert.True(missing.Count == 0, lang + " — missing translations:\n" + string.Join("\n", missing.Distinct().Take(30)));
        }
    }
}
