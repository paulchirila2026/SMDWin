using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // Texts written directly in XAML are translated by their English text: the key is made from it.
    public class LiteralTextsTests
    {
        [Theory]
        [InlineData("How SMD Win looks", "how_smd_win_looks")]
        [InlineData("WIDGET & TRAY", "widget_tray")]
        [InlineData("Today's log, to send with a bug report", "today_s_log_to_send_with_a_bug_report")]
        [InlineData("Everything 10 % smaller, so more fits on the screen", "everything_10_smaller_so_more_fits_on_the_screen")]
        [InlineData("  Open  ", "open")]
        public void Key_IsLowercaseWordsJoinedByUnderscore(string english, string expected)
            => Assert.Equal(expected, LiteralTexts.Key(english));

        [Fact]
        public void Key_LongText_IsCutAtSixtyCharacters_WithoutTrailingUnderscore()
        {
            string key = LiteralTexts.Key("Running as standard user. Some sensors and SMART data require Administrator privileges.");
            Assert.Equal("running_as_standard_user_some_sensors_and_smart_data_require", key);
            Assert.True(key.Length <= 60);
            Assert.False(key.EndsWith("_"));
        }

        [Theory]
        [InlineData("Interface density", true)]
        [InlineData("Open", true)]
        [InlineData("Adds a registry entry to HKCU\\Run.", true)]
        [InlineData("0.5s", false)]
        [InlineData("10s", false)]
        [InlineData("85°C", false)]
        [InlineData("°C", false)]
        [InlineData("drp.su", false)]
        [InlineData("Google", false)]
        [InlineData("%AppData%\\SMDWin\\logs\\sensors-DATE.csv", false)]
        [InlineData("", false)]
        [InlineData("—", false)]
        [InlineData("CPU", false)]
        [InlineData("Secure Boot", false)]
        [InlineData("chkdsk /scan", false)]
        [InlineData("⚠ BD PROCHOT", false)]
        [InlineData("Temp & Stress", false)]
        [InlineData("CPU + RAM + SSD Benchmark", true)]
        [InlineData("SMART health", true)]
        public void IsTranslatable_SkipsValuesUnitsAndNames(string text, bool expected)
            => Assert.Equal(expected, LiteralTexts.IsTranslatable(text));

        [Fact]
        public void Translate_WithoutAKey_KeepsTheEnglishText()
        {
            // the test language pack has no section: the text stays English, not "[PageText.key]"
            Assert.Equal("Interface density", LiteralTexts.Translate("Interface density"));
            Assert.Equal("", LiteralTexts.Translate(""));
        }

        // a small language pack, for the tests below
        private static string? Ro(string key) => key switch
        {
            "memory" => "Memorie",
            "read" => "citire",
            "free" => "liberi /",
            "smart_health" => "Sănătate SMART",
            "uptime" => "Pornit de",
            "move_to_recycle_bin" => "🗑  Mută în Coșul de reciclare",
            "download" => "Download",
            _ => null,
        };

        [Fact]
        public void Translate_OneKey_ServesEverySpellingOfTheText()
        {
            Assert.Equal("MEMORIE", LiteralTexts.Translate("MEMORY", "ro", Ro));
            Assert.Equal("Memorie", LiteralTexts.Translate("Memory", "ro", Ro));
            Assert.Equal("memorie", LiteralTexts.Translate("memory", "ro", Ro));
            Assert.Equal("Citire", LiteralTexts.Translate("Read", "ro", Ro));
            Assert.Equal("citire", LiteralTexts.Translate("read", "ro", Ro));
            Assert.Equal("PORNIT DE", LiteralTexts.Translate("UPTIME", "ro", Ro));
            Assert.Equal("Pornit de", LiteralTexts.Translate("Uptime", "ro", Ro));
        }

        [Fact]
        public void Translate_KeepsTheSpacesAroundTheText_AndLeavesEnglishAlone()
        {
            Assert.Equal(" liberi / ", LiteralTexts.Translate(" free / ", "ro", Ro));
            Assert.Equal("   Memorie", LiteralTexts.Translate("   Memory", "ro", Ro));
            // the symbols in front of the text stay as they are in the translation
            Assert.Equal("🗑  Mută în Coșul de reciclare", LiteralTexts.Translate("🗑  Move to Recycle Bin", "ro", Ro));
            // in English the page text is not touched, even if the key exists
            Assert.Equal("MEMORY", LiteralTexts.Translate("MEMORY", "en", Ro));
            // no key: the English text
            Assert.Equal("Something else", LiteralTexts.Translate("Something else", "ro", Ro));
        }

        [Fact]
        public void MatchCase_KeepsAcronyms_AndGermanNouns()
        {
            // the leading abbreviation is not lowercased
            Assert.Equal("SMART sănătate", LiteralTexts.MatchCase("health of SMART", "SMART sănătate", "ro"));
            Assert.Equal("Sănătate SMART", LiteralTexts.Translate("SMART health", "ro", Ro));
            // in German nouns keep their capital letter, even if the English text is lowercase
            Assert.Equal("Download", LiteralTexts.MatchCase("download", "Download", "de"));
            Assert.Equal("download", LiteralTexts.MatchCase("download", "Download", "fr"));
            Assert.Equal("DOWNLOAD", LiteralTexts.MatchCase("DOWNLOAD", "Download", "de"));
            // text without letters or an empty translation: unchanged
            Assert.Equal("", LiteralTexts.MatchCase("Read", "", "ro"));
            Assert.Equal("12 %", LiteralTexts.MatchCase("12 %", "12 %", "ro"));
        }
    }
}
