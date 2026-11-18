using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    public class EventSubscriptionAuditorTests
    {
        [Fact]
        public void BalancedFile_IsBalanced()
        {
            string src = "ThemeManager.ThemeChanged += OnThemeChanged;\n" +
                         "Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;\n";
            var counts = EventSubscriptionAuditor.CountSubscriptions(src, "ThemeManager.ThemeChanged");

            Assert.True(counts.IsBalanced);
            Assert.Equal(1, counts.SubscribeCount);
            Assert.Equal(1, counts.UnsubscribeCount);
        }

        [Fact]
        public void SubscribeWithoutUnsubscribe_IsNotBalanced()
        {
            string src = "ThemeManager.ThemeChanged += OnThemeChanged;\n";
            var counts = EventSubscriptionAuditor.CountSubscriptions(src, "ThemeManager.ThemeChanged");

            Assert.False(counts.IsBalanced);
            Assert.Equal(1, counts.SubscribeCount);
            Assert.Equal(0, counts.UnsubscribeCount);
        }

        [Fact]
        public void EmptySource_IsBalanced_NothingToLeak()
        {
            var counts = EventSubscriptionAuditor.CountSubscriptions("", "ThemeManager.ThemeChanged");
            Assert.True(counts.IsBalanced);
        }

        [Fact]
        public void MultipleWindowsInSameFile_CountsSeparately()
        {
            string src =
                "ThemeManager.ThemeChanged += OnThemeChanged;\n" +
                "ThemeManager.ThemeChanged += OnThemeChanged2;\n" +
                "ThemeManager.ThemeChanged -= OnThemeChanged;\n";
            var counts = EventSubscriptionAuditor.CountSubscriptions(src, "ThemeManager.ThemeChanged");

            Assert.Equal(2, counts.SubscribeCount);
            Assert.Equal(1, counts.UnsubscribeCount);
            Assert.False(counts.IsBalanced); // the second handler never unsubscribes
        }
    }

    /// <summary>
    /// A REAL audit of the project source — not invented data. Scans every .cs file in views/ and checks the
    /// subscribe/unsubscribe symmetry for ThemeManager.ThemeChanged. MainWindow.cs is excluded on purpose — it is the main
    /// window, it lives as long as the app, so it does not have (and does not need) an unsubscribe.
    ///
    /// Runs ONLY here, on `dotnet test`, on the development machine — the .cs source does not exist on a client computer where
    /// the in-app self-test runs (that one works only with the compiled binary). This kind of code convention check cannot and
    /// must not go into SelfTestService/MainWindow.SelfTest.cs.
    /// </summary>
    public class ThemeChangedSubscriptionSourceAuditTests
    {
        // Windows that live as long as the app — they do not need to unsubscribe from a static event, because they never close
        // before it.
        // MainWindow.*.cs are parts of the same window (partial class) and ButtonContrastGuard is installed once for the app.
        private static readonly string[] _exemptFiles = { "MainWindow.cs", "ButtonContrastGuard.cs" };
        private static bool IsExempt(string fileName) =>
            System.Array.IndexOf(_exemptFiles, fileName) >= 0 || fileName.StartsWith("MainWindow.", System.StringComparison.Ordinal);
        // a subscription meant to last as long as the app is marked on its line and not counted
        private const string AppLifetimeMarker = "[app-lifetime]";

        private static string GetViewsFolder([CallerFilePath] string thisTestFilePath = "")
        {
            // This test file is in <root>/SMDWin.Tests/...cs — go one level up, to the project root, then into views/.
            string testProjectDir = Path.GetDirectoryName(thisTestFilePath)!;
            string projectRoot = Path.GetFullPath(Path.Combine(testProjectDir, ".."));
            return Path.Combine(projectRoot, "views");
        }

        [Fact]
        public void AllNonExemptViews_HaveBalancedThemeChangedSubscriptions()
        {
            string viewsFolder = GetViewsFolder();
            Assert.True(Directory.Exists(viewsFolder), $"folderul views/ nu a fost gasit la {viewsFolder} — structura proiectului s-a schimbat? actualizeaza GetViewsFolder().");

            var unbalancedFiles = new System.Collections.Generic.List<string>();

            foreach (var file in Directory.GetFiles(viewsFolder, "*.cs", SearchOption.AllDirectories))
            {
                string fileName = Path.GetFileName(file);
                if (IsExempt(fileName)) continue;

                string content = string.Join("\n", File.ReadAllLines(file).Where(l => !l.Contains(AppLifetimeMarker)));
                var counts = EventSubscriptionAuditor.CountSubscriptions(content, "ThemeManager.ThemeChanged");

                // Files that do not touch ThemeChanged at all (most of them) are automatically balanced (0 == 0) — only those that DO
                // subscribe matter.
                if (!counts.IsBalanced)
                    unbalancedFiles.Add($"{fileName}: += {counts.SubscribeCount}, -= {counts.UnsubscribeCount}");
            }

            Assert.True(unbalancedFiles.Count == 0,
                "Ferestre cu abonare/dezabonare NEECHILIBRATA la ThemeManager.ThemeChanged " +
                "(risc de leak — fereastra inchisa ramane vie prin evenimentul static, sau " +
                "handler-ul ei ruleaza dupa ce fereastra s-a inchis):\n" +
                string.Join("\n", unbalancedFiles) +
                "\n\nDaca fereastra noua traieste intentionat cat aplicatia (ca MainWindow), " +
                "adaug-o in _exemptFiles de mai sus. Altfel, adauga " +
                "`Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;` langa abonare.");
        }
    }
}
