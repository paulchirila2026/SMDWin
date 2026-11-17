using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfToolTip        = System.Windows.Controls.ToolTip;

namespace SMDWin.Views
{
    public static class TechTooltip
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(TechTooltip), new PropertyMetadata(null, OnTextChanged));

        public static void SetText(DependencyObject el, string value) => el.SetValue(TextProperty, value);
        public static string GetText(DependencyObject el) => (string)el.GetValue(TextProperty);

        // Remember the ORIGINAL (short) tooltip of each control with TechTooltip attached, to put it back exactly as it was when
        // Alt is released. A simple dictionary is enough — the number of controls with this attribute is small and fixed (the
        // main action buttons), with no real risk of a leak during the run.
        private static readonly Dictionary<FrameworkElement, object?> _originalTooltips = new();

        private static bool _altDown;
        private static FrameworkElement? _hovered;

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement fe) return;
            fe.MouseEnter -= Fe_MouseEnter;
            fe.MouseLeave -= Fe_MouseLeave;
            if (!string.IsNullOrEmpty(e.NewValue as string))
            {
                fe.MouseEnter += Fe_MouseEnter;
                fe.MouseLeave += Fe_MouseLeave;
            }
        }

        private static void Fe_MouseEnter(object sender, WpfMouseEventArgs e)
        {
            var fe = (FrameworkElement)sender;
            _hovered = fe;
            if (_altDown) ShowTech(fe);
        }

        private static void Fe_MouseLeave(object sender, WpfMouseEventArgs e)
        {
            var fe = (FrameworkElement)sender;
            if (ReferenceEquals(_hovered, fe)) _hovered = null;
            RestoreOriginal(fe);
        }

        /// <summary>The single entry point for the global Alt key state — called from PreviewKeyDown/PreviewKeyUp on the main
        /// window. Alt in WPF comes as a SystemKey (Key.System), not as a Key directly — the caller must check both
        /// (see MainWindow.cs).</summary>
        public static void SetAltState(bool down)
        {
            if (_altDown == down) return;
            _altDown = down;
            if (down)
            {
                var target = FindTechAncestor(Mouse.DirectlyOver as DependencyObject) ?? _hovered;
                if (target != null) { _hovered = target; ShowTech(target); }
            }
            else if (_hovered != null) RestoreOriginal(_hovered);
        }

        /// <summary>Walks up the VISUAL tree (not the logical one — Mouse.DirectlyOver can be an element inside a template, e.g.
        /// a Path/TextBlock in a Button) until it finds the first ancestor with TechTooltip.Text set, or null.</summary>
        private static FrameworkElement? FindTechAncestor(DependencyObject? start)
        {
            var cur = start;
            while (cur != null)
            {
                if (cur is FrameworkElement fe && !string.IsNullOrEmpty(GetText(fe)))
                    return fe;
                cur = (cur is System.Windows.Media.Visual || cur is System.Windows.Media.Media3D.Visual3D)
                    ? System.Windows.Media.VisualTreeHelper.GetParent(cur)
                    : null;
            }
            return null;
        }

        private static void ShowTech(FrameworkElement fe)
        {
            if (!_originalTooltips.ContainsKey(fe))
                _originalTooltips[fe] = fe.ToolTip;
            fe.ToolTip = new WpfToolTip { Content = GetText(fe), IsOpen = true };
        }

        private static void RestoreOriginal(FrameworkElement fe)
        {
            if (fe.ToolTip is WpfToolTip openTip) openTip.IsOpen = false;
            if (_originalTooltips.TryGetValue(fe, out var orig))
            {
                fe.ToolTip = orig;
                _originalTooltips.Remove(fe);
            }
        }
    }
}
