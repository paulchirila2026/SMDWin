using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SMDWin.Services;

using Application         = System.Windows.Application;
using Brush               = System.Windows.Media.Brush;
using Brushes             = System.Windows.Media.Brushes;
using Button              = System.Windows.Controls.Button;
using Canvas              = System.Windows.Controls.Canvas;
using FontFamily          = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox          = System.Windows.MessageBox;
using Orientation         = System.Windows.Controls.Orientation;
using Rectangle           = System.Windows.Shapes.Rectangle;
using ToolTip             = System.Windows.Controls.ToolTip;
using MouseEventArgs      = System.Windows.Input.MouseEventArgs;
using Cursors             = System.Windows.Input.Cursors;
using VerticalAlignment   = System.Windows.VerticalAlignment;
using WpfColor            = System.Windows.Media.Color;
using WpfPoint            = System.Windows.Point;

namespace SMDWin.Views
{
    internal class StringToBrushConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            try { return new SolidColorBrush((WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(value?.ToString() ?? "#888")); }
            catch { return System.Windows.Media.Brushes.Gray; }
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
    }

    internal class BoolToOnOffConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
            => (value is bool b && b) ? "ON" : "OFF";
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
    }

    internal class FractionWidthConverter : System.Windows.Data.IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                double fraction   = values.Length > 0 && values[0] is double f  ? f  : 0.0;
                double trackWidth = values.Length > 1 && values[1] is double tw ? tw : 0.0;
                fraction = Math.Max(0.0, Math.Min(1.0, fraction));
                if (trackWidth <= 0 || double.IsNaN(trackWidth)) return 2.0; // fallback minim vizibil
                return Math.Max(2.0, fraction * trackWidth);
            }
            catch { return 2.0; }
        }
        public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c) => throw new NotImplementedException();
    }
}

namespace SMDWin.Views
{
    /// <summary>A colour from the model (text "#22C55E" or a brush) → the brush to display, with the status colours of the
    /// current theme.</summary>
    public sealed class ThemeStatusBrushConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
            => value is System.Windows.Media.Brush b ? b : SMDWin.Services.StatusColors.ToBrush(value?.ToString());
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
    }
}
