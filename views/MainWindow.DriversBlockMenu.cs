using System;
using System.Windows;
using System.Windows.Controls;
using SMDWin.Services;

namespace SMDWin.Views
{
    /// <summary>
    /// Drivers: the "Block updates ▾" button with the menu of the three blocks — the graphics driver, the driver of one
    /// device, all driver updates. The label of the global option comes from BtnToggleGlobalBlock (hidden), which the code
    /// keeps up to date ("Block driver updates" / "Allow driver updates").
    /// </summary>
    public partial class MainWindow
    {
        private static string DB(string key, string en) => LanguageService.S("Drivers", key, en);

        internal void DriverBlockMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement anchor) return;
            try
            {
                var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

                System.Windows.Controls.MenuItem Item(string header, string tip, RoutedEventHandler handler)
                {
                    var mi = new System.Windows.Controls.MenuItem { Header = header, ToolTip = tip };
                    mi.Click += (s, a) => handler(s, a);
                    menu.Items.Add(mi);
                    return mi;
                }

                Item(DB("BlockMenuGpu", "Block the graphics card driver"),
                     DB("BlockMenuGpuTip", "Windows Update will not replace the driver the graphics card uses now."),
                     BlockGpuDriver_Click);
                Item(DB("BlockMenuDevice", "Block a device's driver…"),
                     DB("BlockMenuDeviceTip", "Choose a device whose driver Windows Update must not replace."),
                     BlockCustomDriver_Click);
                menu.Items.Add(new System.Windows.Controls.Separator());
                string global = BtnToggleGlobalBlock?.Content as string is { Length: > 0 } g ? g : DB("BlockMenuGlobal", "Block all driver updates");
                Item(global,
                     DB("BlockMenuGlobalTip", "Windows Update stops installing drivers for every device (can be turned back on here)."),
                     ToggleGlobalDriverBlock_Click);

                menu.IsOpen = true;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "DriverBlockMenu_Click"); }
        }
    }
}
