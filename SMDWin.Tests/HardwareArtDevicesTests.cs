using System;
using System.Linq;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    /// <summary>The graphics card, the monitors and the system: which drawing is chosen, what is read from the name and from
    /// the monitor data.</summary>
    public class HardwareArtDevicesTests
    {
        [Fact]
        public void Gpu_IntegratedGraphics_AreRecognisedByName()
        {
            foreach (string name in new[]
            {
                "Intel(R) UHD Graphics 630", "Intel(R) HD Graphics 4600", "Intel(R) Iris(R) Xe Graphics", "Intel(R) Arc(TM) Graphics",
                "Intel(R) Arc(TM) 140V GPU (16GB)", "Intel(R) Graphics",
                "AMD Radeon(TM) Graphics", "AMD Radeon(TM) Vega 8 Graphics", "AMD Radeon(TM) R7 Graphics", "AMD Radeon(TM) 780M",
            })
                Assert.True(GpuPackage.IsIntegrated(name), name);

            foreach (string name in new[]
            {
                "NVIDIA GeForce GTX 1050 Ti", "NVIDIA GeForce RTX 3060 Laptop GPU", "AMD Radeon RX 580 Series", "AMD Radeon RX 6600M",
                "AMD Radeon HD 8670M", "AMD Radeon R5 M330", "Radeon RX Vega 56", "Intel(R) Arc(TM) A770 Graphics", "Intel(R) Arc(TM) B580 Graphics",
                "Intel(R) Arc(TM) A370M Graphics", "Intel(R) Iris(R) Xe MAX Graphics", "Microsoft Basic Display Adapter", "",
            })
                Assert.False(GpuPackage.IsIntegrated(name), name);
        }

        [Fact]
        public void Gpu_Style_FollowsTheKindOfComputer()
        {
            Assert.Equal(GpuPictureStyle.Integrated, GpuPackage.StyleFor("Intel(R) UHD Graphics 630", laptop: true));
            Assert.Equal(GpuPictureStyle.Integrated, GpuPackage.StyleFor("Intel(R) HD Graphics 4600", laptop: false));
            Assert.Equal(GpuPictureStyle.LaptopChip, GpuPackage.StyleFor("NVIDIA GeForce GTX 1050 Ti", laptop: true));
            Assert.Equal(GpuPictureStyle.DesktopCard, GpuPackage.StyleFor("NVIDIA GeForce GTX 1050 Ti", laptop: false));
        }

        [Fact]
        public void Gpu_VendorShortNameAndFans()
        {
            Assert.Equal("NVIDIA", GpuPackage.Vendor("NVIDIA GeForce GTX 1050 Ti"));
            Assert.Equal("AMD", GpuPackage.Vendor("AMD Radeon RX 580 Series"));
            Assert.Equal("AMD", GpuPackage.Vendor("ATI Radeon HD 5450"));
            Assert.Equal("Intel", GpuPackage.Vendor("Intel(R) UHD Graphics 630"));
            Assert.Equal("", GpuPackage.Vendor("Microsoft Basic Display Adapter"));

            Assert.Equal("GeForce GTX 1050 Ti", GpuPackage.ShortName("NVIDIA GeForce GTX 1050 Ti"));
            Assert.Equal("UHD Graphics 630", GpuPackage.ShortName("Intel(R) UHD Graphics 630"));
            Assert.Equal("Radeon RX 580", GpuPackage.ShortName("AMD Radeon RX 580 Series"));
            Assert.Equal("Radeon Graphics", GpuPackage.ShortName("AMD Radeon(TM) Graphics"));
            Assert.Equal("", GpuPackage.ShortName(null));

            Assert.Equal(1, GpuPackage.Fans(75));
            Assert.Equal(2, GpuPackage.Fans(150));
            Assert.Equal(3, GpuPackage.Fans(320));
            Assert.Equal(2, GpuPackage.Fans(0));     // putere necunoscuta

            Assert.True(GpuPackage.IsBasicDriver("Microsoft Basic Display Adapter"));
            Assert.True(GpuPackage.IsBasicDriver("Standard VGA Graphics Adapter"));
            Assert.False(GpuPackage.IsBasicDriver("NVIDIA GeForce GTX 1050 Ti"));
        }

        [Fact]
        public void Gpu_DriverText_UsesTheMakersNumbering()
        {
            Assert.Equal("536.23", GpuPackage.DriverText("NVIDIA GeForce GTX 1050 Ti", "31.0.15.3623"));
            Assert.Equal("456.71", GpuPackage.DriverText("NVIDIA GeForce GTX 1050 Ti", "27.21.14.5671"));
            Assert.Equal("31.0.101.2125", GpuPackage.DriverText("Intel(R) UHD Graphics 630", "31.0.101.2125"));
            Assert.Equal("", GpuPackage.DriverText("NVIDIA GeForce GTX 1050 Ti", null));
            Assert.Equal("1.2", GpuPackage.DriverText("NVIDIA GeForce GTX 1050 Ti", "1.2"));     // too short: unchanged
        }

        [Fact]
        public void GpuArt_AllStyles_StayInsideTheBox()
        {
            foreach (GpuPictureStyle style in Enum.GetValues<GpuPictureStyle>())
                for (int fans = 1; fans <= 3; fans++)
                    foreach (bool intel in new[] { false, true })
                    {
                        var art = GpuArt.Build(style, fans, intel);
                        Assert.True(art.Shapes.Count > 5, $"{style}");
                        AssertInside(art, $"{style} {fans} {intel}");
                    }
            // the number of fans shows in the drawing: each has a hub (a disc in the colour of the cover)
            int Hubs(int fans) => GpuArt.Build(GpuPictureStyle.DesktopCard, fans).Shapes.Count(s => s.Fill == ArtFill.LidShade && s.Radius > 0 && Math.Abs(s.W - s.H) < 0.01 && Math.Abs(s.Radius - s.W / 2) < 0.01);
            Assert.Equal(1, Hubs(1));
            Assert.Equal(2, Hubs(2));
            Assert.Equal(3, Hubs(3));
            Assert.Equal(3, Hubs(9));     // over 3 → 3
        }

        [Fact]
        public void GpuArt_VendorColoursTheAccents_AndVramBarShowsTheMemory()
        {
            ArtFill band(string vendor) => GpuArt.Build(GpuPictureStyle.DesktopCard, 2, false, vendor).Shapes.First(s => s.Y > 76 && s.Y < 77 && s.H == 2).Fill;
            Assert.Equal(ArtFill.Ok, band("NVIDIA"));
            Assert.Equal(ArtFill.Crit, band("AMD"));
            Assert.Equal(ArtFill.Blue, band("Intel"));
            Assert.Equal(ArtFill.Accent, band(""));
            Assert.Equal(ArtFill.Blue, band("Intel"));

            // the VRAM bar: 12 segments; the full ones grow with the memory (2 GB one segment, 24 GB the whole bar)
            Assert.Equal(0, GpuArt.VramSegments(0));
            Assert.Equal(1, GpuArt.VramSegments(1));
            Assert.Equal(2, GpuArt.VramSegments(4));
            Assert.Equal(4, GpuArt.VramSegments(8));
            Assert.Equal(12, GpuArt.VramSegments(24));
            Assert.Equal(12, GpuArt.VramSegments(48));
            int Filled(double gb, GpuPictureStyle st) => GpuArt.Build(st, 2, false, "NVIDIA", gb).Shapes.Count(s => s.Fill == ArtFill.Ok && s.Radius == 1 && s.Opacity > 0.9 && s.H is 5 or 4);
            Assert.Equal(4, Filled(8, GpuPictureStyle.DesktopCard));
            Assert.Equal(12, Filled(24, GpuPictureStyle.DesktopCard));
            Assert.Equal(6, Filled(12, GpuPictureStyle.LaptopChip));
            Assert.Equal(0, Filled(0, GpuPictureStyle.DesktopCard));
            Assert.Equal(GpuArt.Build(GpuPictureStyle.Integrated).Shapes.Count, GpuArt.Build(GpuPictureStyle.Integrated, 2, false, "NVIDIA", 8).Shapes.Count);   // integrated: no bar
            // an old "intel" means the same as vendor = "Intel"
            Assert.Equal(band("Intel"), GpuArt.Build(GpuPictureStyle.DesktopCard, 2, true).Shapes.First(s => s.Y > 76 && s.Y < 77 && s.H == 2).Fill);
        }

        [Fact]
        public void GpuArt_VendorDrawingsStayInsideTheBox()
        {
            foreach (GpuPictureStyle style in Enum.GetValues<GpuPictureStyle>())
                foreach (string vendor in new[] { "", "NVIDIA", "AMD", "Intel" })
                    foreach (double gb in new[] { 0.0, 4, 8, 24, 100 })
                        for (int fans = 1; fans <= 3; fans++)
                            AssertInside(GpuArt.Build(style, fans, false, vendor, gb), $"{style} {vendor} {gb} {fans}");
        }

        [Theory]
        [InlineData("8 GB", 8)]
        [InlineData("8.0 GB", 8)]
        [InlineData("4096 MB", 4)]
        [InlineData("0,5 TB", 512)]
        [InlineData("1.5GB", 1.5)]
        [InlineData("", 0)]
        [InlineData("unknown", 0)]
        public void VramGb_ReadsTheText(string text, double expected) => Assert.Equal(expected, GpuPackage.VramGb(text), 3);

        private static void AssertInside(ArtDrawing art, string what)
        {
            foreach (var s in art.Shapes)
            {
                Assert.True(s.X >= -0.01 && s.Y >= -0.01 && s.X + s.W <= art.Width + 0.01 && s.Y + s.H <= art.Height + 0.01,
                    $"{what}: {s.Kind} {s.X:0.#},{s.Y:0.#} {s.W:0.#}×{s.H:0.#}");
                if (s.Kind == ArtKind.Polygon)
                    foreach (var p in s.Points!)
                        Assert.True(p.X >= -0.01 && p.Y >= -0.01 && p.X <= art.Width + 0.01 && p.Y <= art.Height + 0.01, $"{what}: point {p.X:0.#},{p.Y:0.#}");
            }
        }

        [Fact]
        public void Display_Diagonal_SnapsToTheSizesSoldInShops()
        {
            // the monitor reports its size in whole cm: 53 × 30 cm = 23.98 inch → 24
            Assert.Equal(24, DisplayFacts.NiceDiagonal(DisplayFacts.DiagonalInches(53, 30)));
            Assert.Equal(15.6, DisplayFacts.NiceDiagonal(DisplayFacts.DiagonalInches(34, 19)));     // 15.33 → 15.6
            Assert.Equal(27, DisplayFacts.NiceDiagonal(DisplayFacts.DiagonalInches(60, 34)));
            Assert.Equal(14, DisplayFacts.NiceDiagonal(DisplayFacts.DiagonalInches(31, 17)));
            Assert.Equal(45.5, DisplayFacts.NiceDiagonal(45.52));      // unusual size: stays as it is
            Assert.Equal(0, DisplayFacts.DiagonalInches(0, 0));        // the monitor does not say
            Assert.Equal("", DisplayFacts.DiagonalText(0));
            Assert.Equal("15.6″", DisplayFacts.DiagonalText(15.33));
            Assert.Equal("24″", DisplayFacts.DiagonalText(23.98));
        }

        [Fact]
        public void Display_ResolutionNameConnectionAndDensity()
        {
            Assert.Equal("Full HD", DisplayFacts.ResolutionName(1920, 1080));
            Assert.Equal("Full HD", DisplayFacts.ResolutionName(1080, 1920));     // monitor rotit
            Assert.Equal("4K", DisplayFacts.ResolutionName(3840, 2160));
            Assert.Equal("HD", DisplayFacts.ResolutionName(1366, 768));
            Assert.Equal("", DisplayFacts.ResolutionName(1680, 1050));

            Assert.Equal("HDMI", DisplayFacts.Connection(5));
            Assert.Equal("DisplayPort", DisplayFacts.Connection(10));
            Assert.Equal("VGA", DisplayFacts.Connection(0));
            Assert.Equal("", DisplayFacts.Connection(0x80000000));
            Assert.True(DisplayFacts.IsBuiltIn(0x80000000));
            Assert.True(DisplayFacts.IsBuiltIn(11));      // DisplayPort intern (eDP)
            Assert.True(DisplayFacts.IsBuiltIn(6));       // LVDS
            Assert.False(DisplayFacts.IsBuiltIn(5));
            Assert.False(DisplayFacts.IsBuiltIn(10));

            Assert.Equal(141, DisplayFacts.Ppi(1920, 1080, 15.6));
            Assert.Equal(92, DisplayFacts.Ppi(1920, 1080, 24));
            Assert.Equal(0, DisplayFacts.Ppi(1920, 1080, 0));
        }

        [Fact]
        public void MonitorArt_Screens_KeepTheirShapeAndRelativeSize()
        {
            // a single 16:9 monitor with no known size: fills the width
            var one = MonitorArt.Sizes(new[] { new MonitorSpec(1920, 1080, 0, true, false) });
            Assert.Single(one);
            Assert.True(Math.Abs(one[0].W / one[0].H - 16.0 / 9) < 0.01);
            Assert.True(one[0].H <= 68.01 && one[0].W <= 124.01);

            // a 15.6 laptop (19 cm high) + a 24 monitor (30 cm): the monitor comes out larger
            var two = MonitorArt.Sizes(new[] { new MonitorSpec(1920, 1080, 19, true, true), new MonitorSpec(1920, 1080, 30, false, false) });
            Assert.Equal(2, two.Count);
            Assert.True(Math.Abs(two[1].H / two[0].H - 30.0 / 19) < 0.01);

            // no real sizes for any of them: the same diagonal; the ultrawide one is wider and shorter
            var mixed = MonitorArt.Sizes(new[] { new MonitorSpec(1920, 1080, 30, true, false), new MonitorSpec(3440, 1440, 0, false, false) });
            Assert.True(Math.Abs(Math.Sqrt(mixed[0].W * mixed[0].W + mixed[0].H * mixed[0].H) - Math.Sqrt(mixed[1].W * mixed[1].W + mixed[1].H * mixed[1].H)) < 0.01);
            Assert.True(mixed[1].W > mixed[0].W && mixed[1].H < mixed[0].H);

            // rotated monitor: taller than wide
            var portrait = MonitorArt.Sizes(new[] { new MonitorSpec(1080, 1920, 0, true, false) });
            Assert.True(portrait[0].H > portrait[0].W);

            // more than three screens: the first three are drawn
            var many = Enumerable.Range(0, 5).Select(i => new MonitorSpec(1920, 1080, 0, i == 0, false, (i + 1).ToString())).ToArray();
            Assert.Equal(3, MonitorArt.Sizes(many).Count);
            Assert.Empty(MonitorArt.Sizes(Array.Empty<MonitorSpec>()));
        }

        [Fact]
        public void MonitorArt_Drawings_StayInsideTheBox()
        {
            var cases = new[]
            {
                new[] { new MonitorSpec(1920, 1080, 0, true, false) },
                new[] { new MonitorSpec(1920, 1080, 19, true, true) },
                new[] { new MonitorSpec(1920, 1080, 19, true, true, "1"), new MonitorSpec(2560, 1440, 34, false, false, "2") },
                new[] { new MonitorSpec(1080, 1920, 0, false, false, "1"), new MonitorSpec(3440, 1440, 0, true, false, "2"), new MonitorSpec(1920, 1080, 0, false, false, "3") },
                new[] { new MonitorSpec(0, 0, 0, true, false) },      // unread resolution: usual proportions
            };
            foreach (var screens in cases)
            {
                var art = MonitorArt.Build(screens);
                AssertInside(art, $"{screens.Length} screens");
                // one lit screen for each monitor; the numbers appear only when given
                Assert.Equal(screens.Length, art.Shapes.Count(s => s.Fill == ArtFill.Accent));
                Assert.Equal(screens.Count(m => m.Label.Length > 0), art.Shapes.Count(s => s.Kind == ArtKind.Text));
            }
            Assert.Empty(MonitorArt.Build(Array.Empty<MonitorSpec>()).Shapes);
            // the main screen is brighter than the others
            var pair = MonitorArt.Build(cases[2]).Shapes.Where(s => s.Fill == ArtFill.Accent).ToList();
            Assert.True(pair[0].Opacity > pair[1].Opacity);
        }

        [Fact]
        public void SystemArt_LaptopAndBoard_StayInsideTheBox()
        {
            foreach (bool laptop in new[] { true, false })
            {
                var art = SystemArt.Build(laptop);
                Assert.True(art.Shapes.Count > 8);
                AssertInside(art, laptop ? "laptop" : "board");
            }
            // the laptop has a screen (accent), the motherboard does not
            Assert.True(SystemArt.Build(true).Shapes.Any(s => s.Fill == ArtFill.Accent));
            Assert.False(SystemArt.Build(false).Shapes.Any(s => s.Fill == ArtFill.Accent));
        }

        [Fact]
        public void IntegratedGpu_IsOneClassicChip_WithoutMemoryStrip()
        {
            var art = GpuArt.Build(GpuPictureStyle.Integrated);
            Assert.DoesNotContain(art.Shapes, s => s.Kind == ArtKind.Text);                 // neither "RAM" nor "GPU"
            Assert.Equal(1, art.Shapes.Count(s => s.Fill == ArtFill.Die && s.H > 40));               // a single silicon die
            Assert.DoesNotContain(art.Shapes, s => s.Fill == ArtFill.Die && s.Y > 75);       // nothing drawn below (memory)
        }

        [Fact]
        public void LaptopIntelCpu_HasOneDie_AndNoGraphicsZone()
        {
            var art = CpuArt.Build(CpuPackageStyle.LaptopIntel, "Intel", "Core i5-10210U");
            Assert.Equal(1, art.Shapes.Count(s => s.Fill == ArtFill.Die));
            Assert.DoesNotContain(art.Shapes, s => s.Fill == ArtFill.Accent);
        }

        [Theory]
        [InlineData("chrome", AppKind.Browser)]
        [InlineData("Discord.exe", AppKind.Communication)]
        [InlineData("AnyDesk", AppKind.Remote)]
        [InlineData("OneDrive", AppKind.Cloud)]
        [InlineData("svchost", AppKind.System)]
        [InlineData("nothing-known", AppKind.Other)]
        [InlineData("", AppKind.Other)]
        public void AppKinds_ClassifyByProcessName(string name, AppKind expected)
            => Assert.Equal(expected, AppKinds.Classify(name));

        [Fact]
        public void EveryAppKind_HasItsOwnLabelAndDrawing()
        {
            var kinds = System.Enum.GetValues<AppKind>();
            Assert.Equal(kinds.Length, kinds.Select(AppKinds.Label).Distinct().Count());
            foreach (var k in kinds) Assert.True(AppArt.Build(k).Shapes.Count >= 3, k.ToString());
        }
    }
}
