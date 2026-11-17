using System;
using System.Linq;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    /// <summary>Computers, motherboards, integrated graphics, peripherals and network: which drawing is chosen and that the
    /// shapes stay inside the box.</summary>
    public class HardwareArtComputersTests
    {
        [Theory]
        [InlineData(ComputerKind.AllInOne, 1, "Apple Inc.", "iMac14,2", false)]
        [InlineData(ComputerKind.MiniPc, 1, "Apple Inc.", "Macmini8,1", false)]
        [InlineData(ComputerKind.Ultrabook, 10, "Apple Inc.", "MacBookAir10,1", true)]
        [InlineData(ComputerKind.Laptop, 10, "LENOVO", "ThinkPad T480", true)]
        [InlineData(ComputerKind.Ultrabook, 10, "Dell Inc.", "XPS 13 9310", true)]
        [InlineData(ComputerKind.Tower, 3, "Dell Inc.", "OptiPlex 7010", false)]
        [InlineData(ComputerKind.SmallDesktop, 4, "Dell Inc.", "OptiPlex 3040", false)]
        [InlineData(ComputerKind.Server, 23, "Dell Inc.", "PowerEdge R740", false)]
        [InlineData(ComputerKind.MiniPc, 35, "Intel", "NUC11PAHi5", false)]
        [InlineData(ComputerKind.Tablet, 30, "Microsoft", "Surface Pro 7", true)]
        public void Computer_KindFollowsChassisThenModel(ComputerKind expected, int chassis, string maker, string model, bool laptop)
            => Assert.Equal(expected, ComputerPackage.KindFor(new[] { (ushort)chassis }, maker, model, laptop));

        [Fact]
        public void Computer_WithoutChassis_FallsBackToBattery()
        {
            Assert.Equal(ComputerKind.Laptop, ComputerPackage.KindFor(null, "", "", true));
            Assert.Equal(ComputerKind.Tower, ComputerPackage.KindFor(null, "", "", false));
        }

        [Fact]
        public void Board_KindFollowsNameThenComputer()
        {
            Assert.Equal(BoardKind.Atx, BoardPackage.KindFor("ROG STRIX B550-F GAMING", ComputerKind.Tower));
            Assert.Equal(BoardKind.MicroAtx, BoardPackage.KindFor("B450M PRO-VDH", ComputerKind.Tower));
            Assert.Equal(BoardKind.MiniItx, BoardPackage.KindFor("H81I-PLUS mini-ITX", ComputerKind.Tower));
            Assert.Equal(BoardKind.Laptop, BoardPackage.KindFor("anything", ComputerKind.Ultrabook));
            Assert.Equal(BoardKind.MiniItx, BoardPackage.KindFor("", ComputerKind.MiniPc));
        }

        [Fact]
        public void Peripheral_KindFollowsName()
        {
            Assert.Equal(PeripheralKind.Keyboard, PeripheralArt.KindFor("HID Keyboard Device"));
            Assert.Equal(PeripheralKind.Mouse, PeripheralArt.KindFor("Logitech USB Optical Mouse"));
            Assert.Equal(PeripheralKind.Camera, PeripheralArt.KindFor("Integrated Webcam"));
            Assert.Equal(PeripheralKind.UsbStick, PeripheralArt.KindFor("USB Mass Storage Device"));
            Assert.Equal(PeripheralKind.UsbHub, PeripheralArt.KindFor("Generic USB Hub"));
            Assert.Equal(PeripheralKind.Headphones, PeripheralArt.KindFor("Logitech G435 Headset"));
            Assert.Equal(PeripheralKind.Headphones, PeripheralArt.KindFor("Sony WH-1000XM4 Headphones"));
            Assert.Equal(PeripheralKind.Speakers, PeripheralArt.KindFor("Realtek High Definition Audio"));
            Assert.Equal(PeripheralKind.Phone, PeripheralArt.KindFor("Apple Mobile Device USB Driver"));
            Assert.Equal(PeripheralKind.Phone, PeripheralArt.KindFor("SAMSUNG Android Phone"));
            Assert.Equal(PeripheralKind.ExternalHdd, PeripheralArt.KindFor("WD My Passport 0827 USB Device"));
            Assert.Equal(PeripheralKind.ExternalHdd, PeripheralArt.KindFor("Seagate Backup Plus Portable Drive"));
            Assert.Equal(PeripheralKind.Router, PeripheralArt.KindFor("TP-Link Wireless Router"));
            Assert.Equal(PeripheralKind.Printer, PeripheralArt.KindFor("HP LaserJet Pro M404"));
            Assert.Null(PeripheralArt.KindFor("Unknown Gizmo"));
        }

        [Fact]
        public void Peripheral_DistinctKinds_FollowPriority_AndSetFitsTheBox()
        {
            var kinds = PeripheralArt.DistinctKinds(new[] { "HID Keyboard Device", "USB Mouse", "Logitech Headset", "Another Keyboard", "HP LaserJet", "Mystery" });
            Assert.Equal(new[] { PeripheralKind.Printer, PeripheralKind.Headphones, PeripheralKind.Keyboard, PeripheralKind.Mouse }, kinds);
            Assert.Empty(PeripheralArt.DistinctKinds(new[] { "Mystery", null }));

            // one type: the whole drawing; several: a grid, in the same box
            Assert.Equal(PeripheralArt.Build(PeripheralKind.Keyboard).Shapes.Count, PeripheralArt.BuildSet(new[] { PeripheralKind.Keyboard }).Shapes.Count);
            var set = PeripheralArt.BuildSet(kinds);
            Assert.Equal(PeripheralArt.Width, set.Width);
            Assert.Equal(kinds.Take(4).Sum(k => PeripheralArt.Build(k).Shapes.Count), set.Shapes.Count);
            foreach (var s in set.Shapes)
            {
                Assert.InRange(s.X, -0.01, set.Width + 0.01); Assert.InRange(s.Y, -0.01, set.Height + 0.01);
                Assert.InRange(s.X + s.W, -0.01, set.Width + 0.01); Assert.InRange(s.Y + s.H, -0.01, set.Height + 0.01);
            }
            Assert.NotEmpty(PeripheralArt.BuildSet(Array.Empty<PeripheralKind>()).Shapes);     // no types: a stick, not an empty drawing
        }

        [Fact]
        public void Wifi_BarsAndColourFollowTheSignal()
        {
            Assert.Equal(new[] { 0, 1, 2, 2, 3, 4, 4 }, new[] { 0, 10, 25, 49, 50, 75, 100 }.Select(NetworkArt.SignalBars).ToArray());
            Assert.Equal(ArtFill.Ok, NetworkArt.SignalFill(60));
            Assert.Equal(ArtFill.Warn, NetworkArt.SignalFill(45));
            Assert.Equal(ArtFill.Crit, NetworkArt.SignalFill(20));
            int Filled(NetworkArt_Args a) => NetworkArt.BuildWifi(a.Pct, a.Connected, a.Known).Shapes.Count(s => s.Opacity > 0.9 && s.W == 6 && s.Fill != ArtFill.Muted);
            Assert.Equal(4, Filled(new(90, true, true)));
            Assert.Equal(2, Filled(new(30, true, true)));
            Assert.Equal(0, Filled(new(0, false, true)));
            Assert.Equal(0, Filled(new(80, true, false)));
            Assert.Contains(NetworkArt.BuildWifi(0, false).Shapes, s => s.Kind == ArtKind.Polygon && s.Fill == ArtFill.Crit);          // crucea
            Assert.DoesNotContain(NetworkArt.BuildWifi(0, true, false).Shapes, s => s.Fill == ArtFill.Crit);                           // not read: no cross
        }

        private record NetworkArt_Args(int Pct, bool Connected, bool Known);

        [Fact]
        public void ArtLayout_Tile_ScalesAndPositionsEachDrawingInItsCell()
        {
            var one = new ArtDrawing(100, 100, new[] { new ArtShape(ArtKind.Rect, 0, 0, 100, 100, ArtFill.Lid, 10) });
            var tiled = ArtLayout.Tile(new[] { one, one }, 200, 100, 2);
            Assert.Equal(2, tiled.Shapes.Count);
            Assert.Equal((0.0, 0.0, 100.0, 100.0), (tiled.Shapes[0].X, tiled.Shapes[0].Y, tiled.Shapes[0].W, tiled.Shapes[0].H));
            Assert.Equal(100.0, tiled.Shapes[1].X);
            var small = ArtLayout.Tile(new[] { one, one, one, one }, 200, 200, 2);
            Assert.All(small.Shapes, s => Assert.Equal(100.0, s.W));
            var half = ArtLayout.Tile(new[] { one, one }, 100, 50, 2);       // 50 × 50 cells: the drawing shrinks to half
            Assert.Equal(50.0, half.Shapes[0].W);
            Assert.Equal(5.0, half.Shapes[0].Radius);
        }

        [Fact]
        public void ArtCatalog_KeysAreUnique_AndEveryEntryStaysInsideItsBox()
        {
            Assert.Equal(ArtCatalog.All.Count, ArtCatalog.All.Select(e => e.Key).Distinct().Count());
            Assert.NotNull(ArtCatalog.Find("cpu.ArmSoc"));
            Assert.NotNull(ArtCatalog.Find("peripheral.Headphones"));
            Assert.Null(ArtCatalog.Find("nope"));
            foreach (var entry in ArtCatalog.All)
            {
                var art = entry.Build();
                Assert.NotEmpty(art.Shapes);
                foreach (var s in art.Shapes)
                {
                    Assert.True(s.X >= -0.01 && s.Y >= -0.01 && s.X + s.W <= art.Width + 0.01 && s.Y + s.H <= art.Height + 0.01, $"{entry.Key}: {s.Kind} {s.X:0.#},{s.Y:0.#} {s.W:0.#}×{s.H:0.#}");
                    if (s.Kind == ArtKind.Polygon)
                        Assert.All(s.Points!, p => Assert.True(p.X >= -0.01 && p.Y >= -0.01 && p.X <= art.Width + 0.01 && p.Y <= art.Height + 0.01, $"{entry.Key}: point"));
                }
            }
        }

        [Fact]
        public void AllNewDrawings_StayInsideTheBox()
        {
            var all = Enum.GetValues<ComputerKind>().Select(ComputerArt.Build)
                .Concat(Enum.GetValues<BoardKind>().Select(BoardArt.Build))
                .Concat(Enum.GetValues<PeripheralKind>().Select(PeripheralArt.Build))
                .Concat(Enum.GetValues<NetworkKind>().Select(NetworkArt.Build))
                .Append(IntegratedGpuArt.Build());
            foreach (var art in all)
            {
                Assert.NotEmpty(art.Shapes);
                foreach (var s in art.Shapes)
                {
                    Assert.InRange(s.X, -0.01, art.Width + 0.01);
                    Assert.InRange(s.Y, -0.01, art.Height + 0.01);
                    Assert.InRange(s.X + s.W, -0.01, art.Width + 0.01);
                    Assert.InRange(s.Y + s.H, -0.01, art.Height + 0.01);
                }
            }
        }

        [Theory]
        [InlineData("TP-LINK TECHNOLOGIES CO.,LTD.", "TP-LINK")]
        [InlineData("ASUSTek COMPUTER INC.", "ASUSTek COMPUTER")]
        [InlineData("Ubiquiti Inc", "Ubiquiti")]
        [InlineData("D-Link International", "D-Link International")]
        [InlineData("", "")]
        public void RouterIdentity_ShortVendor_DropsTheLegalForm(string raw, string expected)
            => Assert.Equal(expected, SMDWin.Services.RouterIdentity.ShortVendor(raw));

        [Fact]
        public void RouterIdentity_DisplayName_PrefersModelThenFriendlyNameThenVendor()
        {
            var full = new SMDWin.Services.RouterIdentity("192.168.0.1", "AA:BB", "TP-LINK TECHNOLOGIES CO.,LTD.", "TP-Link", "Archer AX10", "Archer");
            Assert.Equal("TP-Link Archer AX10", full.DisplayName);
            var modelHasBrand = new SMDWin.Services.RouterIdentity("g", "", "", "TP-Link", "TP-Link Archer C6", "");
            Assert.Equal("TP-Link Archer C6", modelHasBrand.DisplayName);
            var friendly = new SMDWin.Services.RouterIdentity("g", "", "Vendor", "", "", "Home Router");
            Assert.Equal("Home Router", friendly.DisplayName);
            var onlyMac = new SMDWin.Services.RouterIdentity("g", "AA", "TP-LINK TECHNOLOGIES CO.,LTD.", "", "", "");
            Assert.Equal("TP-LINK", onlyMac.DisplayName);
            Assert.False(onlyMac.HasModel);
            Assert.True(full.HasModel);
        }
    }
}
