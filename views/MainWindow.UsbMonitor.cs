using System.Windows;

namespace SMDWin.Views
{
    public partial class MainWindow
    {
        // Tools › USB monitor — one window at a time (a second click brings it to the front)
        private void BtnUsbMonitor_Click(object sender, RoutedEventArgs e) => UsbMonitorWindow.ShowSingle(this);
    }
}
