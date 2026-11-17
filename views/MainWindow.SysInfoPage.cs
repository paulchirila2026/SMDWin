using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SMDWin.Views
{
    /// <summary>
    /// The System Information page in the side menu: a panel created at start-up (so it is in the navigation tables), the
    /// content (<see cref="SystemInfoView"/>) is built on first open.
    /// </summary>
    public partial class MainWindow
    {
        private Grid? _panelSysInfo;
        private SystemInfoView? _sysInfoView;
        private int _sysInfoTabRequest = -1;
        private string? _sysInfoFocusRequest;

        private Grid CreateSysInfoPanel()
        {
            if (_panelSysInfo != null) return _panelSysInfo;
            _panelSysInfo = new Grid { Visibility = Visibility.Collapsed };
            _panelSysInfo.SetResourceReference(FrameworkElement.MarginProperty, "PageMargin");
            MainContentBorder.Children.Add(_panelSysInfo);
            return _panelSysInfo;
        }

        /// <summary>Opens the System Information page on the requested tab (0 = Hardware, 1 = Windows).</summary>
        private Task OpenSystemInfoPage(int tab)
        {
            _sysInfoTabRequest = tab;
            return NavigateTo("SysInfo");
        }

        /// <summary>Click on a component name in the Dashboard: System Information, on its card.</summary>
        internal void DashName_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.Tag is not string key) return;
            _sysInfoFocusRequest = key;
            _ = OpenSystemInfoPage(tab: key == "Windows" ? 1 : 0);
        }

        private async Task ShowSysInfoPageAsync()
        {
            if (_panelSysInfo == null) return;
            if (_sysInfoView == null)
            {
                _sysInfoView = new SystemInfoView();
                _panelSysInfo.Children.Add(_sysInfoView);
            }
            if (_sysInfoTabRequest >= 0)
            {
                _sysInfoView.ShowTab(_sysInfoTabRequest);
                _sysInfoTabRequest = -1;
            }
            if (_sysInfoFocusRequest != null)
            {
                _sysInfoView.FocusCard(_sysInfoFocusRequest);
                _sysInfoFocusRequest = null;
            }
            await _sysInfoView.RefreshAsync(force: false);
        }

        /// <summary>Developer › Design experiments: the page layout changed — the page is rebuilt.</summary>
        internal void ApplySysInfoLayoutExperiment() => ResetSysInfoPage();

        /// <summary>After a theme change: the page is rebuilt with the new colours (immediately if it is the one open now).</summary>
        private void ResetSysInfoPage()
        {
            if (_panelSysInfo == null || _sysInfoView == null) return;
            int tab = _sysInfoView.CurrentTab;
            _panelSysInfo.Children.Clear();
            _sysInfoView = null;
            if (_currentPanel == "SysInfo")
            {
                _sysInfoTabRequest = tab;
                _ = ShowSysInfoPageAsync();
            }
        }
    }
}
