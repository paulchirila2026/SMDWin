using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    /// <summary>The sleep states read from the GetPwrCapabilities answer (the bytes of the SYSTEM_POWER_CAPABILITIES structure).</summary>
    public class PowerCapabilitiesTests
    {
        private static byte[] Caps(bool s3, bool s4, bool hiberFile, bool aoAc)
        {
            var b = new byte[76];
            b[0] = 1;                       // power button
            b[5] = (byte)(s3 ? 1 : 0);
            b[6] = (byte)(s4 ? 1 : 0);
            b[7] = 1;                       // S5 (off) always exists
            b[8] = (byte)(hiberFile ? 1 : 0);
            b[20] = (byte)(aoAc ? 1 : 0);
            return b;
        }

        [Fact]
        public void ClassicLaptop_SleepAndHibernate()
        {
            var c = PowerCapabilities.Parse(Caps(s3: true, s4: true, hiberFile: true, aoAc: false));
            Assert.Equal(new PowerCapabilities(Sleep: true, Hibernate: true, ModernStandby: false), c);
        }

        [Fact]
        public void ModernStandbyLaptop()
        {
            var c = PowerCapabilities.Parse(Caps(s3: false, s4: true, hiberFile: true, aoAc: true));
            Assert.Equal(new PowerCapabilities(false, true, true), c);
        }

        [Fact]
        public void Hibernate_NeedsTheHibernationFile()
        {
            // the board supports hibernation, but it is turned off (powercfg /h off) → not available
            var c = PowerCapabilities.Parse(Caps(s3: true, s4: true, hiberFile: false, aoAc: false));
            Assert.False(c!.Value.Hibernate);
            Assert.True(c.Value.Sleep);
        }

        [Fact]
        public void TooShort_IsNotRead()
        {
            Assert.Null(PowerCapabilities.Parse(new byte[PowerCapabilities.MinBytes - 1]));
            Assert.NotNull(PowerCapabilities.Parse(new byte[PowerCapabilities.MinBytes]));
            Assert.True(PowerCapabilities.BufferBytes >= 76);
        }
    }
}
