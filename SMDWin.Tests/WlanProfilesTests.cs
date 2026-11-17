using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    public class WlanProfilesTests
    {
        private const string Ns = "http://www.microsoft.com/networking/WLAN/profile/v1";

        private static string Xml(string auth, string enc, string? key, bool isProtected) => $@"<?xml version=""1.0""?>
<WLANProfile xmlns=""{Ns}"">
  <name>Acasa</name>
  <SSIDConfig><SSID><hex>4163617361</hex><name>Acasa 5G</name></SSID></SSIDConfig>
  <MSM><security>
    <authEncryption><authentication>{auth}</authentication><encryption>{enc}</encryption><useOneX>false</useOneX></authEncryption>
    {(key == null ? "" : $"<sharedKey><keyType>passPhrase</keyType><protected>{(isProtected ? "true" : "false")}</protected><keyMaterial>{key}</keyMaterial></sharedKey>")}
  </security></MSM>
</WLANProfile>";

        [Fact]
        public void PlainKey_IsReadWithFriendlyAuth()
        {
            var p = WlanProfiles.Parse("Acasa", Xml("WPA2PSK", "AES", "parola123", false));
            Assert.Equal("Acasa 5G", p.Ssid);
            Assert.Equal("parola123", p.Password);
            Assert.Equal("WPA2-Personal", p.Auth);
            Assert.Equal("AES", p.Cipher);
        }

        [Fact]
        public void ProtectedKey_IsNotShown()
        {
            var p = WlanProfiles.Parse("Acasa", Xml("WPA3SAE", "AES", "01000000D08C9DDF0115D1118C7A00C04FC297EB", true));
            Assert.Equal("—", p.Password);
            Assert.Equal("WPA3-Personal", p.Auth);
        }

        [Fact]
        public void OpenNetwork_HasNoPasswordOrCipher()
        {
            var p = WlanProfiles.Parse("Cafenea", Xml("open", "none", null, false));
            Assert.Equal("—", p.Password);
            Assert.Equal("Open", p.Auth);
            Assert.Equal("—", p.Cipher);
        }

        [Fact]
        public void BrokenXml_KeepsTheProfileName()
        {
            var p = WlanProfiles.Parse("Birou", "<not xml");
            Assert.Equal("Birou", p.Ssid);
            Assert.Equal("—", p.Password);
        }
    }
}
