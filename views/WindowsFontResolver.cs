using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SMDWin.Models;
using SMDWin.Services;

// Explicit aliases — avoid conflicts with System.Windows.Forms.* and System.IO.*
using WpfColor       = System.Windows.Media.Color;
using WpfButton      = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfTextBox     = System.Windows.Controls.TextBox;
using WpfClipboard   = System.Windows.Clipboard;
using WpfFontFamily  = System.Windows.Media.FontFamily;
using WpfHAlign      = System.Windows.HorizontalAlignment;
using WpfVAlign      = System.Windows.VerticalAlignment;
using WpfTabControl  = System.Windows.Controls.TabControl;
using WpfTabItem     = System.Windows.Controls.TabItem;
using WpfBrushes     = System.Windows.Media.Brushes;
using PenLineCap     = System.Windows.Media.PenLineCap;
using PenLineJoin    = System.Windows.Media.PenLineJoin;
using WpfPath        = System.Windows.Shapes.Path;
using WpfCursors     = System.Windows.Input.Cursors;
using WpfViewbox     = System.Windows.Controls.Viewbox;
using WpfCanvas      = System.Windows.Controls.Canvas;
using WpfPanel       = System.Windows.Controls.Panel;

namespace SMDWin.Views
{
    /// <summary>
    /// Resolves font family names to Windows font files so PdfSharp can embed
    /// fonts even when the GDI+ font subsystem is unavailable or restricted.
    /// </summary>
    public sealed class WindowsFontResolver : PdfSharp.Fonts.IFontResolver
    {
        // Map (familyName.lower, bold, italic) → filename under %WINDIR%\Fonts
        private static readonly Dictionary<(string, bool, bool), string> _map = new()
        {
            { ("arial",          false, false), "arial.ttf"    },
            { ("arial",          true,  false), "arialbd.ttf"  },
            { ("arial",          false, true),  "ariali.ttf"   },
            { ("arial",          true,  true),  "arialbi.ttf"  },
            { ("segoe ui",       false, false), "segoeui.ttf"  },
            { ("segoe ui",       true,  false), "segoeuib.ttf" },
            { ("segoe ui",       false, true),  "segoeuii.ttf" },
            { ("segoe ui",       true,  true),  "segoeuiz.ttf" },
            { ("segoe ui semibold", false, false), "seguisb.ttf" },
            { ("segoe ui semibold", true,  false), "seguisb.ttf" },
            { ("times new roman",false, false), "times.ttf"    },
            { ("times new roman",true,  false), "timesbd.ttf"  },
            { ("courier new",    false, false), "cour.ttf"     },
            { ("courier new",    true,  false), "courbd.ttf"   },
        };

        private static readonly string _fontsDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

        public PdfSharp.Fonts.FontResolverInfo? ResolveTypeface(
            string familyName, bool bold, bool italic)
        {
            var key = (familyName.ToLowerInvariant(), bold, italic);
            if (_map.TryGetValue(key, out var file) && Exists(file))
                return new PdfSharp.Fonts.FontResolverInfo(file);

            // Fall back: try non-bold/non-italic variant
            var keyPlain = (familyName.ToLowerInvariant(), false, false);
            if (_map.TryGetValue(keyPlain, out var filePlain) && Exists(filePlain))
                return new PdfSharp.Fonts.FontResolverInfo(filePlain);

            // the file is missing (Windows without the requested font): Arial, with the same weight
            if (_map.TryGetValue(("arial", bold, false), out var arial) && Exists(arial))
                return new PdfSharp.Fonts.FontResolverInfo(arial);
            return null;
        }

        private static bool Exists(string file) => File.Exists(Path.Combine(_fontsDir, file));

        public byte[]? GetFont(string faceName)
        {
            var path = Path.Combine(_fontsDir, faceName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
    }
}
