using System;
using System.Collections.Generic;
using System.Linq;

namespace SMDWin.Services
{
    /// <summary>One illustration from the library: group, stable key, label and drawing.</summary>
    public sealed record ArtEntry(string Group, string Key, string Label, Func<ArtDrawing> Build);

    /// <summary>
    /// The illustration library in one place: every drawing has a stable key ("cpu.ArmSoc", "gpu.desktop.NVIDIA",
    /// "peripheral.Headphones"). The gallery in Developer › Design experiments and the tests walk the same list, and
    /// pages take their drawing by key, without drawing code of their own.
    /// </summary>
    public static class ArtCatalog
    {
        private static readonly Dictionary<CpuPackageStyle, string> CpuSamples = new()
        {
            [CpuPackageStyle.LaptopIntel]         = "Intel Core i5-8250U",
            [CpuPackageStyle.LaptopIntelGraphics] = "Intel Core i7-8750H",
            [CpuPackageStyle.LaptopAmd]           = "AMD Ryzen 7 5800H",
            [CpuPackageStyle.DesktopIntel]        = "Intel Core i5-4570",
            [CpuPackageStyle.DesktopAmd]          = "AMD Ryzen 5 5600X",
            [CpuPackageStyle.Server]              = "Intel Xeon E5-2680 v4",
            [CpuPackageStyle.Entry]               = "Intel Celeron N4020",
            [CpuPackageStyle.ArmSoc]              = "Apple M2",
        };

        public static IReadOnlyList<ArtEntry> All { get; } = Create();

        public static ArtEntry? Find(string key) => All.FirstOrDefault(e => e.Key == key);

        private static IReadOnlyList<ArtEntry> Create()
        {
            var list = new List<ArtEntry>();

            foreach (var (style, label) in CpuPackage.Styles)
            {
                string sample = CpuSamples.TryGetValue(style, out var n) ? n : "";
                list.Add(new ArtEntry("Processors", $"cpu.{style}", label,
                    () => CpuArt.Build(style, CpuPackage.Brand(sample), CpuPackage.ShortName(sample))));
            }

            foreach (var (vendor, vram) in new[] { ("NVIDIA", 8.0), ("AMD", 16.0), ("Intel", 12.0) })
            {
                string v = vendor; double gb = vram;
                list.Add(new ArtEntry("Graphics cards", $"gpu.desktop.{v}", $"{v} · desktop · {gb:0} GB",
                    () => GpuArt.Build(GpuPictureStyle.DesktopCard, 2, false, v, gb)));
            }
            list.Add(new ArtEntry("Graphics cards", "gpu.desktop.NVIDIA.3fans", "NVIDIA · three fans · 24 GB", () => GpuArt.Build(GpuPictureStyle.DesktopCard, 3, false, "NVIDIA", 24)));
            list.Add(new ArtEntry("Graphics cards", "gpu.desktop.AMD.1fan", "AMD · one fan · 4 GB", () => GpuArt.Build(GpuPictureStyle.DesktopCard, 1, false, "AMD", 4)));
            foreach (var vendor in new[] { "NVIDIA", "AMD", "Intel" })
            {
                string v = vendor;
                list.Add(new ArtEntry("Graphics cards", $"gpu.laptop.{v}", $"{v} · laptop chip · 6 GB", () => GpuArt.Build(GpuPictureStyle.LaptopChip, 2, false, v, 6)));
            }
            list.Add(new ArtEntry("Graphics cards", "gpu.integrated", "Integrated", () => GpuArt.Build(GpuPictureStyle.Integrated)));

            foreach (var kind in Enum.GetValues<PeripheralKind>())
            {
                var k = kind;
                list.Add(new ArtEntry("Peripherals", $"peripheral.{k}", k.ToString(), () => PeripheralArt.Build(k)));
            }
            list.Add(new ArtEntry("Peripherals", "peripheral.set", "Several devices (grid)",
                () => PeripheralArt.BuildSet(new[] { PeripheralKind.Keyboard, PeripheralKind.Headphones, PeripheralKind.Phone })));

            foreach (var kind in Enum.GetValues<NetworkKind>())
            {
                var k = kind;
                list.Add(new ArtEntry("Network", $"network.{k}", k.ToString(), () => NetworkArt.Build(k)));
            }
            list.Add(new ArtEntry("Network", "network.wifi.strong", "Wi-Fi · strong (82%)", () => NetworkArt.BuildWifi(82, true)));
            list.Add(new ArtEntry("Network", "network.wifi.medium", "Wi-Fi · medium (48%)", () => NetworkArt.BuildWifi(48, true)));
            list.Add(new ArtEntry("Network", "network.wifi.weak", "Wi-Fi · weak (18%)", () => NetworkArt.BuildWifi(18, true)));
            list.Add(new ArtEntry("Network", "network.wifi.off", "Wi-Fi · not connected", () => NetworkArt.BuildWifi(0, false)));
            foreach (var kind in Enum.GetValues<AppKind>())
            {
                var k = kind;
                list.Add(new ArtEntry("Applications", $"app.{k}", AppKinds.Label(k), () => AppArt.Build(k)));
            }
            foreach (var kind in Enum.GetValues<ComputerKind>())
            {
                var k = kind;
                list.Add(new ArtEntry("Computers", $"computer.{k}", k.ToString(), () => ComputerArt.Build(k)));
            }
            foreach (var kind in Enum.GetValues<BoardKind>())
            {
                var k = kind;
                list.Add(new ArtEntry("Motherboards", $"board.{k}", k.ToString(), () => BoardArt.Build(k)));
            }
            return list;
        }
    }
}
