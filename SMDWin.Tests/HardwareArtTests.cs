using System;
using System.Linq;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    /// <summary>The hardware illustrations: which processor drawing is chosen, hybrid cores, the drawing shapes, M.2 SATA disks.</summary>
    public class HardwareArtTests
    {
        [Fact]
        public void Style_FollowsWhereTheProcessorSits_AndWhoMadeIt()
        {
            // the laptop from the screenshots: i7-8750H with Intel UHD 630 graphics
            Assert.Equal(CpuPackageStyle.LaptopIntelGraphics, CpuPackage.StyleFor("Intel(R) Core(TM) i7-8750H CPU @ 2.20GHz", laptop: true, "FCBGA1440", integratedGraphics: true));
            Assert.Equal(CpuPackageStyle.LaptopIntel, CpuPackage.StyleFor("Intel(R) Core(TM) i7-8750H CPU @ 2.20GHz", true, null, false));
            Assert.Equal(CpuPackageStyle.LaptopAmd, CpuPackage.StyleFor("AMD Ryzen 7 5800H with Radeon Graphics", true, "FP6", true));
            Assert.Equal(CpuPackageStyle.DesktopIntel, CpuPackage.StyleFor("Intel(R) Core(TM) i5-4570 CPU @ 3.20GHz", false, "LGA 1150", true));
            Assert.Equal(CpuPackageStyle.DesktopAmd, CpuPackage.StyleFor("AMD Ryzen 5 5600X 6-Core Processor", false, "AM4", false));
            Assert.Equal(CpuPackageStyle.Server, CpuPackage.StyleFor("Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz", false, "LGA 2011-3", false));
            Assert.Equal(CpuPackageStyle.Server, CpuPackage.StyleFor("AMD Ryzen Threadripper 3960X 24-Core Processor", false, "sTRX4", false));
            Assert.Equal(CpuPackageStyle.Server, CpuPackage.StyleFor("AMD EPYC 7302 16-Core Processor", false, null, false));       // no socket in the database: by name
            Assert.Equal(CpuPackageStyle.Server, CpuPackage.StyleFor("Intel(R) Core(TM) i9-7900X CPU @ 3.30GHz", false, "LGA 2066", false));   // soclu mare
        }

        [Fact]
        public void Style_SolderedProcessors_AreDrawnAsLaptopParts()
        {
            // a mobile Xeon in a portable workstation
            Assert.Equal(CpuPackageStyle.LaptopIntelGraphics, CpuPackage.StyleFor("Intel(R) Xeon(R) CPU E3-1505M v5 @ 2.80GHz", true, null, true));
            // mini PC with a laptop processor: "desktop" chassis, but a soldered socket
            Assert.Equal(CpuPackageStyle.LaptopIntel, CpuPackage.StyleFor("Intel(R) Core(TM) i5-8259U CPU @ 2.30GHz", false, "BGA1526", false));
            Assert.Equal(CpuPackageStyle.LaptopAmd, CpuPackage.StyleFor("AMD Ryzen 5 4500U", false, "FP6", false));
            // a socketed Xeon E3 is a PC processor, not a "big server"
            Assert.Equal(CpuPackageStyle.DesktopIntel, CpuPackage.StyleFor("Intel(R) Xeon(R) CPU E3-1230 v3 @ 3.30GHz", false, "LGA 1150", false));
            // ARM processor (Snapdragon, Apple M): its own package, not an x86 laptop chip
            Assert.Equal(CpuPackageStyle.ArmSoc, CpuPackage.StyleFor("Snapdragon X Elite", true, null, false));
            // unknown manufacturer: a single chip, no markings
            Assert.Equal(CpuPackageStyle.LaptopAmd, CpuPackage.StyleFor("Genuine Unknown CPU", true, null, false));
            Assert.Equal(CpuPackageStyle.DesktopIntel, CpuPackage.StyleFor("", false, null, false));
        }

        [Fact]
        public void Resolve_UsesTheChosenStyle_OrTheAutomaticOne()
        {
            Assert.Equal(CpuPackageStyle.Server, CpuPackage.Resolve("Server", CpuPackageStyle.LaptopAmd));
            Assert.Equal(CpuPackageStyle.LaptopAmd, CpuPackage.Resolve(CpuPackage.Auto, CpuPackageStyle.LaptopAmd));
            Assert.Equal(CpuPackageStyle.LaptopAmd, CpuPackage.Resolve(null, CpuPackageStyle.LaptopAmd));
            Assert.Equal(CpuPackageStyle.LaptopAmd, CpuPackage.Resolve("99", CpuPackageStyle.LaptopAmd));        // a number without a style
            Assert.Equal(CpuPackage.Styles.Length, Enum.GetValues<CpuPackageStyle>().Length);
        }

        [Fact]
        public void HybridCores_ComeFromCoresAndThreads()
        {
            Assert.Equal((8, 8), CpuPackage.HybridCores("13th Gen Intel(R) Core(TM) i7-13700K", 16, 24));
            Assert.Equal((6, 8), CpuPackage.HybridCores("12th Gen Intel(R) Core(TM) i7-12700H", 14, 20));
            Assert.Equal((2, 8), CpuPackage.HybridCores("12th Gen Intel(R) Core(TM) i5-1235U", 10, 12));
            Assert.Null(CpuPackage.HybridCores("Intel(R) Core(TM) i7-8750H CPU @ 2.20GHz", 6, 12));     // all with two threads
            Assert.Null(CpuPackage.HybridCores("Intel(R) Core(TM) i5-9400 CPU @ 2.90GHz", 6, 6));        // all with one thread
            Assert.Null(CpuPackage.HybridCores("AMD Ryzen 5 5600X 6-Core Processor", 6, 12));
            Assert.Null(CpuPackage.HybridCores("AMD Ryzen 7 5700U", 8, 12));                            // not Intel: no guessing
            Assert.Null(CpuPackage.HybridCores("Intel", 0, 0));
        }

        [Fact]
        public void ShortName_AndClock()
        {
            Assert.Equal("Core i7-8750H", CpuPackage.ShortName("Intel(R) Core(TM) i7-8750H CPU @ 2.20GHz"));
            Assert.Equal("Ryzen 5 5600X", CpuPackage.ShortName("AMD Ryzen 5 5600X 6-Core Processor"));
            Assert.Equal("Core i7-13700K", CpuPackage.ShortName("13th Gen Intel(R) Core(TM) i7-13700K"));
            Assert.Equal("Ryzen 7 5800H", CpuPackage.ShortName("AMD Ryzen 7 5800H with Radeon Graphics"));
            Assert.Equal("Xeon E5-2680 v4", CpuPackage.ShortName("Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz"));
            Assert.Equal("", CpuPackage.ShortName(""));
            Assert.Equal("2.2 GHz", CpuPackage.ClockText("2208 MHz"));
            Assert.Equal("3.6 GHz", CpuPackage.ClockText("3600 MHz"));
            Assert.Equal("3.75 GHz", CpuPackage.ClockText("3750 MHz"));
            Assert.Equal("800 MHz", CpuPackage.ClockText("800 MHz"));
            Assert.Equal("", CpuPackage.ClockText(""));
            Assert.Equal("Intel", CpuPackage.Vendor("Intel(R) Core(TM) i7"));
            Assert.Equal("AMD", CpuPackage.Vendor("Ryzen 5 5600X"));
            Assert.Equal("", CpuPackage.Vendor("Snapdragon X"));
        }

        [Fact]
        public void EveryDrawing_StaysInsideItsBox()
        {
            foreach (var (style, _) in CpuPackage.Styles)
            {
                var art = CpuArt.Build(style, "intel", "Core i7-8700K");
                Assert.True(art.Shapes.Count >= 6, style.ToString());
                foreach (var sh in art.Shapes)
                {
                    Assert.True(sh.X >= 0 && sh.Y >= 0 && sh.X + sh.W <= art.Width && sh.Y + sh.H <= art.Height, $"{style}: {sh.Kind} {sh.Fill} iese din caseta");
                    Assert.True(sh.W > 0 && sh.H > 0 && sh.Opacity > 0 && sh.Opacity <= 1);
                    if (sh.Kind == ArtKind.Polygon)
                        Assert.True(sh.Points is { Count: >= 3 } && sh.Points.All(p => p.X >= 0 && p.X <= art.Width && p.Y >= 0 && p.Y <= art.Height));
                }
            }
        }

        [Fact]
        public void Drawings_ShowWhatTheStylePromises()
        {
            bool Has(CpuPackageStyle st, ArtFill fill) => CpuArt.Build(st, "intel", "x").Shapes.Any(s => s.Fill == fill);
            string Texts(CpuPackageStyle st) => string.Join("|", CpuArt.Build(st, "AMD", "Ryzen 5 5600X").Shapes.Where(s => s.Kind == ArtKind.Text).Select(s => s.Text));

            // with a lid: the manufacturer and the model engraved; no visible chip
            foreach (var st in new[] { CpuPackageStyle.DesktopIntel, CpuPackageStyle.DesktopAmd, CpuPackageStyle.Server })
            {
                Assert.True(Has(st, ArtFill.Lid) && !Has(st, ArtFill.Die), st.ToString());
                Assert.Equal("AMD|Ryzen 5 5600X", Texts(st));
            }
            // laptop: the chip visible, no lid
            foreach (var st in new[] { CpuPackageStyle.LaptopIntel, CpuPackageStyle.LaptopIntelGraphics, CpuPackageStyle.LaptopAmd })
                Assert.True(Has(st, ArtFill.Die) && !Has(st, ArtFill.Lid), st.ToString());
            // only the variant with graphics has the accent area
            Assert.True(Has(CpuPackageStyle.LaptopIntelGraphics, ArtFill.Accent));
            Assert.False(Has(CpuPackageStyle.LaptopIntel, ArtFill.Accent));
            Assert.Equal("GPU", Texts(CpuPackageStyle.LaptopIntelGraphics));
            // no markings when none are given
            Assert.Empty(CpuArt.Build(CpuPackageStyle.DesktopIntel).Shapes.Where(s => s.Kind == ArtKind.Text));
            // all have the golden corner mark
            Assert.True(CpuPackage.Styles.All(x => CpuArt.Build(x.Style).Shapes.Any(s => s.Kind == ArtKind.Polygon && s.Fill == ArtFill.Gold)));
        }

        [Theory]
        [InlineData("Intel(R) Core(TM) i7-8750H CPU @ 2.20GHz", CpuFamily.Core)]
        [InlineData("13th Gen Intel(R) Core(TM) i5-13400", CpuFamily.Core)]
        [InlineData("Intel(R) Core(TM) Ultra 7 155H", CpuFamily.Core)]
        [InlineData("Intel(R) Core(TM) m3-7Y30 CPU @ 1.00GHz", CpuFamily.Core)]
        [InlineData("AMD Ryzen 5 5600X 6-Core Processor", CpuFamily.Ryzen)]
        [InlineData("AMD Ryzen 7 PRO 6850U with Radeon Graphics", CpuFamily.Ryzen)]
        [InlineData("Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz", CpuFamily.Server)]
        [InlineData("AMD EPYC 7302 16-Core Processor", CpuFamily.Server)]
        [InlineData("AMD Ryzen Threadripper 3970X", CpuFamily.Server)]
        [InlineData("Intel(R) Celeron(R) N4020 CPU @ 1.10GHz", CpuFamily.Entry)]
        [InlineData("Intel(R) Pentium(R) Silver N5030 CPU @ 1.10GHz", CpuFamily.Entry)]
        [InlineData("Intel(R) Processor N100", CpuFamily.Entry)]
        [InlineData("Intel(R) Atom(TM) x5-Z8350 CPU @ 1.44GHz", CpuFamily.Entry)]
        [InlineData("Apple M2", CpuFamily.Arm)]
        [InlineData("Apple M1 Pro", CpuFamily.Arm)]
        [InlineData("Snapdragon(R) X Elite - X1E78100 - Qualcomm(R) Oryon(TM) CPU", CpuFamily.Arm)]
        [InlineData("Genuine Unknown", CpuFamily.Other)]
        public void Family_FollowsTheName(string name, CpuFamily expected) => Assert.Equal(expected, CpuPackage.Family(name));

        [Fact]
        public void Family_DecidesTheStyleAndTheInscription()
        {
            Assert.Equal(CpuPackageStyle.ArmSoc, CpuPackage.StyleFor("Apple M2", true, null, false));
            Assert.Equal(CpuPackageStyle.ArmSoc, CpuPackage.StyleFor("Apple M1", false, null, false));
            Assert.Equal(CpuPackageStyle.Entry, CpuPackage.StyleFor("Intel(R) Celeron(R) N4020", true, null, false));
            Assert.Equal(CpuPackageStyle.Entry, CpuPackage.StyleFor("Intel(R) Processor N100", false, "BGA1264", false));
            Assert.Equal(CpuPackageStyle.DesktopIntel, CpuPackage.StyleFor("Intel(R) Celeron(R) G4900", false, "LGA1151", false));    // socket on the board: like a desktop
            Assert.Equal(CpuPackageStyle.Server, CpuPackage.StyleFor("Intel(R) Xeon(R) E5-2680 v4", false, "LGA2011-3", false));
            Assert.Equal(CpuPackageStyle.LaptopIntel, CpuPackage.StyleFor("Intel(R) Core(TM) i5-8250U", true, null, false));

            Assert.Equal("intel", CpuPackage.Brand("Intel(R) Core(TM) i5-4570"));
            Assert.Equal("RYZEN", CpuPackage.Brand("AMD Ryzen 5 5600X"));
            Assert.Equal("XEON", CpuPackage.Brand("Intel(R) Xeon(R) E5-2680 v4"));
            Assert.Equal("EPYC", CpuPackage.Brand("AMD EPYC 7302"));
            Assert.Equal("CELERON", CpuPackage.Brand("Intel(R) Celeron(R) N4020"));
            Assert.Equal("PENTIUM", CpuPackage.Brand("Intel(R) Pentium(R) Silver N5030"));
            Assert.Equal("Apple", CpuPackage.Brand("Apple M2"));
            Assert.Equal("Snapdragon", CpuPackage.Brand("Snapdragon(R) X Elite"));
            Assert.Equal("", CpuPackage.Brand("Genuine Unknown"));
        }

        [Fact]
        public void ArmSoc_ShowsCoresAndMemory_EntryHasALid()
        {
            var arm = CpuArt.Build(CpuPackageStyle.ArmSoc, "Apple", "M2");
            Assert.Equal(4, arm.Shapes.Count(s => s.Fill == ArtFill.Accent && Math.Abs(s.W - 10) < 0.01));      // performance cores
            Assert.Contains(arm.Shapes, s => s.Kind == ArtKind.Text && s.Text == "RAM");
            Assert.Contains(arm.Shapes, s => s.Kind == ArtKind.Text && s.Text == "Apple");
            var entry = CpuArt.Build(CpuPackageStyle.Entry, "CELERON", "Celeron N4020");
            Assert.Contains(entry.Shapes, s => s.Fill == ArtFill.Lid);
            Assert.DoesNotContain(entry.Shapes, s => s.Fill == ArtFill.Die);
            // the text fits on the lid: the family once, the model without the family word
            var texts = entry.Shapes.Where(s => s.Kind == ArtKind.Text).Select(s => s.Text).ToArray();
            Assert.Equal(new[] { "CELERON", "Celeron N4020" }.Length, texts.Length);
            Assert.Equal("N4020", texts[1]);
            Assert.All(entry.Shapes.Where(s => s.Kind == ArtKind.Text), s => Assert.True(s.X >= 42 && s.X + s.W <= 90));
        }

        [Theory]
        [InlineData("HDD", "ST500LM012 HN-M500MBB", "HDD 2.5")]
        [InlineData("HDD", "WDC WD10JPVX-22JC3T0", "HDD 2.5")]
        [InlineData("HDD", "TOSHIBA MQ01ABD100", "HDD 2.5")]
        [InlineData("HDD", "ST9500325AS", "HDD 2.5")]
        [InlineData("HDD", "HGST HTS541010A9E680", "HDD 2.5")]
        [InlineData("HDD", "ST1000DM010-2EP102", "HDD")]
        [InlineData("HDD", "WDC WD10EZEX-08WN4A0", "HDD")]
        [InlineData("HDD", "TOSHIBA DT01ACA100", "HDD")]
        [InlineData("External HDD", "WD My Passport", "External HDD")]
        public void PictureKind_HddSizeFollowsTheModel(string kind, string model, string expected)
            => Assert.Equal(expected, DriveForm.PictureKind(kind, model));

        [Fact]
        public void PictureKind_HddInALaptopIsAlways25()
        {
            Assert.Equal("HDD 2.5", DriveForm.PictureKind("HDD", "Unknown model", true));
            Assert.Equal("HDD", DriveForm.PictureKind("HDD", "Unknown model", false));
            Assert.Equal("SATA SSD", DriveForm.PictureKind("SATA SSD", "Samsung SSD 860 EVO", true));
        }

        [Fact]
        public void DriveHealth_NeverInventsAState()
        {
            Assert.Equal(DriveHealthState.Unknown, DriveHealth.From(100, false));
            Assert.Equal(DriveHealthState.Unknown, DriveHealth.From(0, true));
            Assert.Equal(DriveHealthState.Good, DriveHealth.From(90, true));
            Assert.Equal(DriveHealthState.Watch, DriveHealth.From(89, true));
            Assert.Equal(DriveHealthState.Watch, DriveHealth.From(60, true));
            Assert.Equal(DriveHealthState.Failing, DriveHealth.From(59, true));
        }

        [Fact]
        public void M2Sata_IsRecognisedByModel()
        {
            foreach (var m2 in new[]
            {
                "SAMSUNG MZNLN256HMHQ-000L7", "Samsung SSD 860 EVO M.2 500GB", "INTEL SSDSCKKF256G8H", "WDC WDS250G2B0B-00YS70",
                "CT500MX500SSD4", "KINGSTON SA400M8240G", "Micron MTFDDAV256TBN", "TS256GMTS800", "TOSHIBA THNSNK128GVN8",
                "SanDisk SD8SN8U-256G-1006", "LITEON CV8-8E128", "SK hynix SC308 HFS256G39TND-N210A",
            })
                Assert.True(DriveForm.IsM2Sata(m2), m2);

            foreach (var notM2 in new[]
            {
                "Samsung SSD 860 EVO 500GB", "CT500MX500SSD1", "KINGSTON SA400S37240G", "INTEL SSDSC2KW256G8", "WDC WDS500G2B0A-00SM50",
                "Micron MTFDDAK256TBN", "ST1000DM010-2EP102", "", null,
            })
                Assert.False(DriveForm.IsM2Sata(notM2), notM2 ?? "(null)");
        }

        [Fact]
        public void PictureKind_OnlyChangesSataSsds()
        {
            Assert.Equal("M.2 SATA SSD", DriveForm.PictureKind("SATA SSD", "CT500MX500SSD4"));
            Assert.Equal("M.2 SATA SSD", DriveForm.PictureKind("SSD", "SAMSUNG MZNLN256HMHQ"));
            Assert.Equal("SATA SSD", DriveForm.PictureKind("SATA SSD", "Samsung SSD 860 EVO 500GB"));
            Assert.Equal("NVMe SSD", DriveForm.PictureKind("NVMe SSD", "Samsung SSD 970 EVO M.2"));      // NVMe stays NVMe
            Assert.Equal("HDD", DriveForm.PictureKind("HDD", "ST1000DM010"));
            Assert.Equal("Unknown", DriveForm.PictureKind(null, null));
        }
    }
}
