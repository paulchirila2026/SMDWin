using SMDWin.Views;
using Xunit;

namespace SMDWin.Tests
{
    /// <summary>The "winget upgrade" table is read by column position, whatever the language of the header.</summary>
    public class WingetTableTests
    {
        private const string Sample =
            "   - \r   \\ \r" +
            "Nume                       ID                       Versiune      Disponibil    Sursă\n" +
            "------------------------------------------------------------------------------------------\n" +
            "Google Chrome              Google.Chrome            156.0.8078.80 157.0.7151.41 winget\n" +
            "7-Zip 23.01 (x64)          7zip.7zip                23.01         24.09         winget\n" +
            "Mozilla Firefox (x64 ro)   Mozilla.Firefox.ro       131.0         132.0.1       winget\n" +
            "3 actualizări disponibile.\n";

        [Fact]
        public void ReadsAllRows()
        {
            var t = MainWindow.ParseWingetTable(Sample);
            Assert.Equal(3, t.Count);
            Assert.Equal("157.0.7151.41", t["Google.Chrome"].Available);
            Assert.Equal("23.01", t["7zip.7zip"].Installed);
            Assert.Equal("Mozilla Firefox (x64 ro)", t["Mozilla.Firefox.ro"].Name);
        }

        [Fact]
        public void NoTableGivesEmpty()
        {
            Assert.Empty(MainWindow.ParseWingetTable("No installed package found matching input criteria."));
        }
    }
}
