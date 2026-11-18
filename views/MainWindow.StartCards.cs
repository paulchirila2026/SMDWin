using System;
using System.Windows;
using System.Windows.Controls;

namespace SMDWin.Views
{
    public partial class MainWindow
    {
        // Icons for the start cards (24×24 canvas)
        internal const string StartIconBolt   = "F1 M7.43,2.83 C7.6,2.33 8.07,2 8.6,2 L15.06,2 C15.91,2 16.51,2.84 16.24,3.65 L14.8,8 L18.76,8 C19.86,8 20.43,9.33 19.66,10.12 L8.59,21.54 C7.53,22.62 5.71,21.64 6.04,20.16 L7.31,14.5 L5.75,14.49 C4.54,14.49 3.7,13.29 4.1,12.15 Z";
        internal const string StartIconSearch = "F1 M16.1,17.16 A8,8 0 1 1 17.16,16.1 L21.78,20.72 A0.75,0.75 0 1 1 20.72,21.78 Z M17.5,11 A6.5,6.5 0 0 0 4.5,11 A6.5,6.5 0 0 0 17.5,11";
        internal const string StartIconGauge  = "M12,3 A10,10 0 0 0 2,13 A10,10 0 0 0 3.6,18.4 L5.3,17.4 A8,8 0 0 1 4,13 A8,8 0 0 1 20,13 A8,8 0 0 1 18.7,17.4 L20.4,18.4 A10,10 0 0 0 22,13 A10,10 0 0 0 12,3 Z M16.2,7.4 L12.9,12.1 A1.6,1.6 0 1 1 11.6,11.1 Z";
        internal const string StartIconPing   = "M4,20 L4,15 L7,15 L7,20 Z M10,20 L10,11 L13,11 L13,20 Z M16,20 L16,6 L19,6 L19,20 Z";

        private bool _benchCardHooked;

        /// <summary>The Benchmark tab's empty area becomes the start card (drive shown, big Start button).</summary>
        private void FillBenchStartCard(Border host)
        {
            if (!_benchCardHooked && CbBenchDrive != null)
            {
                _benchCardHooked = true;
                CbBenchDrive.SelectionChanged += (_, _) => { if (host.Visibility == Visibility.Visible) FillBenchStartCard(host); };
            }
            string drive = CbBenchDrive?.SelectedItem?.ToString() ?? "";
            host.Padding = new Thickness(26, 30, 26, 30);
            host.Child = StartCard.Build(StartIconBolt,
                PT("Disk benchmark"),
                PT("Writes and reads temporary test files to measure how fast this drive really is. The files are deleted at the end."),
                new[] { PT("about 1–2 minutes"), PT("needs about 2 GB free"), PT("your files are not touched") },
                PT("Start benchmark"),
                () => RunFullBench_Click(BtnRunFullBench, new RoutedEventArgs()),
                drive.Length > 0 ? StartCard.InfoLine(PT("Drive"), drive) : null,
                framed: false);
        }

        /// <summary>The empty block map of Surface scan shows the start card on top.</summary>
        private void FillSurfaceStartCard(Border host)
        {
            host.IsHitTestVisible = true;
            host.Padding = new Thickness(12, 6, 12, 6);
            host.Child = StartCard.Build(StartIconSearch,
                PT("Surface scan"),
                PT("Reads every area of the drive to find slow or unreadable spots. It only reads — nothing is written."),
                new[] { PT("long on large hard drives"), PT("can be stopped at any time") },
                PT("Start scan"),
                () => SurfaceScan_Start_Click(BtnSurfaceStart, new RoutedEventArgs()),
                compact: true);
        }
    }
}
