using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Application    = System.Windows.Application;
using Button         = System.Windows.Controls.Button;
using Color          = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace SMDWin.Views
{
    /// <summary>
    /// Themed replacement for MessageBox.Show().
    /// Usage:
    ///   AppDialog.Show("Message");
    ///   bool yes = AppDialog.Confirm("Are you sure?");
    ///   AppDialog.Show("msg", "Title", AppDialog.Kind.Warning);
    /// </summary>
    public partial class AppDialog : Window
    {
        public enum Kind { Info, Warning, Error, Success }
        public enum Buttons { Ok, OkCancel, YesNo }

        private bool _result = false;
        private int _choice = -1;

        public AppDialog()
        {
            InitializeComponent();
            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
            SourceInitialized += (_, _) =>
            {
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    SMDWin.Services.ThemeManager.ApplyTitleBarColor(hwnd, SMDWin.Services.SettingsService.Current.ThemeName);
                    {
                        string _resolved = SMDWin.Services.ThemeManager.Normalize(SMDWin.Services.SettingsService.Current.ThemeName);
                        if (SMDWin.Services.ThemeManager.Themes.TryGetValue(_resolved, out var _t))
                            SMDWin.Services.ThemeManager.SetCaptionColor(hwnd, _t["BgDark"]);
                    }
                }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            };
        }

        // ──────────────────────────────────────────────────────────────
        //  Static helpers
        // ──────────────────────────────────────────────────────────────

        /// <summary>Show a simple informational dialog and wait for OK.</summary>
        public static void Show(string message, string title = "SMD Win", Kind kind = Kind.Info, Window? owner = null)
        {
            ShowCore(message, title, kind, Buttons.Ok, owner);
        }

        /// <summary>Show a Yes/No dialog. Returns true if Yes was clicked.</summary>
        public static bool Confirm(string message, string title = "SMD Win", Window? owner = null)
        {
            return ShowCore(message, title, Kind.Warning, Buttons.YesNo, owner);
        }

        /// <summary>
        /// Confirmation for a destructive action (uninstall, delete): the confirm button is RED and carries the action label
        /// (e.g. "Uninstall"), not a generic "Yes". The red appears only here, at the moment of decision — not permanently
        /// in the page's button bar.
        /// </summary>
        public static bool ConfirmDanger(string message, string title, string confirmLabel,
                                         string cancelLabel = "Cancel", Window? owner = null)
        {
            _pendingDanger = (confirmLabel, cancelLabel);
            try { return ShowCore(message, title, Kind.Warning, Buttons.YesNo, owner); }
            finally { _pendingDanger = null; }
        }
        [ThreadStatic] private static (string Confirm, string Cancel)? _pendingDanger;

        /// <summary>
        /// Multiple-choice dialog: a cancel button + one button per option (the last one is the primary one).
        /// Returns the index of the chosen option (0..n-1) or -1 on cancel / close.
        /// </summary>
        public static int Choose(string message, string title, string cancelLabel, params string[] options)
        {
            _pendingChoices = (cancelLabel, options);
            try
            {
                ShowCore(message, title, Kind.Info, Buttons.YesNo, null);
                return _lastChoice;
            }
            finally { _pendingChoices = null; }
        }
        [ThreadStatic] private static (string Cancel, string[] Options)? _pendingChoices;
        [ThreadStatic] private static int _lastChoice;


        // ──────────────────────────────────────────────────────────────
        //  Core builder
        // ──────────────────────────────────────────────────────────────

        private static bool ShowCore(string message, string title, Kind kind, Buttons buttons, Window? owner)
        {
            var dlg = new AppDialog();

            // Copy ALL live theme resources (including runtime keys set by ThemeManager.Apply)
            // MergedDictionaries alone misses runtime-set keys like BgCardColor, AccentBrush etc.
            if (Application.Current?.Resources != null)
            {
                foreach (var key in Application.Current.Resources.Keys)
                {
                    try { dlg.Resources[key] = Application.Current.Resources[key]; } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                }
                foreach (var rd in Application.Current.Resources.MergedDictionaries)
                    if (!dlg.Resources.MergedDictionaries.Contains(rd))
                        dlg.Resources.MergedDictionaries.Add(rd);
            }

            if (!dlg.Resources.Contains("BgCardBrush"))
            {
                try
                {
                    string normalized = SMDWin.Services.ThemeManager.Normalize(SMDWin.Services.SettingsService.Current.ThemeName);
                    SMDWin.Services.ThemeManager.Apply(normalized, dlg.Resources);
                }
                catch (Exception ex) { SMDWin.Services.AppLogger.Debug($"AppDialog.ShowCore theme fallback: {ex.Message}"); }
            }

            dlg.TxtTitle.Text   = title;
            dlg.TxtMessage.Text = message;
            dlg.SetIcon(kind);
            dlg.BuildButtons(buttons, kind);

            if (owner != null)
                dlg.Owner = owner;
            else if (Application.Current?.MainWindow is Window mw && mw.IsLoaded)
                dlg.Owner = mw;

            dlg.Loaded += (_, _) =>
            {
                try
                {
                    dlg.Topmost = true;
                    dlg.Activate();
                    dlg.Focus();
                    dlg.Topmost = false; // it must not stay topmost permanently, only when it appears
                }
                catch (Exception ex) { SMDWin.Services.AppLogger.Debug($"AppDialog activate: {ex.Message}"); }
            };

            dlg.ShowDialog();
            _lastChoice = dlg._choice;
            return dlg._result;
        }

        // ──────────────────────────────────────────────────────────────
        //  Icon setup
        // ──────────────────────────────────────────────────────────────

        private void SetIcon(Kind kind)
        {
            // Fluent filled icon for each kind
            (string icon, string colorHex) = kind switch
            {
                Kind.Warning => ("warning_filled",          "#F59E0B"),
                Kind.Error   => ("error_circle_filled",     "#EF4444"),
                Kind.Success => ("checkmark_circle_filled", "#22C55E"),
                _            => ("info_filled",             "#3B82F6"),
            };

            IconPath.Data = SMDWin.Services.FluentIcons.Get(icon);
            IconPath.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
        }

        // ──────────────────────────────────────────────────────────────
        //  Button builder
        // ──────────────────────────────────────────────────────────────

        private void BuildButtons(Buttons kind, Kind dlgKind)
        {
            ButtonPanel.Children.Clear();

            if (_pendingChoices is { } choices)
            {
                AddButton(choices.Cancel, isPrimary: false, result: false);
                for (int i = 0; i < choices.Options.Length; i++)
                    AddButton(choices.Options[i], isPrimary: i == choices.Options.Length - 1, result: true, choice: i);
                return;
            }

            switch (kind)
            {
                case Buttons.Ok:
                    AddButton("OK", isPrimary: true, result: true);
                    break;

                case Buttons.OkCancel:
                    AddButton("Cancel", isPrimary: false, result: false);
                    AddButton("OK",     isPrimary: true,  result: true);
                    break;

                case Buttons.YesNo:
                    if (_pendingDanger is { } danger)
                    {
                        AddButton(danger.Cancel,  isPrimary: false, result: false);
                        AddButton(danger.Confirm, isPrimary: true,  result: true, danger: true);
                        break;
                    }
                    AddButton("No",  isPrimary: false, result: false);
                    AddButton("Yes", isPrimary: true,  result: true);
                    break;
            }
        }

        private void AddButton(string label, bool isPrimary, bool result, bool danger = false, int choice = -1)
        {
            var btn = new Wpf.Ui.Controls.Button
            {
                Content    = label,
                Appearance = danger    ? Wpf.Ui.Controls.ControlAppearance.Danger
                           : isPrimary ? Wpf.Ui.Controls.ControlAppearance.Primary
                                       : Wpf.Ui.Controls.ControlAppearance.Secondary,
                MinWidth   = 80,
                Margin     = new Thickness(8, 0, 0, 0),
            };
            btn.Click += (_, _) => { _result = result; _choice = choice; Close(); };
            ButtonPanel.Children.Add(btn);
        }

        // ──────────────────────────────────────────────────────────────
        //  Window chrome
        // ──────────────────────────────────────────────────────────────

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            _result = false;
            Close();
        }
    }
}
