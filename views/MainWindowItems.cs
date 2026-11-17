using System;
using SMDWin.Views;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SMDWin;
using SMDWin.Models;
using SMDWin.Services;
using Forms = System.Windows.Forms;
using Application      = System.Windows.Application;
using MessageBox       = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage  = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using SaveFileDialog   = Microsoft.Win32.SaveFileDialog;
using ToolTip          = System.Windows.Controls.ToolTip;
using OpenFileDialog   = Microsoft.Win32.OpenFileDialog;
using Button           = System.Windows.Controls.Button;
using Brush            = System.Windows.Media.Brush;
using WpfColor         = System.Windows.Media.Color;
using WpfPoint         = System.Windows.Point;
using WpfColorConv     = System.Windows.Media.ColorConverter;
using Line             = System.Windows.Shapes.Line;
using Ellipse          = System.Windows.Shapes.Ellipse;
using Rectangle        = System.Windows.Shapes.Rectangle;
using Polygon          = System.Windows.Shapes.Polygon;

namespace SMDWin.Views
{
    // ── Fan entry for ItemsControl binding ───────────────────────────────────
    public class FanEntry
    {
        public string Name    { get; set; } = "";
        public string RpmText { get; set; } = "";
    }

    // ── LAN Scanner live-binding model ───────────────────────────────────────
    public class LanScanItem
    {
        public string Ip          { get; set; } = "";
        public string Hostname    { get; set; } = "—";
        public string Vendor      { get; set; } = "—";
        public string PingMs      { get; set; } = "—";
        public string StatusColor { get; set; } = "#22C55E";
    }
}
