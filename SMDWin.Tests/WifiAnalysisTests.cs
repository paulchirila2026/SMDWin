using System.Collections.Generic;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    public class WifiAnalysisTests
    {
        private static WifiAp Ap(string ssid, string band, int ch, int width, int rssi, string bssid = "00:11:22:33:44:55")
        {
            int f = WifiAnalysis.FrequencyOf(band, ch);
            int center = band == "2.4" && width == 40 ? f + 10 : f;
            return new WifiAp { Ssid = ssid, Band = band, Channel = ch, WidthMHz = width, Rssi = rssi, Bssid = bssid,
                                FrequencyMHz = f, LowMHz = center - width / 2, HighMHz = center + width / 2 };
        }

        [Theory]
        [InlineData(2412, "2.4", 1)]
        [InlineData(2437, "2.4", 6)]
        [InlineData(2484, "2.4", 14)]
        [InlineData(5180, "5", 36)]
        [InlineData(5500, "5", 100)]
        [InlineData(6135, "6", 37)]
        public void ChannelOf_Frequency(int mhz, string band, int ch)
        {
            Assert.Equal((band, ch), WifiAnalysis.ChannelOf(mhz));
        }

        [Fact]
        public void Channels_1_6_11_DoNotOverlap_But_1_And_3_Do()
        {
            var c1 = Ap("a", "2.4", 1, 20, -50); var c6 = Ap("b", "2.4", 6, 20, -50); var c3 = Ap("c", "2.4", 3, 20, -50);
            Assert.Equal(0, WifiAnalysis.OverlapMHz(c1, c6));
            Assert.True(WifiAnalysis.OverlapMHz(c1, c3) > 0);
        }

        [Fact]
        public void Interference_SplitsCoChannelAndPartialOverlap()
        {
            var me = Ap("Home", "2.4", 6, 20, -45, "a4:2b:b0:1c:7e:10");
            var list = new List<WifiAp> { me, Ap("Same", "2.4", 6, 20, -60, "11:11:11:11:11:11"), Ap("Near", "2.4", 4, 20, -70, "22:22:22:22:22:22"),
                                          Ap("Far", "2.4", 11, 20, -60, "33:33:33:33:33:33"), Ap("Home", "2.4", 1, 20, -70, "a4:2b:b0:1c:9a:22") };
            var (co, adj) = WifiAnalysis.Interference(list, me);
            Assert.Single(co); Assert.Equal("Same", co[0].Ssid);
            Assert.Single(adj); Assert.Equal("Near", adj[0].Ssid);
        }

        [Fact]
        public void Best_On24_PicksTheQuietestOf_1_6_11()
        {
            var me = Ap("Home", "2.4", 6, 20, -45, "a4:2b:b0:1c:7e:10");
            var list = new List<WifiAp> { me, Ap("x", "2.4", 1, 20, -50, "11:11:11:11:11:11"), Ap("y", "2.4", 6, 20, -55, "22:22:22:22:22:22"), Ap("z", "2.4", 11, 20, -88, "33:33:33:33:33:33") };
            Assert.Equal(11, WifiAnalysis.Best(list, "2.4", me)[0].ch);
        }

        [Theory]
        [InlineData("Wi-Fi 6", 80, 2, 1201)]
        [InlineData("Wi-Fi 5", 80, 2, 867)]
        [InlineData("Wi-Fi 4", 20, 2, 144)]
        public void MaxRate_KnownValues(string std, int width, int streams, int expected)
        {
            Assert.Equal(expected, WifiAnalysis.MaxRate(std, width, streams));
        }

        [Fact]
        public void ParseIes_ReadsWidthStandardAndSecurity()
        {
            // 5 GHz, channel 36, VHT operation 80 MHz centred on 42, HE capabilities, RSN with SAE + PSK and PMF optional
            var ap = new WifiAp { Band = "5", Channel = 36, FrequencyMHz = 5180 };
            var ie = new List<byte>();
            ie.AddRange(new byte[] { 192, 5, 1, 42, 0, 0, 0 });                                          // VHT operation
            var he = new byte[1 + 6 + 11 + 4]; he[0] = 35; he[18] = 0xFA; he[19] = 0xFF;                  // HE caps, Rx map: 2 streams
            ie.Add(255); ie.Add((byte)he.Length); ie.AddRange(he);
            ie.AddRange(new byte[] { 48, 24, 1, 0, 0, 0x0F, 0xAC, 4, 1, 0, 0, 0x0F, 0xAC, 4, 2, 0, 0, 0x0F, 0xAC, 2, 0, 0x0F, 0xAC, 8, 0x80, 0 });   // RSN
            WifiScanner.ParseIes(ap, ie.ToArray(), 0x11, 0);
            Assert.Equal(80, ap.WidthMHz);
            Assert.Equal(5170, ap.LowMHz); Assert.Equal(5250, ap.HighMHz);
            Assert.Equal("Wi-Fi 6", ap.Standard);
            Assert.Equal(2, ap.Streams);
            Assert.Equal("WPA2/WPA3", ap.Security);
            Assert.Equal("optional", ap.Pmf);
        }
    }
}
