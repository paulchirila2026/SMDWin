using System.Collections.Generic;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    /// <summary>Checking the language packs: the placeholders must be the same as in English.</summary>
    public class LanguageAuditTests
    {
        [Fact]
        public void Placeholders_AreReadWithTheirFormat_AndWithoutRepeats()
        {
            Assert.Equal("0,1", string.Join(",", LanguageAudit.Placeholders("{0} drive has only {1} GB free")));
            Assert.Equal("0,2", string.Join(",", LanguageAudit.Placeholders("{2:F1} of {0,5} and again {0}")));
            Assert.Equal("", string.Join(",", LanguageAudit.Placeholders("no placeholders, {braces} only")));
            Assert.Equal("", string.Join(",", LanguageAudit.Placeholders(null)));
        }

        [Fact]
        public void Mismatches_AreFound_WhenATranslationUsesOtherPlaceholders()
        {
            var english = new Dictionary<string, string>
            {
                ["Notif.DiskSpaceMsg"] = "{0} drive has only {1} GB free.",
                ["Common.Ok"] = "OK",
                ["Apps.Count"] = "{0} programs",
                ["Only.English"] = "{0}",
            };
            var german = new Dictionary<string, string>
            {
                ["Notif.DiskSpaceMsg"] = "Laufwerk C: hat nur noch {0} GB frei.",     // {1} is missing: the values would appear wrong
                ["Common.Ok"] = "OK",
                ["Apps.Count"] = "Programme: {0}",                                    // a different word order, the same placeholders
            };
            var bad = LanguageAudit.PlaceholderMismatches(english, german);
            Assert.Equal("Notif.DiskSpaceMsg", string.Join(",", bad));
        }
    }
}
