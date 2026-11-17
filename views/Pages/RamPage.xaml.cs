using System.Linq;

namespace SMDWin.Views.Pages
{
    /// <summary>The "Ram" page — interface only; the logic is in MainWindow (the MainWindow.*.cs files).</summary>
    public partial class RamPage : System.Windows.Controls.UserControl
    {
        public RamPage() { InitializeComponent(); _ready = true; }

        // events fired by the XAML load itself (e.g. IsChecked="True") do not reach MainWindow: at that point the page is not
        // yet attached to the window and its elements are not reachable from there
        private readonly bool _ready;

        // the main window; null while the page is being built (before it is placed in the window)
        private SMDWin.Views.MainWindow? Main =>
            System.Windows.Window.GetWindow(this) as SMDWin.Views.MainWindow
            ?? System.Windows.Application.Current?.Windows.OfType<SMDWin.Views.MainWindow>().FirstOrDefault();

        // the XAML handlers → the same methods in MainWindow
        private void OpenMemDiag_Click(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.OpenMemDiag_Click(s, e); }
        private void PanelRamScroll_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e) { if (_ready) Main?.PanelRamScroll_ScrollChanged(sender, e); }
        private void RunAdvancedRamTests_Click(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.RunAdvancedRamTests_Click(s, e); }
        private void RunBasicRamTests_Click(object s, System.Windows.RoutedEventArgs e) { if (_ready) Main?.RunBasicRamTests_Click(s, e); }
    }
}
