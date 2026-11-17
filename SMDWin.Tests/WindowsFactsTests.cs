using System;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    /// <summary>The Windows info window: version support, antivirus state, how many protections are active.</summary>
    public class WindowsFactsTests
    {
        [Fact]
        public void Support_Windows10_NoLongerGetsUpdates()
        {
            Assert.Equal(WindowsSupport.Windows10End, WindowsFacts.SupportFor(19045, "Professional"));
            Assert.Equal(WindowsSupport.Windows10End, WindowsFacts.SupportFor(10240, "Core"));
            Assert.Equal(WindowsSupport.Supported, WindowsFacts.SupportFor(22631, "Professional"));
            Assert.Equal(WindowsSupport.Supported, WindowsFacts.SupportFor(26100, "Core"));
            Assert.Equal(WindowsSupport.LegacyEnd, WindowsFacts.SupportFor(9600, "Professional"));      // Windows 8.1
            Assert.Equal(WindowsSupport.LegacyEnd, WindowsFacts.SupportFor(7601, "Ultimate"));          // Windows 7
        }

        [Fact]
        public void Support_LongTermAndServerEditions_AreNotJudged()
        {
            Assert.Equal(WindowsSupport.Supported, WindowsFacts.SupportFor(19044, "EnterpriseS"));        // LTSC 2021
            Assert.Equal(WindowsSupport.Supported, WindowsFacts.SupportFor(19044, "IoTEnterpriseS"));
            Assert.Equal(WindowsSupport.Supported, WindowsFacts.SupportFor(17763, "ServerStandard"));
            Assert.Equal(WindowsSupport.Supported, WindowsFacts.SupportFor(0, "Professional"));           // build necitit
            Assert.Equal(WindowsSupport.Windows10End, WindowsFacts.SupportFor(19045, null));
        }

        [Fact]
        public void Antivirus_State_IsReadFromTheProductCode()
        {
            Assert.Equal((true, true), WindowsFacts.AntivirusState(397568));      // 0x061100: Defender on, up to date
            Assert.Equal((false, true), WindowsFacts.AntivirusState(393472));     // 0x060100: Defender oprit
            Assert.Equal((true, false), WindowsFacts.AntivirusState(397584));     // 0x061110: on, old signatures
            Assert.Equal((true, true), WindowsFacts.AntivirusState(266240));      // 0x041000: alt antivirus, pornit
            Assert.Equal((false, false), WindowsFacts.AntivirusState(262160));    // 0x040010: off, old signatures
        }

        [Fact]
        public void Antivirus_TheOneActuallyRunning_IsShown()
        {
            var defenderOff = new AntivirusProduct("Windows Defender", false, true);
            var other = new AntivirusProduct("ESET Security", true, true);
            Assert.Equal(other, WindowsFacts.ActiveAntivirus(new[] { defenderOff, other }));
            Assert.Equal(defenderOff, WindowsFacts.ActiveAntivirus(new[] { defenderOff }));       // none on: the first one
            Assert.Null(WindowsFacts.ActiveAntivirus(Array.Empty<AntivirusProduct>()));
            Assert.Null(WindowsFacts.ActiveAntivirus(new[] { new AntivirusProduct("  ", true, true) }));
        }

        [Fact]
        public void Protection_Score_CountsOnlyWhatWasRead()
        {
            Assert.Equal((3, 3), WindowsFacts.ProtectionScore(true, true, true));
            Assert.Equal((1, 3), WindowsFacts.ProtectionScore(true, false, false));
            Assert.Equal((2, 2), WindowsFacts.ProtectionScore(true, null, true));      // an unread protection is not counted
            Assert.Equal((0, 0), WindowsFacts.ProtectionScore(null, null));

            Assert.True(WindowsFacts.BitLockerOn(1) == true);
            Assert.True(WindowsFacts.BitLockerOn(0) == false);
            Assert.Null(WindowsFacts.BitLockerOn(2));
        }
    }
}
