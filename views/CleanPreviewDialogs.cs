using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;
using Brushes     = System.Windows.Media.Brushes;
using CheckBox    = System.Windows.Controls.CheckBox;
using FontFamily  = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment   = System.Windows.VerticalAlignment;

namespace SMDWin.Views
{
    /// <summary>
    /// Tools → "Clear temporary files": first a scan (which folders, how big, the largest items), then the deletion only after
    /// the confirmation. Replaces the "del /s /q" sent straight into a command window, where nothing was shown before deleting.
    /// Files in use are skipped; SMD Win's own folder in Temp (its bundled tools) is never touched.
    /// </summary>
    internal sealed class TempCleanDialog : Window
    {
        private sealed class Target
        {
            public string Title = "", Desc = "", Path = "";
            public CheckBox Check = null!;
            public TextBlock SizeTxt = null!;
            public long Bytes; public int Files;
        }

        private readonly List<Target> _targets = new();
        private readonly CheckBox _olderOnly;
        private readonly StackPanel _largest = new();
        private readonly TextBlock _total, _status;
        private readonly Wpf.Ui.Controls.Button _deleteBtn, _cancelBtn;
        private List<(string Name, string Path, long Bytes)> _top = new();
        private bool _busy, _done;

        private static string PT(string en, params object?[] a) => MainWindow.PT(en, a);
        private static string Own => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SMDWin");

