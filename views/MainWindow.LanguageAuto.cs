using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using SMDWin.Services;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SMDWin.Views
{
    /// <summary>
    /// Translation of the texts written directly in XAML. Many titles, labels and explanations on the pages have no name
    /// (x:Name), so they do not appear in ApplyLanguage and used to stay in English whatever the chosen language.
    /// Here the content area is walked once on every language change: every unnamed text, every tooltip, the right-click
    /// menus, the column headers and a few named elements with fixed text are translated by their English text
    /// (<see cref="LiteralTexts"/>).
    /// </summary>
    public partial class MainWindow
    {
        // On each element, for the text and for the tooltip: the English text from XAML (captured at start — translation always
        // starts from it) and the last translation written here (to see whether the code changed it in the meantime).
        private static readonly DependencyProperty SourceTextProperty =
            DependencyProperty.RegisterAttached("LiteralSourceText", typeof(string), typeof(MainWindow));
        private static readonly DependencyProperty SourceTipProperty =
            DependencyProperty.RegisterAttached("LiteralSourceTip", typeof(string), typeof(MainWindow));
        private static readonly DependencyProperty WrittenTextProperty =
            DependencyProperty.RegisterAttached("LiteralWrittenText", typeof(string), typeof(MainWindow));
        private static readonly DependencyProperty WrittenTipProperty =
            DependencyProperty.RegisterAttached("LiteralWrittenTip", typeof(string), typeof(MainWindow));


        /// <summary>Lists built in code (they rebuild their own texts on a language change).</summary>
        private static readonly HashSet<string> _literalSkipContainers = new(StringComparer.Ordinal)
        {
            "WidgetMetricChips", "TrayMetricChips", "LangPacksPanel",
        };

        /// <summary>
        /// Where the walk starts: the content area (all pages), the window buttons and the warning dot in the sidebar.
        /// The sidebar labels are translated in ApplyLanguage.
        /// </summary>
        private static readonly string[] _literalRoots = { "MainContentBorder", "CaptionButtonsPanel", "SidebarStressThrottleDot", "SidebarSearchHint", "HealthPillBorder" };

        /// <summary>A short text set by code, in the current language (English, Romanian, German, French).</summary>
        private static string T4(string en, string ro, string de, string fr)
            => LanguageService.CurrentCode switch { "ro" => ro, "de" => de, "fr" => fr, _ => en };

        /// <summary>
        /// A text set by code, translated through the shared page texts (section PageText of Languages/*.json, keyed by the English
        /// text). For texts with values, the English template is passed ("{0} free") and filled in after: PT("{0} free", size).
        /// </summary>
        internal static string PT(string en) => LiteralTexts.Translate(en);
        internal static string PT(string en, params object?[] args) => string.Format(LiteralTexts.Translate(en), args);

        private enum LiteralPass
        {
            Capture,     // only captures the English XAML text of each element
            Translate,   // writes the translation in the current language
            List,        // records the texts still in English (for the self-test)
        }

        private void WalkLiterals(LiteralPass pass, List<string>? missing)
        {
            foreach (string name in _literalRoots)
                if (FindName(name) is DependencyObject root) TranslateLiteralsIn(root, pass, missing);
        }

        /// <summary>
        /// Captures the English XAML texts right after the window loads — before the data or ApplyLanguage change anything.
        /// This way, later, "the displayed text is still the XAML one" surely means "a fixed, untranslated text".
        /// </summary>
        private void CaptureLiteralSources()
        {
            try { WalkLiterals(LiteralPass.Capture, null); }
            catch (Exception ex) { AppLogger.Warning(ex, "CaptureLiteralSources"); }
        }

        /// <summary>
        /// Translates a part built after the window loaded (a Network tool, the tool tabs): its English texts are captured and
        /// translated now; on a later language change the normal walk (MainContentBorder) covers it as well.
        /// </summary>
        internal void TranslateLateContent(DependencyObject root)
        {
            try
            {
                TranslateLiteralsIn(root, LiteralPass.Capture, null);
                TranslateLiteralsIn(root, LiteralPass.Translate, null);
            }
            catch (Exception ex) { AppLogger.Debug($"TranslateLateContent: {ex.Message}"); }
        }

        /// <summary>Translates the fixed page texts into the current language. Called at the end of ApplyLanguage.</summary>
        private void TranslatePageLiterals()
        {
            try
            {
                WalkLiterals(LiteralPass.Translate, null);
                ScheduleFitSettingsPage();   // the texts have a different length in another language
            }
            catch (Exception ex) { AppLogger.Warning(ex, "TranslatePageLiterals"); }
        }

        /// <summary>
        /// The fixed page texts still shown in English in the current language: those without a translation in the language
        /// pack and not translated by ApplyLanguage either (for the self-test). In English the list is empty.
        /// </summary>
        internal List<string> ListUntranslatedPageTexts()
        {
            var missing = new List<string>();
            if (LanguageService.CurrentCode == "en") return missing;
            try { WalkLiterals(LiteralPass.List, missing); }
            catch (Exception ex) { AppLogger.Warning(ex, "ListUntranslatedPageTexts"); }
            return missing;
        }

        // ── Settings page without scrolling ─────────────────────────────────────────────────────────────

        /// <summary>The page shrinks by at most this much to fit; below this threshold the text would be hard to read,
        /// so the page stays at normal size and scrolls.</summary>
        private const double SettingsMinScale = 0.88;
        private bool _fittingSettingsPage;

        internal void SettingsPage_SizeChanged(object sender, SizeChangedEventArgs e) { LayoutSettingsColumns(); ScheduleFitSettingsPage(); }

        internal void SettingsPage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is true) { DecorateSettingsRows(); if (TxtSettingsSearchHint != null) TxtSettingsSearchHint.Text = PT("Search settings…"); ForwardSidebarQueryToSettings(TxtSidebarSearch?.Text?.Trim() ?? ""); LayoutSettingsColumns(); ScheduleFitSettingsPage(); }
        }

        private void ScheduleFitSettingsPage() =>
            Dispatcher.BeginInvoke(new Action(FitSettingsPage), System.Windows.Threading.DispatcherPriority.Loaded);

        /// <summary>
        /// App rule: pages scroll only when needed. On small screens (compact density) and with longer texts (Romanian, German)
        /// the Settings page comes out slightly taller than the window. Instead of a scroll bar, the content shrinks exactly
        /// enough to fit, at most down to <see cref="SettingsMinScale"/>.
        /// </summary>
        private void FitSettingsPage()
        {
            if (_fittingSettingsPage) return;
            if (FindName("PanelSettings") is not ScrollViewer page || FindName("SettingsPageContent") is not FrameworkElement content) return;
            if (!page.IsVisible || page.ActualHeight <= 0) return;
            _fittingSettingsPage = true;
            try
            {
                // always start from normal size: after the window is enlarged the page returns to 100% by itself
                content.LayoutTransform = System.Windows.Media.Transform.Identity;
                page.UpdateLayout();
                double scale = 1.0;
                for (int pass = 0; pass < 3; pass++)
                {
                    double extent = page.ExtentHeight, viewport = page.ViewportHeight;
                    if (viewport <= 0 || extent <= viewport + 0.5) break;
                    scale *= viewport / extent;
                    if (scale < SettingsMinScale) { scale = 1.0; break; }
                    // narrower → texts may wrap onto one more line; that is why it is measured again
                    content.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
                    page.UpdateLayout();
                }
                if (scale >= 0.999) content.LayoutTransform = System.Windows.Media.Transform.Identity;
            }
            catch (Exception ex) { AppLogger.Debug($"FitSettingsPage: {ex.Message}"); }
            finally { _fittingSettingsPage = false; }
        }

        private void TranslateLiteralsIn(DependencyObject node, LiteralPass pass, List<string>? missing)
        {
            bool leaf = false;
            if (node is FrameworkElement fe)
            {
                bool named = !string.IsNullOrEmpty(fe.Name);
                // Hidden containers without a name are not reported to the self-test (some only hold old fields that are never shown).
                // Their texts are still captured and translated: many Dashboard cells are hidden only until data arrives (Visibility
                // bound to the value) and used to stay in English after they appeared.
                if (!named && fe.Visibility == Visibility.Collapsed && pass == LiteralPass.List) return;
                if (named && _literalSkipContainers.Contains(fe.Name)) return;
                // The text of a named element is translated too, as long as it is still exactly the XAML one (or the translation written
                // here): many named labels are never touched by code and used to stay in English. A text set by code in the meantime
                // (ApplyLanguage, data) is no longer the XAML one, so it stays the code's — see Apply.
                bool visitText = true;

                if (fe is TextBlock tb)
                {
                    // A TextBlock with plain text is a leaf. (The Inlines property is not touched: reading it would turn it into a
                    // composite one.) A composite one has its pieces as children, taken below.
                    leaf = !HasInlineChildren(tb);
                    // only texts written literally: one bound to a resource or a binding is not touched
                    if (leaf && visitText && fe.ReadLocalValue(TextBlock.TextProperty) is string)
                        Apply(fe, SourceTextProperty, WrittenTextProperty, tb.Text, text => tb.Text = text, pass, missing);
                }
                else if (visitText && fe is ContentControl cc && fe.ReadLocalValue(ContentControl.ContentProperty) is string content)
                    Apply(fe, SourceTextProperty, WrittenTextProperty, content, text => cc.Content = text, pass, missing);

                // a tooltip translated in ApplyLanguage has no key here and stays untouched
                if (fe.ReadLocalValue(FrameworkElement.ToolTipProperty) is string tip)
                    Apply(fe, SourceTipProperty, WrittenTipProperty, tip, text => fe.ToolTip = text, pass, missing);

                // the right-click menu is not part of the page tree
                if (fe.ContextMenu is ContextMenu menu) TranslateMenuItems(menu.Items, pass, missing);

                if (fe is DataGrid grid)
                    foreach (var column in grid.Columns)
                        if (column.Header is string header)
                            Apply(column, SourceTextProperty, WrittenTextProperty, header, text => column.Header = text, pass, missing);
            }
            else if (node is System.Windows.Documents.Run run && string.IsNullOrEmpty(run.Name)
                     && run.ReadLocalValue(System.Windows.Documents.Run.TextProperty) is string)
            {
                // a text piece of a composed row ("12 GB" + " free / " + "512 GB")
                Apply(run, SourceTextProperty, WrittenTextProperty, run.Text, text => run.Text = text, pass, missing);
            }

            if (leaf || node is ItemsControl) return;   // list items (e.g. the languages) are not page texts
            // the children are collected into a list first: writing a text may change the parent's collection during the walk
            var children = new List<DependencyObject>();
            foreach (object child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject d) children.Add(d);
            foreach (var d in children) TranslateLiteralsIn(d, pass, missing);
        }

        /// <summary>true if the TextBlock is composed of pieces (Run, Span…), not a single text.</summary>
        private static bool HasInlineChildren(TextBlock tb)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(tb))
                if (child is DependencyObject) return true;
            return false;
        }

        private void TranslateMenuItems(ItemCollection items, LiteralPass pass, List<string>? missing)
        {
            foreach (object item in items)
            {
                if (item is not MenuItem entry) continue;
                if (string.IsNullOrEmpty(entry.Name) && entry.Header is string header)
                    Apply(entry, SourceTextProperty, WrittenTextProperty, header, text => entry.Header = text, pass, missing);
                if (entry.Items.Count > 0) TranslateMenuItems(entry.Items, pass, missing);
            }
        }

        /// <summary>
        /// A fixed text, per pass.
        /// Capture: captures the English XAML text. Elements that appear later (built by code) have no captured text and are
        /// never touched.
        /// Translate: writes the translation, if any; in English or without a translation it puts back the XAML text, if the
        /// element had been translated. A text changed by code in the meantime (neither the XAML one nor the last translation
        /// written here) stays the code's.
        /// List: records the text if it has no translation and still looks exactly as in XAML.
        /// </summary>
        private static void Apply(DependencyObject element, DependencyProperty sourceSlot, DependencyProperty writtenSlot,
                                  string current, Action<string> write, LiteralPass pass, List<string>? missing)
        {
            if (pass == LiteralPass.Capture)
            {
                if (element.GetValue(sourceSlot) == null && LiteralTexts.IsTranslatable(current))
                    element.SetValue(sourceSlot, current);
                return;
            }
            if (element.GetValue(sourceSlot) is not string src) return;
            if (pass == LiteralPass.List)
            {
                if (missing != null && current == src && !LiteralTexts.HasTranslation(src) && !missing.Contains(src)) missing.Add(src);
                return;
            }

            string? written = element.GetValue(writtenSlot) as string;
            if (current != src && current != written) return;
            if (LanguageService.CurrentCode != "en" && LiteralTexts.HasTranslation(src))
            {
                string translated = LiteralTexts.Translate(src);
                if (translated != current) write(translated);
                element.SetValue(writtenSlot, translated);
            }
            else if (written != null)
            {
                if (current != src) write(src);
                element.ClearValue(writtenSlot);
            }
        }
    }
}
