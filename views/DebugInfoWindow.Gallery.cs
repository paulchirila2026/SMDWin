using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SMDWin.Services;

namespace SMDWin.Views
{
    public partial class DebugInfoWindow
    {
        /// <summary>
        /// The illustration gallery (Design experiments): all drawings in <see cref="ArtCatalog"/>, by group, plus the disks by
        /// type and health state, memory modules, monitors, the battery, the Windows area and the card icons. Shows at a glance
        /// what exists and how it looks in the current theme.
        /// </summary>
        private void AddIllustrationGallery(System.Windows.Controls.StackPanel panel)
        {
            System.Windows.Controls.TextBlock Heading(string text, double top)
            {
                var h = new System.Windows.Controls.TextBlock { Text = text, FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 8) };
                h.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextPrimaryBrush");
                return h;
            }
            System.Windows.Controls.Border Tile(FrameworkElement picture, string label, double width, double pictureHeight)
            {
                var card = new System.Windows.Controls.Border
                {
                    Width = width, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(8), CornerRadius = new CornerRadius(8),
                };
                card.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BgCardBrush");
                var stack = new System.Windows.Controls.StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
                var holder = new System.Windows.Controls.Grid { Height = pictureHeight, Margin = new Thickness(0, 0, 0, 6) };
                picture.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
                picture.VerticalAlignment = System.Windows.VerticalAlignment.Center;
                holder.Children.Add(picture);
                stack.Children.Add(holder);
                var name = new System.Windows.Controls.TextBlock { Text = label, FontSize = Ui.FontXS, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
                name.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextSecondaryBrush");
                stack.Children.Add(name);
                card.Child = stack;
                return card;
            }

            panel.Children.Add(Heading("Illustration library", 20));
            foreach (var group in ArtCatalog.All.GroupBy(e => e.Group))
            {
                panel.Children.Add(Heading(group.Key, 4));
                var wrap = new System.Windows.Controls.WrapPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                foreach (var entry in group)
                {
                    try
                    {
                        var picture = new ArtPicture { Drawing = entry.Build(), Width = 132, Height = 108 };
                        var tile = Tile(picture, entry.Label, 160, 112);
                        tile.ToolTip = entry.Key;
                        wrap.Children.Add(tile);
                    }
                    catch (Exception ex) { AppLogger.Debug($"Illustration gallery {entry.Key}: {ex.Message}"); }
                }
                panel.Children.Add(wrap);
            }

            panel.Children.Add(Heading("Drives · by type and health", 4));
            var drives = new System.Windows.Controls.WrapPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            foreach (string kind in new[] { "HDD", "HDD 2.5", "SATA SSD", "M.2 SATA SSD", "NVMe SSD", "External HDD", "External SSD", "External drive", "USB stick", "SD card" })
                foreach (var state in new[] { DriveHealthState.Unknown, DriveHealthState.Good, DriveHealthState.Watch, DriveHealthState.Failing })
                {
                    try
                    {
                        var drive = new System.Windows.Controls.Viewbox { Width = 96, Height = 66, Child = HardwareIllustrations.Drive(kind, state) };
                        var tile = Tile(drive, $"{kind} · {state}", 120, 70);
                        tile.ToolTip = $"{kind} / {state}";
                        drives.Children.Add(tile);
                    }
                    catch (Exception ex) { AppLogger.Debug($"Illustration gallery drive {kind}/{state}: {ex.Message}"); }
                }
            panel.Children.Add(drives);

            panel.Children.Add(Heading("Memory slots", 4));
            var slots = new System.Windows.Controls.WrapPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            var dimm = new System.Windows.Controls.Viewbox { Height = 34, Child = HardwareIllustrations.Dimm("DDR4", "DIMM") };
            slots.Children.Add(Tile(dimm, "Module", 160, 40));
            slots.Children.Add(Tile(new System.Windows.Controls.Viewbox { Height = 34, Child = HardwareIllustrations.EmptySlot("DIMM") }, "Free slot", 160, 40));
            panel.Children.Add(slots);

            // memory modules by type and form factor
            panel.Children.Add(Heading("Memory modules · by type", 4));
            var modules = new System.Windows.Controls.WrapPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            foreach (var (type, form) in new[] { ("DDR3", "DIMM"), ("DDR4", "DIMM"), ("DDR5", "DIMM"), ("DDR3", "SODIMM"), ("DDR4", "SODIMM"), ("DDR5", "SODIMM"), ("LPDDR4", "SODIMM") })
            {
                var moduleView = new System.Windows.Controls.Viewbox { Height = 34, Child = HardwareIllustrations.Dimm(type, form) };
                modules.Children.Add(Tile(moduleView, $"{type} · {form}", 160, 40));
            }
            panel.Children.Add(modules);

            // monitors: one screen, several, ultrawide, all-in-one
            panel.Children.Add(Heading("Displays", 4));
            var screens = new System.Windows.Controls.WrapPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            var screenSets = new (string Label, MonitorSpec[] Items)[]
            {
                ("Laptop screen", new[] { new MonitorSpec(1920, 1080, 18, true, true) }),
                ("Desktop 16:9", new[] { new MonitorSpec(1920, 1080, 30, true, false) }),
                ("Ultrawide", new[] { new MonitorSpec(3440, 1440, 34, true, false) }),
                ("Two screens", new[] { new MonitorSpec(1920, 1080, 30, true, false, "1"), new MonitorSpec(1920, 1080, 30, false, false, "2") }),
                ("Three screens", new[] { new MonitorSpec(2560, 1440, 34, true, false, "1"), new MonitorSpec(1920, 1080, 30, false, false, "2"), new MonitorSpec(1080, 1920, 30, false, false, "3") }),
                ("All-in-one", new[] { new MonitorSpec(1920, 1080, 30, true, true, "", true) }),
            };
            foreach (var (setLabel, setItems) in screenSets)
                screens.Children.Add(Tile(HardwareIllustrations.Monitors(setItems), setLabel, 160, 112));
            panel.Children.Add(screens);

            // battery: the block variants, at a few charge levels
            panel.Children.Add(Heading("Battery · styles and levels", 4));
            var batteries = new System.Windows.Controls.WrapPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            foreach (var (style, styleLabel) in BatteryBlocks.Styles)
                foreach (var (percent, charging) in new[] { (90.0, false), (50.0, false), (18.0, false), (6.0, false), (60.0, true) })
                {
                    var battery = new BatteryPicture { Variant = style, Percent = percent, Charging = charging };
                    batteries.Children.Add(Tile(new System.Windows.Controls.Viewbox { Height = 60, Child = battery },
                        $"{styleLabel} · {percent:0}%{(charging ? " ⚡" : "")}", 150, 66));
                }
            panel.Children.Add(batteries);

            // the Windows area in System Info
            panel.Children.Add(Heading("Windows card", 4));
            var windows = new System.Windows.Controls.WrapPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            windows.Children.Add(Tile(SystemInfoView.WindowsLogo(true), "Windows 11", 160, 56));
            windows.Children.Add(Tile(SystemInfoView.WindowsLogo(false), "Windows 10", 160, 56));
            windows.Children.Add(Tile(SystemInfoView.LicenseBadge(true), "Licensed", 160, 92));
            windows.Children.Add(Tile(SystemInfoView.LicenseBadge(false), "Not licensed", 160, 92));
            foreach (var (sb, tpm, av, active, known, ringLabel) in new (bool?, bool?, bool?, int, int, string)[]
            {
                (true, true, true, 3, 3, "Protection · all on"),
                (false, true, true, 2, 3, "Protection · 2 of 3"),
                (false, false, false, 0, 3, "Protection · none"),
                (null, null, null, 0, 0, "Protection · unknown"),
            })
                windows.Children.Add(Tile(SystemInfoView.ProtectionRing(new[] { sb, tpm, av, sb, tpm }, active, known), ringLabel, 160, 92));
            panel.Children.Add(windows);

            // the card icons (the same keys as in System Info)
            panel.Children.Add(Heading("Card icons", 4));
            var icons = new System.Windows.Controls.WrapPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            foreach (string key in InfoCardBuilder.Icons.Keys)
                icons.Children.Add(Tile(SpecSheet.IconTile(key, 36), key, 90, 40));
            panel.Children.Add(icons);
        }
    }
}
