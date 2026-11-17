using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SMDWin.Views
{
    /// <summary>
    /// Explanations of technical terms (<see cref="Glossary"/>): a fixed label on the page gets a tooltip with the
    /// explanation. Runs after the texts are translated, so the explanation follows the chosen language too.
    /// </summary>
    public partial class MainWindow
    {
        private static readonly DependencyProperty GlossaryKeyProperty =
            DependencyProperty.RegisterAttached("GlossaryKey", typeof(string), typeof(MainWindow));

        private void ApplyGlossaryHints()
        {
            try
            {
                foreach (string name in _literalRoots)
                    if (FindName(name) is DependencyObject root) AddGlossaryHints(root);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ApplyGlossaryHints"); }
        }

        private void AddGlossaryHints(DependencyObject node)
        {
            bool leaf = false;
            if (node is FrameworkElement fe)
            {
                if (string.IsNullOrEmpty(fe.Name) && fe.Visibility == Visibility.Collapsed) return;
                if (!string.IsNullOrEmpty(fe.Name) && _literalSkipContainers.Contains(fe.Name)) return;

                if (fe is TextBlock tb)
                {
                    leaf = !HasInlineChildren(tb);
                    if (leaf && tb.ReadLocalValue(TextBlock.TextProperty) is string text)
                        TryAttachGlossary(tb, (tb.GetValue(SourceTextProperty) as string) ?? text, underline: true);
                }
                else if (fe is ContentControl cc && cc.ReadLocalValue(ContentControl.ContentProperty) is string content)
                    TryAttachGlossary(cc, (cc.GetValue(SourceTextProperty) as string) ?? content, underline: false);
            }

            if (leaf || node is ItemsControl) return;
            var children = new List<DependencyObject>();
            foreach (object child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject d) children.Add(d);
            foreach (var d in children) AddGlossaryHints(d);
        }

        private static void TryAttachGlossary(FrameworkElement element, string label, bool underline)
        {
            string? key = Glossary.KeyFor(label);
            if (key == null) return;
            // a tooltip set by code or in XAML takes precedence over the explanation
            bool ours = element.GetValue(GlossaryKeyProperty) is string;
            if (!ours && element.ReadLocalValue(FrameworkElement.ToolTipProperty) != DependencyProperty.UnsetValue) return;

            element.SetValue(GlossaryKeyProperty, key);
            element.ToolTip = Glossary.Text(key);
            ToolTipService.SetShowDuration(element, 20000);
            // no underline (it looked cluttered): the tooltip stays, the visual hint was removed
        }
    }
}