        public TempCleanDialog()
        {
            DialogKit.SetupDialogWindow(this, PT("Temporary files"), 640);
            var root = new StackPanel();
            root.Children.Add(DialogKit.Header(this, "Icon.Delete", PT("Temporary files"),
                PT("Files left by programs and by Windows while they work. Documents, pictures and programs are not touched. Files a program is using right now are skipped."), out _));

            AddTarget(PT("Temp of this account"), PT("programs, installers, browser updates"), System.IO.Path.GetTempPath());
            AddTarget(PT("Windows Temp"), PT("left over from updates and services"),
                      System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"));
            foreach (var t in _targets) root.Children.Add(TargetRow(t));

            _olderOnly = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            _olderOnly.Checked += (_, _) => _ = ScanAsync(); _olderOnly.Unchecked += (_, _) => _ = ScanAsync();
            var optText = new StackPanel();
            var ot = DialogKit.Text(PT("Only files older than 1 day"), Ui.FontM, semibold: true);
            optText.Children.Add(ot);
            optText.Children.Add(DialogKit.Text(PT("today's files may belong to an installation that is still running"), Ui.FontXS, secondary: true));
            var opt = new DockPanel(); opt.Children.Add(_olderOnly); opt.Children.Add(optText);
            root.Children.Add(DialogKit.RowCard(opt, () => _olderOnly.IsChecked = !(_olderOnly.IsChecked ?? false)));

            root.Children.Add(DialogKit.SectionLabel(PT("The largest"), topMargin: 8));
            root.Children.Add(_largest);
            var note = DialogKit.Text(PT("ⓘ The files are deleted for good (not to the Recycle Bin), but nothing here is needed by programs — it is created again when needed."), Ui.FontXS, secondary: true);
            note.Margin = new Thickness(2, 4, 2, 0);
            root.Children.Add(note);

            var left = new StackPanel();
            _total = DialogKit.Text("—", Ui.FontL, semibold: true);
            _status = DialogKit.Text(PT("Scanning…"), Ui.FontXS, secondary: true);
            left.Children.Add(_total); left.Children.Add(_status);
            _cancelBtn = new Wpf.Ui.Controls.Button { Content = PT("Cancel"), Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary, Margin = new Thickness(0, 0, 8, 0) };
            _cancelBtn.Click += (_, _) => { if (!_busy) Close(); };
            _deleteBtn = new Wpf.Ui.Controls.Button { Content = PT("Delete"), Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, IsEnabled = false };
            _deleteBtn.Click += async (_, _) => { if (_done) Close(); else await DeleteAsync(); };
            root.Children.Add(DialogKit.Footer(left, _cancelBtn, _deleteBtn));

            Content = DialogKit.Shell(root);
            Loaded += async (_, _) => await ScanAsync();
        }

        private void AddTarget(string title, string desc, string path)
        {
            if (!Directory.Exists(path)) return;
            if (_targets.Any(t => string.Equals(t.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return;
            _targets.Add(new Target { Title = title, Desc = desc, Path = path });
        }

        private UIElement TargetRow(Target t)
        {
            t.Check = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 10, 0) };
            t.Check.Checked += (_, _) => UpdateTotal(); t.Check.Unchecked += (_, _) => UpdateTotal();
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(t.Check);
            var mid = new StackPanel();
            mid.Children.Add(DialogKit.Text(t.Title, Ui.FontM, semibold: true));
            mid.Children.Add(DialogKit.Text(t.Desc, Ui.FontXS, secondary: true));
            var p = DialogKit.Text(ShortPath(t.Path), Ui.FontXS, secondary: true);
            p.FontFamily = new FontFamily("Consolas, Cascadia Mono, Segoe UI"); p.Opacity = 0.8;
            mid.Children.Add(p);
            Grid.SetColumn(mid, 1); g.Children.Add(mid);
            t.SizeTxt = DialogKit.Text("…", Ui.FontM, semibold: true);
            t.SizeTxt.TextAlignment = TextAlignment.Right; t.SizeTxt.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(t.SizeTxt, 2); g.Children.Add(t.SizeTxt);
            return DialogKit.RowCard(g, () => t.Check.IsChecked = !(t.Check.IsChecked ?? false));
        }

        private static string ShortPath(string p)
        {
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return p.StartsWith(user, StringComparison.OrdinalIgnoreCase) ? @"C:\Users\…" + p[user.Length..] : p;
        }

        private bool Eligible(FileInfo f, bool olderOnly, DateTime cutoff) =>
            !f.FullName.StartsWith(Own, StringComparison.OrdinalIgnoreCase) && (!olderOnly || f.LastWriteTime < cutoff);

        private static IEnumerable<FileInfo> Files(DirectoryInfo d)
        {
            var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            try { return d.EnumerateFiles("*", opt); } catch { return Array.Empty<FileInfo>(); }
        }

        private async Task ScanAsync()
        {
            if (_busy || _done) return;
            _busy = true; _deleteBtn.IsEnabled = false; _status.Text = PT("Scanning…");
            bool olderOnly = _olderOnly.IsChecked == true;
            var cutoff = DateTime.Now.AddDays(-1);
            var targets = _targets.Select(t => t.Path).ToList();
            var result = await Task.Run(() =>
            {
                var per = new List<(long Bytes, int Files)>();
                var tops = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in targets)
                {
                    long bytes = 0; int files = 0;
                    var root = new DirectoryInfo(path);
                    foreach (var f in Files(root))
                    {
                        try
                        {
                            if (!Eligible(f, olderOnly, cutoff)) continue;
                            bytes += f.Length; files++;
                            // the top-level item inside Temp it belongs to (a folder or a single file)
                            string rel = f.FullName.Substring(root.FullName.TrimEnd('\\').Length + 1);
                            int slash = rel.IndexOf('\\');
                            string top = System.IO.Path.Combine(root.FullName, slash < 0 ? rel : rel[..slash]);
                            tops[top] = tops.TryGetValue(top, out long v) ? v + f.Length : f.Length;
                        }
                        catch { }
                    }
                    per.Add((bytes, files));
                }
                return (per, tops.OrderByDescending(kv => kv.Value).Take(3).ToList());
            });
            for (int i = 0; i < _targets.Count; i++) { _targets[i].Bytes = result.per[i].Bytes; _targets[i].Files = result.per[i].Files; }
            _top = result.Item2.Select(kv => (System.IO.Path.GetFileName(kv.Key), kv.Key, kv.Value)).ToList();
            foreach (var t in _targets)
                t.SizeTxt.Text = Fmt(t.Bytes) + "\n" + PT("{0} files", t.Files.ToString("N0"));
            _largest.Children.Clear();
            foreach (var (name, path, bytes) in _top)
            {
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var sp = new StackPanel();
                sp.Children.Add(DialogKit.Text(name, Ui.FontS));
                var pp = DialogKit.Text(ShortPath(path), Ui.FontXS, secondary: true);
                pp.FontFamily = new FontFamily("Consolas, Cascadia Mono, Segoe UI"); pp.Opacity = 0.75;
                sp.Children.Add(pp);
                g.Children.Add(sp);
                var sz = DialogKit.Text(Fmt(bytes), Ui.FontS, semibold: true); sz.Margin = new Thickness(12, 0, 0, 0);
                Grid.SetColumn(sz, 1); g.Children.Add(sz);
                _largest.Children.Add(DialogKit.RowCard(g));
            }
            if (_top.Count == 0) _largest.Children.Add(DialogKit.Text(PT("Nothing to delete."), Ui.FontS, secondary: true));
            _busy = false;
            _status.Text = PT("Ready — nothing is deleted until the button is pressed");
            UpdateTotal();
        }

        private long Selected => _targets.Where(t => t.Check.IsChecked == true).Sum(t => t.Bytes);

        private void UpdateTotal()
        {
            long sel = Selected;
            _total.Text = PT("Frees {0}", Fmt(sel));
            _deleteBtn.Content = sel > 0 ? PT("Delete {0}", Fmt(sel)) : PT("Delete");
            _deleteBtn.IsEnabled = !_busy && sel > 0;
        }

        private async Task DeleteAsync()
        {
            if (_busy) return;
            _busy = true; _deleteBtn.IsEnabled = false; _cancelBtn.IsEnabled = false;
            _status.Text = PT("Deleting…");
            bool olderOnly = _olderOnly.IsChecked == true;
            var cutoff = DateTime.Now.AddDays(-1);
            var paths = _targets.Where(t => t.Check.IsChecked == true).Select(t => t.Path).ToList();
            var (freed, deleted, skipped) = await Task.Run(() =>
            {
                long fr = 0; int del = 0, sk = 0;
                foreach (var path in paths)
                {
                    var root = new DirectoryInfo(path);
                    foreach (var f in Files(root).ToList())
                    {
                        try
                        {
                            if (!Eligible(f, olderOnly, cutoff)) continue;
                            long len = f.Length;
                            if (f.IsReadOnly) f.IsReadOnly = false;
                            f.Delete(); fr += len; del++;
                        }
                        catch { sk++; }   // in use or protected: left alone
                    }
                    // the folders left empty (deepest first); never the Temp folder itself, never SMD Win's own folder
                    try
                    {
                        var dirs = root.EnumerateDirectories("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                                       .Where(d => !d.FullName.StartsWith(Own, StringComparison.OrdinalIgnoreCase))
                                       .OrderByDescending(d => d.FullName.Length).ToList();
                        foreach (var d in dirs)
                            try { if (!d.EnumerateFileSystemInfos().Any()) d.Delete(); } catch { }
                    }
                    catch { }
                }
                return (fr, del, sk);
            });
            _busy = false; _done = true;
            _total.Text = PT("Freed {0}", Fmt(freed));
            _status.Text = skipped > 0
                ? PT("{0} files deleted · {1} in use were skipped", deleted.ToString("N0"), skipped.ToString("N0"))
                : PT("{0} files deleted", deleted.ToString("N0"));
            _deleteBtn.Content = PT("Close"); _deleteBtn.IsEnabled = true;
            _cancelBtn.Visibility = Visibility.Collapsed;
            ToolActivityLog.Add("Clear temporary files", $"freed {Fmt(freed)}" + (skipped > 0 ? $" · {skipped} in use skipped" : ""), "ok");
            AppLogger.Info($"Clear temporary files: freed {freed} bytes, {deleted} files deleted, {skipped} skipped");
        }

        internal static string Fmt(long b) =>
            b >= 1L << 30 ? $"{b / (double)(1L << 30):0.00} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0} MB" : b >= 1024 ? $"{b / 1024.0:0} KB" : $"{b} B";
    }

    /// <summary>
    /// Tools → "Old Windows components": before DISM /StartComponentCleanup runs, Windows is asked how big the component store is
    /// and whether a clean-up is recommended (DISM /AnalyzeComponentStore, ~1 min), and what the clean-up does is explained.
    /// ShowDialog() == true → the caller starts the clean-up.
    /// </summary>
    internal sealed class ComponentCleanDialog : Window
    {
        private readonly TextBlock _size, _rec, _status, _estimate;
        private readonly Wpf.Ui.Controls.Button _cleanBtn;

        private static string PT(string en, params object?[] a) => MainWindow.PT(en, a);

        public ComponentCleanDialog()
        {
            DialogKit.SetupDialogWindow(this, PT("Old Windows components"), 620);
            var root = new StackPanel();
            root.Children.Add(DialogKit.Header(this, "Icon.Delete", PT("Old Windows components"),
                PT("When an update replaces a Windows file, the old version is kept in the WinSxS folder. The clean-up removes the ones that are no longer used."), out _));

            var facts = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            facts.ColumnDefinitions.Add(new ColumnDefinition()); facts.ColumnDefinitions.Add(new ColumnDefinition());
            UIElement Fact(string k, TextBlock v, int col)
            {
                var sp = new StackPanel();
                sp.Children.Add(DialogKit.Text(k, Ui.FontXS, secondary: true));
                sp.Children.Add(v);
                var card = DialogKit.RowCard(sp);
                card.Margin = new Thickness(col == 0 ? 0 : 4, 0, col == 0 ? 4 : 0, 6);
                Grid.SetColumn(card, col);
                return card;
            }
            _size = DialogKit.Text("…", Ui.FontL, semibold: true);
            _rec = DialogKit.Text("…", Ui.FontL, semibold: true);
            facts.Children.Add(Fact(PT("Old versions kept (can be freed)"), _size, 0));
            facts.Children.Add(Fact(PT("Clean-up recommended by Windows"), _rec, 1));
            root.Children.Add(facts);

            root.Children.Add(DialogKit.SectionLabel(PT("What happens"), topMargin: 6));
            foreach (var line in new[]
            {
                PT("Only old versions of Windows files are removed — personal files and programs are not touched."),
                PT("Updates older than about 30 days can no longer be uninstalled afterwards."),
                PT("Takes 5–20 minutes; the PC can be used meanwhile. No restart is needed."),
                PT("It is the Windows command DISM /StartComponentCleanup — the same one Windows runs by itself from time to time."),
            })
            {
                var t = DialogKit.Text("•  " + line, Ui.FontS);
                t.Margin = new Thickness(4, 0, 0, 5);
                root.Children.Add(t);
            }

            var left = new StackPanel();
            _estimate = DialogKit.Text("—", Ui.FontL, semibold: true);
            _status = DialogKit.Text(PT("Asking Windows how big the store is… (about 1 minute)"), Ui.FontXS, secondary: true);
            left.Children.Add(_estimate); left.Children.Add(_status);
            var cancel = new Wpf.Ui.Controls.Button { Content = PT("Cancel"), Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary, Margin = new Thickness(0, 0, 8, 0) };
            cancel.Click += (_, _) => { DialogResult = false; };
            _cleanBtn = new Wpf.Ui.Controls.Button { Content = PT("Clean"), Appearance = Wpf.Ui.Controls.ControlAppearance.Primary };
            _cleanBtn.Click += (_, _) => { DialogResult = true; };
            root.Children.Add(DialogKit.Footer(left, cancel, _cleanBtn));

            Content = DialogKit.Shell(root);
            Loaded += async (_, _) => await AnalyzeAsync();
        }

        private async Task AnalyzeAsync()
        {
            try
            {
                var (exit, output) = await Task.Run(() =>
                {
                    // /English: the same words on every Windows language, so the numbers can be read
                    var psi = new ProcessStartInfo("dism.exe", "/English /Online /Cleanup-Image /AnalyzeComponentStore")
                    {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                    };
                    using var p = Process.Start(psi)!;
                    string o = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(180_000)) { try { p.Kill(); } catch { } return (-1, o); }
                    return (p.ExitCode, o);
                });
                string? Val(string label)
                {
                    var m = Regex.Match(output, Regex.Escape(label) + @"\s*:\s*(.+)", RegexOptions.IgnoreCase);
                    return m.Success ? m.Groups[1].Value.Trim() : null;
                }
                string? backups = Val("Backups and Disabled Features");
                string? cache = Val("Cache and Temporary Data");
                string? recommended = Val("Component Store Cleanup Recommended");
                double gb = ToGb(backups) + ToGb(cache);
                if (exit != 0 || (backups == null && recommended == null))
                {
                    _size.Text = "—"; _rec.Text = "—";
                    _status.Text = PT("Windows did not give the size (the clean-up can still run).");
                    AppLogger.Warning($"AnalyzeComponentStore: exit {exit}");
                    return;
                }
                _size.Text = gb > 0 ? $"{gb:0.0} GB" : (backups ?? "—");
                bool yes = recommended?.StartsWith("Yes", StringComparison.OrdinalIgnoreCase) == true;
                _rec.Text = yes ? PT("Yes") : PT("No — optional");
                _estimate.Text = gb > 0 ? PT("About {0}", $"{gb:0.0} GB") : "—";
                _status.Text = yes ? PT("Checked just now") : PT("Windows says it is not needed now; it can still run.");
                if (!yes) _cleanBtn.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "AnalyzeComponentStore");
                _status.Text = PT("Windows did not give the size (the clean-up can still run).");
            }
        }

        private static double ToGb(string? v)
        {
            if (v == null) return 0;
            var m = Regex.Match(v, @"([\d.,]+)\s*(GB|MB|KB)", RegexOptions.IgnoreCase);
            if (!m.Success) return 0;
            double n = double.TryParse(m.Groups[1].Value.Replace(",", "."), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x) ? x : 0;
            return m.Groups[2].Value.ToUpperInvariant() switch { "MB" => n / 1024, "KB" => n / 1048576, _ => n };
        }
    }
}
