using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;
using Border    = System.Windows.Controls.Border;
using TextBlock = System.Windows.Controls.TextBlock;
using Brushes   = System.Windows.Media.Brushes;

namespace SMDWin.Views
{
    /// <summary>
    /// Settings: the Theme thumbnails (drawn in SettingsPage.xaml, the ThumbChipStyle style in Styles.xaml) and the preview
    /// of the notification area icons, exactly as they look next to the clock, with the current values.
    /// </summary>
    public partial class MainWindow
    {
        private string _trayPreviewKey = "";

        /// <summary>The dark strip with the chosen icons (their real text and colour). Null / empty = hidden.
        /// Rebuilt only when something changes and only while the Settings page is visible.</summary>
        private void UpdateTrayPreview(List<(string text, System.Windows.Media.Color color, string label)>? items)
        {
            try
            {
                if (FindName("TrayPreviewStrip") is not Border strip || FindName("TrayPreviewIcons") is not StackPanel host) return;
                if (items == null || items.Count == 0)
                {
                    strip.Visibility = Visibility.Collapsed;
                    _trayPreviewKey = "";
                    return;
                }
                if (!strip.IsVisible && strip.Visibility == Visibility.Visible) return;   // the Settings page is not visible
                string key = string.Join("|", items.ConvertAll(i => $"{i.text}#{i.color}"));
                strip.Visibility = Visibility.Visible;
                if (key == _trayPreviewKey) return;
                _trayPreviewKey = key;

                host.Children.Clear();
                foreach (var (text, color, label) in items)
                {
                    var brush = new SolidColorBrush(color); brush.Freeze();
                    var icon = new Border
                    {
                        Width = 24, Height = 24, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 6, 0),
                        Background = Brushes.Transparent, ToolTip = label,
                        Child = new TextBlock
                        {
                            Text = text, Foreground = brush, FontWeight = FontWeights.Bold, FontSize = text.Length > 2 ? 10 : 12,
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center,
                        },
                    };
                    host.Children.Add(icon);
                }
                // the clock, so the strip reads as the taskbar
                var clock = new TextBlock
                {
                    Text = DateTime.Now.ToString("HH:mm"), FontSize = Ui.FontS, Margin = new Thickness(8, 0, 0, 0),
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE6, 0xE8, 0xEC)), VerticalAlignment = System.Windows.VerticalAlignment.Center,
                };
                host.Children.Add(clock);
            }
            catch (Exception ex) { AppLogger.Debug($"UpdateTrayPreview: {ex.Message}"); }
        }
    }
}
