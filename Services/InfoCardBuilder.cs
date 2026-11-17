using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

using WpfColor    = System.Windows.Media.Color;
using WpfBrush    = System.Windows.Media.Brush;
using WpfBrushes  = System.Windows.Media.Brushes;
using WpfViewbox  = System.Windows.Controls.Viewbox;
using WpfCanvas   = System.Windows.Controls.Canvas;
using WpfPath     = System.Windows.Shapes.Path;
using WpfTextBox  = System.Windows.Controls.TextBox;
using WpfHAlign   = System.Windows.HorizontalAlignment;
using WpfVAlign   = System.Windows.VerticalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfButton   = System.Windows.Controls.Button;
using WpfFontFamily = System.Windows.Media.FontFamily;

namespace SMDWin.Services
{
    public static class InfoCardBuilder
    {
        /// <summary>Real SVG paths taken from views/IconsLight.xaml (the same icons used in the sidebar and the rest of the app)
        /// — 24x24 viewBox, round/round stroke, thickness 1.75, exactly like the original.</summary>
        public static readonly Dictionary<string, string[]> Icons = new()
        {
            ["System"]     = new[] { "M3 5h18v12H3z M8 21h8 M12 17v4" },
            ["Cpu"]        = new[] { "M9 9h6v6H9z M5 5h14v14H5z M9 2v3 M15 2v3 M9 19v3 M15 19v3 M2 9h3 M2 15h3 M19 9h3 M19 15h3" },
            ["Ram"]        = new[] { "M3 7h18v9H3z M7 16v3 M11 16v3 M15 16v3 M7 10v3 M11 10v3 M15 10v3" },
            ["Gpu"]        = new[] { "M2 7h18v10H2z M20 10h2 M6 17v3 M11 12a2 2 0 1 0 0.01 0 M15 12a2 2 0 1 0 0.01 0" },
            ["Disk"]       = new[] { "M4 5a8 3 0 1 0 16 0a8 3 0 1 0 -16 0 M4 5v14c0 1.7 3.6 3 8 3s8-1.3 8-3V5 M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3" },
            ["Battery"]    = new[] { "M3 8h15v8H3z M18 11h3v2h-3" },
            ["Chassis"]    = new[] { "M7,2 H17 A1,1 0 0 1 18,3 V21 A1,1 0 0 1 17,22 H7 A1,1 0 0 1 6,21 V3 A1,1 0 0 1 7,2 Z", "M9,6 H15", "M9,9 H15", "M12,15 A1.6,1.6 0 1 0 12.01,15" },
            ["Motherboard"]= new[] { "M2 4 H22 V20 H2 Z", "M5 8 H11 V14 H5 Z M8 5 V8 M8 14 V17 M11 11 H14", "M13 9 H21 M13 12 H21 M13 15 H21", "M15 4 H17 V7 H15 Z M18.5 4 H20.5 V7 H18.5 Z" },
            ["Monitor"]    = new[] { "M4 4h16v10H4z M8 18h8 M12 14v4" },
            ["Wifi"]       = new[] { "M2 9a15 15 0 0 1 20 0 M5 12.5a10 10 0 0 1 14 0 M8.5 16a5 5 0 0 1 7 0 M12 19.5h.01" },
            ["Bluetooth"]  = new[] { "M6.5,6.5 L17.5,17.5 L12,23 L12,1 L17.5,6.5 L6.5,17.5" },
            ["Usb"]        = new[] { "M9 7l3-5 3 5z", "M12 7v15", "M12 12l-4 4", "M5.2,17 A1.8,1.8 0 1 0 8.8,17 A1.8,1.8 0 1 0 5.2,17 Z", "M12 14l4 4", "M14,16 H17.5 V19.5 H14 Z" },
            ["Speaker"]    = new[] { "M6,2 H18 A2,2 0 0 1 20,4 V20 A2,2 0 0 1 18,22 H6 A2,2 0 0 1 4,20 V4 A2,2 0 0 1 6,2 Z", "M8.5,15 A3.5,3.5 0 1 0 15.5,15 A3.5,3.5 0 1 0 8.5,15 Z", "M11,6 A1,1 0 1 0 13,6 A1,1 0 1 0 11,6 Z" },
            ["Drivers"]    = new[] { "M9,3 H15 A2,2 0 0 1 17,5 V19 A2,2 0 0 1 15,21 H9 A2,2 0 0 1 7,19 V5 A2,2 0 0 1 9,3 Z", "M12,17 A0.5,0.5 0 1 0 12.01,17" },
            // Windows/Shield — used by the Windows information view
            ["Windows"]    = new[] { "M3,5 L11,4 V11.5 H3 Z", "M12,4 L21,3 V11.5 H12 Z", "M3,12.5 H11 V20 L3,19 Z", "M12,12.5 H21 V21 L12,20 Z" },
            ["Shield"]     = new[] { "M9 12l2 2 4-4 M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z" },
            ["Key"]        = new[] { "M15,7 A4,4 0 1 1 15,15 A4,4 0 1 1 15,7 Z", "M12,10 L3,19 M3,19 V22 H6 M6,22 V19 H9" },
            ["Clock"]      = new[] { "M12 3a9 9 0 1 0 0.01 0 M12 7v5l3 2" },
            // the information in the large cards of the System Information window
            ["Bolt"]       = new[] { "M13 2L4 14h7l-2 8 11-13h-7z" },                                   // putere
            ["Layers"]     = new[] { "M12 3l9 5-9 5-9-5z M3 13l9 5 9-5" },   // cache
            ["Socket"]     = new[] { "M4,4 H20 V20 H4 Z", "M9,4 V20 M15,4 V20 M4,9 H20 M4,15 H20" },            // soclu
            ["Gauge"]      = new[] { "M4.5,18 A9,9 0 1 1 19.5,18", "M12,13 L16,8" },                            // turbo
            ["Channels"]   = new[] { "M4,8 H20 M16,4 L20,8 L16,12", "M20,16 H4 M8,12 L4,16 L8,20" },            // memory channels
            ["Mobile"]     = new[] { "M5,20 V17", "M10,20 V13", "M15,20 V9", "M20,20 V5" },                         // mobile internet (signal bars)
            ["Ethernet"]   = new[] { "M9,3 H15 V8 H9 Z", "M3,16 H9 V21 H3 Z", "M15,16 H21 V21 H15 Z", "M12,8 V12 M6,16 V12 H18 V16" },   // retea prin cablu
            ["Camera"]     = new[] { "M3,7 H7 L9,4 H15 L17,7 H21 V19 H3 Z", "M12,9.5 A3.5,3.5 0 1 0 12.01,9.5" },
            ["Keyboard"]   = new[] { "M2,6 H22 V18 H2 Z", "M6,10 H6.01 M10,10 H10.01 M14,10 H14.01 M18,10 H18.01 M7,14 H17" },
            ["Mouse"]      = new[] { "M7,9 A5,5 0 0 1 17,9 V15 A5,5 0 0 1 7,15 Z", "M12,4 V10" },
            ["Printer"]    = new[] { "M7,9 V3 H17 V9", "M7,17 H4 V9 H20 V17 H17", "M7,14 H17 V21 H7 Z" },
            ["Phone"]      = new[] { "M8,2 H16 A1,1 0 0 1 17,3 V21 A1,1 0 0 1 16,22 H8 A1,1 0 0 1 7,21 V3 A1,1 0 0 1 8,2 Z", "M11,18.5 H13" },
            ["Headphones"] = new[] { "M4,15 V12 A8,8 0 0 1 20,12 V15", "M4,14 H7 V20 H4 Z", "M17,14 H20 V20 H17 Z" },
            ["Globe"]      = new[] { "M12,3 A9,9 0 1 0 12.01,3 Z", "M3,12 H21", "M12,3 C8.5,6.5 8.5,17.5 12,21", "M12,3 C15.5,6.5 15.5,17.5 12,21" },   // network (the card header)
            ["Router"]     = new[] { "M3,15 H21 V20 H3 Z", "M7,17.5 H7.01", "M8,12 A5.5,5.5 0 0 1 16,12", "M5.5,9.5 A9,9 0 0 1 18.5,9.5" },
            ["Ruler"]      = new[] { "M3,17 L17,3 L21,7 L7,21 Z", "M7.5,13.5 L9.5,15.5 M10.5,10.5 L12.5,12.5 M13.5,7.5 L15.5,9.5" },     // the screen diagonal
        };

        public class Theme
        {
            public WpfColor BgCard, Border, TextPrimary, TextSecondary, TextMuted, Accent;
        }


        private const string EyeIconPath = "eye";
        private const string EyeOffIconPath = "eye_off";

        private static WpfButton MakeIconButton(Theme t, string svgPath, string tooltip)
        {
            var btn = new WpfButton
            {
                Width = 24, Height = 24, Margin = new Thickness(4, 0, 0, 0),
                Background = WpfBrushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand, ToolTip = tooltip,
                Padding = new Thickness(0),
            };
            var vb = new WpfViewbox { Width = 13, Height = 13 };
            var cv = new WpfCanvas { Width = 24, Height = 24 };
            cv.Children.Add(new WpfPath { Data = FluentIcons.Get(svgPath), Fill = new SolidColorBrush(t.TextSecondary) });
            vb.Child = cv;
            btn.Content = vb;
            return btn;
        }

    }
}
