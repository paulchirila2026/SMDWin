using System.Windows;
using System.Windows.Controls;
using DataGrid = System.Windows.Controls.DataGrid;

namespace SMDWin.Views
{
    /// <summary>
    /// Keeps the list pages readable in a small window: the less important columns step out when the table gets narrow
    /// (instead of every column being squeezed until its text is cut: "Stoppe", "4.2 (", "Installe"), and the details card on
    /// the right gets narrower on a narrow page.
    /// </summary>
    internal static class ResponsiveLayout
    {
        /// <summary>Hides each column (by its index) while the table is narrower than the given width.</summary>
        public static void HideColumns(DataGrid? grid, params (int Index, double HideBelow)[] rules)
        {
            if (grid == null) return;
            void Apply()
            {
                double w = grid.ActualWidth;
                if (w <= 0) return;
                foreach (var (i, below) in rules)
                    if (i >= 0 && i < grid.Columns.Count)
                    {
                        var vis = w < below ? Visibility.Collapsed : Visibility.Visible;
                        if (grid.Columns[i].Visibility != vis) grid.Columns[i].Visibility = vis;
                    }
            }
            grid.SizeChanged += (_, _) => Apply();
            grid.Loaded += (_, _) => Apply();
        }

        /// <summary>The details column: 340 px, 280 px when the page is narrower than the breakpoint.</summary>
        public static void SidePanel(FrameworkElement? page, ColumnDefinition? col, double wide = 340, double narrow = 280, double breakpoint = 1100)
        {
            if (page == null || col == null) return;
            page.SizeChanged += (_, _) =>
            {
                double w = page.ActualWidth < breakpoint ? narrow : wide;
                if (col.Width.Value != w) col.Width = new GridLength(w);
            };
        }
    }

    public partial class MainWindow
    {
        private bool? _toolsNarrow;

        /// <summary>
        /// The 6 cards of Tools (repair, clean-up, package, timer, snapshots, activity): 3 per row, or 2 per row when narrow.
        /// The snapshots card keeps its span when the activity card is hidden (RefreshToolActivity).
        /// </summary>
        private void LayoutToolsCards(Grid grid, bool narrow)
        {
            if (_toolsNarrow == narrow) return;
            _toolsNarrow = narrow;
            var cells = new System.Collections.Generic.List<UIElement>();
            foreach (UIElement c in grid.Children) cells.Add(c);
            if (cells.Count < 6) return;
            grid.ColumnDefinitions[3].Width = new GridLength(narrow ? 0 : 12);
            grid.ColumnDefinitions[4].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            grid.RowDefinitions[3].Height = new GridLength(narrow ? 12 : 0);
            for (int i = 0; i < 6; i++)
            {
                int row = narrow ? (i / 2) * 2 : (i / 3) * 2;
                int col = narrow ? (i % 2) * 2 : (i % 3) * 2;
                Grid.SetRow(cells[i], row);
                Grid.SetColumn(cells[i], col);
            }
            RefreshToolActivity();
        }

        /// <summary>Applications, Services, Process Monitor and Startup in a small window (see ResponsiveLayout).</summary>
        private void SetupResponsiveSoftwarePages()
        {
            try
            {
                // Applications: name · size · installed · version
                ResponsiveLayout.HideColumns(AppsGrid, (3, 640), (2, 540));
                // Services: service · status · starts · risk
                ResponsiveLayout.HideColumns(ServicesGrid, (2, 560));
                // Startup: on · program · impact · where
                ResponsiveLayout.HideColumns(StartupGrid, (3, 600));
                // Processes: process · CPU · memory · I/O · network · PID
                ResponsiveLayout.HideColumns(ProcessesGrid, (5, 760), (4, 660), (3, 580));
                ResponsiveLayout.SidePanel(PanelApps, ColAppsSide);
                ResponsiveLayout.SidePanel(PanelServices, ColSvcSide);
                ResponsiveLayout.SidePanel(PanelStartup, ColStartupSide);
                // Tools: 3 cards per row, 2 on a narrow page (the cards were squeezed: "Status: Ena", "12 tweaks, each se")
                if (FindName("PanelShutdown") is FrameworkElement tools && FindName("ToolsCardsGrid") is Grid tg)
                    tools.SizeChanged += (_, _) => LayoutToolsCards(tg, tools.ActualWidth < 1180);
            }
            catch (System.Exception ex) { SMDWin.Services.AppLogger.Debug($"SetupResponsiveSoftwarePages: {ex.Message}"); }
        }
    }
}
