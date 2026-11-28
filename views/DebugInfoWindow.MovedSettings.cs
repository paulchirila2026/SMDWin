using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using SMDWin.Services;
using Application = System.Windows.Application;
using Border      = System.Windows.Controls.Border;
using Orientation = System.Windows.Controls.Orientation;
using StackPanel  = System.Windows.Controls.StackPanel;
using TextBlock   = System.Windows.Controls.TextBlock;

namespace SMDWin.Views
{
    public partial class DebugInfoWindow
    {
        // The cards below are not part of Settings for now (they may come back later). They are the real cards, moved here
        // while this window is open, so the options work exactly as they did in Settings.
        private static readonly string[] MovedCardNames = { "SetCardSensors", "SetCardData" };
        private readonly List<(string name, Border card)> _borrowed = new();

        UIElement BuildMovedSettingsPage()
        {
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 20, 24, 20) };
            var panel = new StackPanel { Orientation = Orientation.Vertical, MaxWidth = 760, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            scroll.Content = panel;

            var note = new TextBlock
            {
                Text = "Cards taken out of Settings for now. They work as before: changes are saved automatically.",
                FontSize = Ui.FontS, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            panel.Children.Add(note);

            var main = Application.Current.MainWindow as MainWindow;
            foreach (var name in MovedCardNames)
            {
                var card = main?.BorrowSettingsCard(name);
                if (card == null) continue;
                _borrowed.Add((name, card));
                panel.Children.Add(card);
            }
            if (_borrowed.Count == 0)
            {
                var none = new TextBlock { Text = "The Settings page is not loaded yet. Open Settings once, then open this tab again.", FontSize = Ui.FontS };
                none.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                panel.Children.Add(none);
            }

            // when this window closes, the cards go back to their holder in Settings (hidden there)
            Closed += (_, _) =>
            {
                try
                {
                    foreach (var (name, card) in _borrowed)
                    {
                        panel.Children.Remove(card);
                        (Application.Current.MainWindow as MainWindow)?.ReturnSettingsCard(name, card);
                    }
                    _borrowed.Clear();
                }
                catch (Exception ex) { AppLogger.Warning(ex, "DebugInfoWindow moved settings"); }
            };
            return scroll;
        }
    }
}
