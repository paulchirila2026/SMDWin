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
    public partial class MainWindow : Window
    {
        // ── DISK ──────────────────────────────────────────────────────────────

        private async Task LoadDisksInternalAsync(bool forceRefresh = false)
        {
            // If forceRefresh, invalidate the cache (useful when an external disk is connected/disconnected)
            if (forceRefresh)
                _hwService.InvalidateDiskCache();

            // Show cached disks immediately (no forced refresh)
            if (!forceRefresh && _allDisks.Count > 0)
            {
                PopulateDiskPanel(_allDisks);
            }

            ShowLoading(_LS("Disk", "LoadingDisks"));
            try
            {
                var fresh = await _hwService.GetDisksAsync();
                if (fresh.Count > 0) _allDisks = fresh;
                PopulateDiskPanel(_allDisks);

                // Also update the combos in the other tabs
                PopulateSmartDiskCombo();
                PopulateSurfaceDiskCombo();
                if (CbBenchDrive != null) PopulateBenchDriveCombo();

                // Fire SMART/health notification if any disk needs attention
                try { CheckSmartNotif(_allDisks); } catch (Exception logEx) { AppLogger.Warning(logEx, "CheckSmartNotif(_allDisks);"); }
            }
            finally { HideLoading(); }
        }

        /// <summary>
        /// Populates DiskPanel with disk cards.
        /// ≤2 disks → single column (full width).
        /// ≥3 disks → 2-column WrapPanel — each card ~half width.
        /// </summary>
        private void PopulateDiskPanel(List<DiskHealthEntry> disks)
        {
            DiskPanel.Children.Clear();

            // Save SMART snapshots (max 1/h per disk)
            foreach (var disk in disks)
            {
#pragma warning disable CS4014
                _ = Task.Run(() => SmartHistoryService.SaveSnapshot(disk));
#pragma warning restore CS4014
            }

            if (disks.Count >= 3)
            {
                // 2-column layout: pair cards side by side in a WrapPanel
                var wrap = new System.Windows.Controls.WrapPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                };
                foreach (var disk in disks)
                {
                    var card = BuildDiskCard(disk);
                    // Make each card take ~half the panel width minus gap
                    if (card is FrameworkElement fe)
                    {
                        fe.Margin = new Thickness(0, 0, 0, 0); // existing card has bottom margin
                    }
                    var container = new System.Windows.Controls.Border
                    {
                        Padding = new Thickness(0, 0, 7, 7),
                        Child = card,
                    };
                    // Bind container width to half of WrapPanel's actual width
                    container.Loaded += (_, _) =>
                    {
                        double panelW = DiskPanel.ActualWidth;
                        if (panelW > 800) // wide enough for 2 columns
                            container.Width = panelW / 2.0 - 4;
                    };
                    DiskPanel.SizeChanged += (_, e) =>
                    {
                        if (e.NewSize.Width > 800)
                            container.Width = e.NewSize.Width / 2.0 - 4;
                        else
                            container.Width = double.NaN; // full width on narrow panels
                    };
                    wrap.Children.Add(container);
                }
                DiskPanel.Children.Add(wrap);
            }
            else
            {
                // Single column for 1-2 disks
                foreach (var disk in disks)
                    DiskPanel.Children.Add(BuildDiskCard(disk));
            }
            BuildStorageDriveRows(disks); // the per-disk rows are in MainWindow.StoragePage.cs; DiskPanel stays hidden
        }

        // ── TRIM + Partition Health ───────────────────────────────────────────


        private async void LoadStorageAdvancedAsync()
        {
            // Populate APM info (HDD only)
            try
            {
                var apmInfos = await DiskAdvancedService.GetApmInfoAsync(_allDisks);
                var apmEntries = apmInfos.Select(a => new ApmDisplayEntry
                {
                    DriveLetter  = a.DriveLetter,
                    LevelLabel   = a.LevelLabel,
                    CurrentLevel = a.CurrentLevel
                }).Where(a => a.CurrentLevel > 0).ToList();   // only the disks that actually report APM (HDDs)

                if (ApmPanel != null)
                {
                    ApmPanel.Visibility = apmEntries.Count > 0
                        ? Visibility.Visible : Visibility.Collapsed;
                }
                if (ApmList != null) ApmList.ItemsSource = apmEntries;
                if (TxtApmNote != null)
                    TxtApmNote.Text = apmEntries.Count > 0
                        ? "APM level is read-only in this version. Use manufacturer tools to change it."
                        : "";
            }
            catch (Exception ex) { AppLogger.Warning(ex, "MainWindow.Hardware.LoadStorageAdvancedAsync.APM"); }

            // Populate TRIM status
            try
            {
                var trimStatuses = await DiskAdvancedService.GetTrimStatusAsync(_allDisks);
                var entries = trimStatuses.Select(t => new TrimDisplayEntry
                {
                    DriveLetter = t.DriveLetter,
                    Description = t.Description
                }).ToList();
                if (TrimList != null) TrimList.ItemsSource = entries;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "LoadStorageAdvancedAsync.TRIM"); }

            // Populate health drive combo
            try
            {
                if (CbHealthDrive != null)
                {
                    var ready = await SMDWin.Services.DriveSnapshot.ReadyDrivesAsync();
                    CbHealthDrive.Items.Clear();
                    foreach (var di in ready)
                    {
                        if (di.Type != System.IO.DriveType.Fixed) continue;
                        CbHealthDrive.Items.Add(di.Root.TrimEnd('\\'));
                    }
                    if (CbHealthDrive.Items.Count > 0) CbHealthDrive.SelectedIndex = 0;
                }
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }
        }


        // ── Storage Tabs ─────────────────────────────────────────────────────

        internal void StorageTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string tag = btn.Tag?.ToString() ?? "Overview";

            StorageTabOverview.Visibility = tag == "Overview" ? Visibility.Visible : Visibility.Collapsed;
            StorageTabSmart   .Visibility = tag == "Smart"    ? Visibility.Visible : Visibility.Collapsed;
            StorageTabSurface .Visibility = tag == "Surface"  ? Visibility.Visible : Visibility.Collapsed;
            StorageTabBench   .Visibility = tag == "Bench"    ? Visibility.Visible : Visibility.Collapsed;
            StorageTabFiles   .Visibility = tag == "Files"    ? Visibility.Visible : Visibility.Collapsed;

            SetTabButtonActive(BtnStorageTabOverview, tag == "Overview");
            SetTabButtonActive(BtnStorageTabSmart,    tag == "Smart");
            SetTabButtonActive(BtnStorageTabSurface,  tag == "Surface");
            SetTabButtonActive(BtnStorageTabBench,    tag == "Bench");
            SetTabButtonActive(BtnStorageTabFiles,    tag == "Files");

            if (tag == "Smart"   && CbSmartDisk.Items.Count == 0)   PopulateSmartDiskCombo();
            if (tag == "Surface" && CbSurfaceDisk.Items.Count == 0) PopulateSurfaceDiskCombo();
            if (tag == "Bench"   && CbBenchDrive.Items.Count == 0)  PopulateBenchDriveCombo();
            if (tag == "Bench") UpdateBenchEmptyState();
            if (tag == "Surface") UpdateSurfaceEmptyState();
            if (tag == "Files" && SpaceHost != null && SpaceHost.Child == null) SpaceHost.Child = new SpaceView();
            UpdateStorageTopRow(tag); // the shared Disk activity / All drives card
        }

        private void UpdateSurfaceEmptyState()
        {
            if (_histVals == null) UpdateSurfaceHistogram(new long[6], false);
            if (FindName("SurfaceEmptyState") is Border empty)
            {
                bool none = _sectorMapSeverity.Length == 0;
                empty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
                if (none && empty.Child is not Border) FillSurfaceStartCard(empty);
            }
        }

        internal void BtnStorageDevMgr_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("diskmgmt.msc") { UseShellExecute = true }); }
            catch (Exception ex) { AppLogger.Warning(ex, "MainWindow.Hardware.BtnStorageDevMgr_Click"); }
        }

        internal void OpenDiskpart_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName       = "cmd.exe",
                    Arguments      = "/c diskpart",
                    Verb           = "runas",
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { AppLogger.Warning(ex, "MainWindow.Hardware.OpenDiskpart_Click"); }
        }
    }
}
