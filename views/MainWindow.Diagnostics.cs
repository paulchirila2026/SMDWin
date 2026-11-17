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
using System.Windows.Threading;
using SMDWin;
using SMDWin.Models;
using SMDWin.Services;
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
using WpfColorConv     = System.Windows.Media.ColorConverter;

namespace SMDWin.Views
{
    public partial class MainWindow : Window
    {

        // DiagResults is defined in Models/DiagModels.cs as SMDWin.Models.DiagResults

        // POWERSHELL RUNNER
        // ══════════════════════════════════════════════════════════════════════

        private static readonly string PsCommandsPath = SMDWin.Services.AppPaths.Data("ps_commands.json");

        private List<PsCommandEntry> _psCommands = new();
        private PsCommandEntry? _psSelected;

        private void LoadPsCommands()
        {
            try
            {
                if (File.Exists(PsCommandsPath))
                {
                    var json = File.ReadAllText(PsCommandsPath);
                    _psCommands = System.Text.Json.JsonSerializer.Deserialize<List<PsCommandEntry>>(json)
                                  ?? new List<PsCommandEntry>();
                }
                else
                {
                    // Default example commands
                    _psCommands = new List<PsCommandEntry>
                    {
                        new() { Name = "System Info",     Command = "Get-ComputerInfo | Select-Object CsName, OsName, OsVersion, CsProcessors, CsNumberOfLogicalProcessors, OsTotalVisibleMemorySize" },
                        new() { Name = "Disk Space",      Command = "Get-PSDrive -PSProvider FileSystem | Select-Object Name, @{N='Used(GB)';E={[math]::Round($_.Used/1GB,2)}}, @{N='Free(GB)';E={[math]::Round($_.Free/1GB,2)}}" },
                        new() { Name = "Top CPU Processes",Command = "Get-Process | Sort-Object CPU -Descending | Select-Object -First 15 Name, Id, @{N='CPU(s)';E={[math]::Round($_.CPU,1)}}, @{N='RAM(MB)';E={[math]::Round($_.WorkingSet64/1MB,1)}}" },
                        new() { Name = "Error Events",   Command = "Get-EventLog -LogName System -EntryType Error -Newest 20 | Select-Object TimeGenerated, Source, Message | Format-Table -AutoSize" },
                        new() { Name = "Running Services", Command = "Get-Service | Where-Object {$_.Status -eq 'Running'} | Select-Object DisplayName, Name | Sort-Object DisplayName" },
                        new() { Name = "SFC Verify",      Command = "sfc /verifyonly" },
                        new() { Name = "Flush DNS",        Command = "ipconfig /flushdns" },
                        new() { Name = "Backup Drivers",   Command = "Export-WindowsDriver -Online -Destination \"$env:USERPROFILE\\Documents\\DriversBackup\"" },
                    };
                    SavePsCommands();
                }
            }
            catch { _psCommands = new List<PsCommandEntry>(); }

            PsCommandsGrid.ItemsSource = null;
            PsCommandsGrid.ItemsSource = _psCommands;
            RebuildQuickButtons();
        }

        private void SavePsCommands()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PsCommandsPath)!);
                File.WriteAllText(PsCommandsPath,
                    System.Text.Json.JsonSerializer.Serialize(_psCommands,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AppLogger.Warning(ex, "MainWindow.Diagnostics.SavePsCommands"); }
        }

        private void PsCommandsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PsCommandsGrid.SelectedItem is PsCommandEntry entry)
            {
                _psSelected = entry;
                TxtPsCmdName.Text = entry.Name;
                TxtPsCommand.Text = entry.Command;
            }
        }

        private void PsNew_Click(object sender, RoutedEventArgs e)
        {
            _psSelected = null;
            TxtPsCmdName.Text = _LS("PsEditor", "NewCommand");
            TxtPsCommand.Text = _LS("PsEditor", "DefaultTemplate");
            TxtPsCmdName.Focus();
            TxtPsCmdName.SelectAll();
        }

        private void PsDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_psSelected == null) return;
            _psCommands.Remove(_psSelected);
            _psSelected = null;
            SavePsCommands();
            LoadPsCommands();
            TxtPsCmdName.Text = "";
            TxtPsCommand.Text = "";
        }

        private void PsSave_Click(object sender, RoutedEventArgs e)
        {
            var name = TxtPsCmdName.Text.Trim();
            var cmd  = TxtPsCommand.Text.Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(cmd))
            {
                AppDialog.Show(_LS("PsEditor", "FillNameCommand"), "SMD Win", AppDialog.Kind.Warning);
                return;
            }

            if (_psSelected != null)
            {
                _psSelected.Name    = name;
                _psSelected.Command = cmd;
            }
            else
            {
                var entry = new PsCommandEntry { Name = name, Command = cmd };
                _psCommands.Add(entry);
                _psSelected = entry;
            }

            SavePsCommands();
            LoadPsCommands();
            TxtPsStatus.Text = SMDWin.Services.LanguageService.CurrentCode switch
            {
                "ro" => $"Comandă '{name}' salvată.",
                "de" => $"Befehl '{name}' gespeichert.",
                "fr" => $"Commande « {name} » enregistrée.",
                _    => $"Command '{name}' saved.",
            };
            if (PsOutputBar != null) PsOutputBar.Visibility = Visibility.Visible;
        }

        private async void PsRun_Click(object sender, RoutedEventArgs e)
        {
            var cmd  = TxtPsCommand.Text.Trim();
            var name = TxtPsCmdName.Text.Trim();
            if (string.IsNullOrEmpty(cmd)) return;
            BtnPsRun.IsEnabled = false;
            try   { await RunPsCommandAsync(cmd, string.IsNullOrEmpty(name) ? "command" : name); }
            finally { BtnPsRun.IsEnabled = true; }
        }

        private void PsClear_Click(object sender, RoutedEventArgs e)
        {
            TxtPsOutput.Text = "";
        }

        // ── New Quick-Actions UI helpers ──────────────────────────────────────

        /// Toggle the editor panel open/closed
        private void PsToggleEditor_Click(object sender, RoutedEventArgs e)
        {
            if (PsEditorPanel == null) return;
            bool showing = PsEditorPanel.Visibility == Visibility.Visible;
            PsEditorPanel.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
            if (BtnPsToggleEditor != null)
                BtnPsToggleEditor.Content = showing ? _LS("Common", "Edit") : _LS("Common", "CloseEditor");
            if (!showing)
            {
                // When opening editor, select first command in grid if nothing selected
                if (_psSelected == null && _psCommands.Count > 0)
                {
                    _psSelected = _psCommands[0];
                    TxtPsCmdName.Text = _psSelected.Name;
                    TxtPsCommand.Text = _psSelected.Command;
                    PsCommandsGrid.SelectedItem = _psSelected;
                }
                TxtPsCmdName?.Focus();
            }
        }

        /// Close the compact status bar
        private void PsStatusClose_Click(object sender, RoutedEventArgs e)
        {
            if (PsOutputBar != null) PsOutputBar.Visibility = Visibility.Collapsed;
        }

        /// Close the full output panel
        private void PsOutputClose_Click(object sender, RoutedEventArgs e)
        {
            if (PsFullOutputPanel != null) PsFullOutputPanel.Visibility = Visibility.Collapsed;
        }

        /// Rebuild the WrapPanel of quick-action buttons (one per saved command)
        private void RebuildQuickButtons()
        {
            if (PsQuickButtonsPanel == null) return;
            PsQuickButtonsPanel.Children.Clear();

            string _addLabel   = SMDWin.Services.LanguageService.CurrentCode switch
                { "ro" => "Adaugă", "de" => "Hinzufügen", "fr" => "Ajouter", _ => "Add" };
            string _addTooltip = SMDWin.Services.LanguageService.CurrentCode switch
                { "ro" => "Adaugă comandă nouă", "de" => "Neuen Befehl hinzufügen", "fr" => "Ajouter une nouvelle commande", _ => "Add new command" };

            var accentBrush = (TryFindResource("AccentBrush") as System.Windows.Media.SolidColorBrush)
                               ?? new System.Windows.Media.SolidColorBrush(WpfColor.FromRgb(99, 102, 241));
            var addBtn = new Border
            {
                Width = 100, Height = 80,
                CornerRadius = new CornerRadius(8),
                Background = new System.Windows.Media.SolidColorBrush(
                    WpfColor.FromArgb(35, accentBrush.Color.R, accentBrush.Color.G, accentBrush.Color.B)),
                BorderBrush = accentBrush,
                BorderThickness = new Thickness(1.3),
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = _addTooltip,
            };
            var addSp = new StackPanel
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var addIconVb = new Viewbox { Width = 18, Height = 18, Margin = new Thickness(0, 0, 0, 4) };
            var addIconCanvas = new Canvas { Width = 24, Height = 24 };
            addIconCanvas.Children.Add(new System.Windows.Shapes.Path { Data = SMDWin.Services.FluentIcons.Get("add"), Fill = accentBrush });
            addIconVb.Child = addIconCanvas;
            addSp.Children.Add(addIconVb);
            addSp.Children.Add(new TextBlock
            {
                Text = _addLabel, FontSize = Ui.FontXS,
                Foreground = accentBrush,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            });
            addBtn.Child = addSp;
            addBtn.MouseLeftButtonUp += (_, _) =>
            {
                // Open editor and start new command
                if (PsEditorPanel != null)
                    PsEditorPanel.Visibility = Visibility.Visible;
                if (BtnPsToggleEditor != null)
                    BtnPsToggleEditor.Content = _LS("Common", "CloseEditor");
                PsNew_Click(addBtn, new RoutedEventArgs());
            };
            PsQuickButtonsPanel.Children.Add(addBtn);

            // One tile per saved command
            foreach (var cmd in _psCommands)
            {
                var cmdCapture = cmd;

                var tile = new Border
                {
                    Width = 100, Height = 80,
                    CornerRadius = new CornerRadius(8),
                    Background = new System.Windows.Media.SolidColorBrush(
                        WpfColor.FromArgb(18, accentBrush.Color.R, accentBrush.Color.G, accentBrush.Color.B)),
                    BorderBrush = new System.Windows.Media.SolidColorBrush(
                        WpfColor.FromArgb(70, accentBrush.Color.R, accentBrush.Color.G, accentBrush.Color.B)),
                    BorderThickness = new Thickness(1.3),
                    Margin = new Thickness(0, 0, 8, 8),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = cmdCapture.Command,
                };

                // Name label (up to 2 lines)
                var nameTb = new TextBlock
                {
                    Text = cmdCapture.Name,
                    FontSize = Ui.FontXS, FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    MaxHeight = 42, Margin = new Thickness(4, 0, 4, 3),
                };
                nameTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

                var runIcon = new TextBlock
                {
                    Text = "Run", FontSize = Ui.FontXS,
                    Foreground = accentBrush,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                };

                var sp = new StackPanel
                {
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                sp.Children.Add(nameTb);
                sp.Children.Add(runIcon);
                tile.Child = sp;

                // Single click → run directly
                tile.MouseLeftButtonUp += async (_, _) =>
                {
                    await RunPsCommandAsync(cmdCapture.Command, cmdCapture.Name);
                };

                // Right-click → load into editor for editing
                tile.MouseRightButtonUp += (_, _) =>
                {
                    _psSelected = cmdCapture;
                    TxtPsCmdName.Text = cmdCapture.Name;
                    TxtPsCommand.Text = cmdCapture.Command;
                    PsCommandsGrid.SelectedItem = cmdCapture;
                    if (PsEditorPanel != null)
                        PsEditorPanel.Visibility = Visibility.Visible;
                    if (BtnPsToggleEditor != null)
                        BtnPsToggleEditor.Content = _LS("Common", "CloseEditor");
                };

                // Hover effect — the same accentBrush, just more intense on hover (consistent with the tile palette).
                tile.MouseEnter += (_, _) =>
                    tile.Background = new System.Windows.Media.SolidColorBrush(
                        WpfColor.FromArgb(45, accentBrush.Color.R, accentBrush.Color.G, accentBrush.Color.B));
                tile.MouseLeave += (_, _) =>
                    tile.Background = new System.Windows.Media.SolidColorBrush(
                        WpfColor.FromArgb(18, accentBrush.Color.R, accentBrush.Color.G, accentBrush.Color.B));

                PsQuickButtonsPanel.Children.Add(tile);
            }
        }

        /// Run a PS command and show result in a separate themed window
        private async Task RunPsCommandAsync(string cmd, string label = "")
        {
            if (string.IsNullOrWhiteSpace(cmd)) return;
            string L(string en, string ro, string de, string fr) => SMDWin.Services.LanguageService.CurrentCode switch
            {
                "ro" => ro, "de" => de, "fr" => fr, _ => en,
            };

            // The commands come from an editable JSON file and run with the app's rights: the exact command is shown and
            // confirmation is required
            string shown = cmd.Length > 700 ? cmd.Substring(0, 700) + " …" : cmd;
            if (!AppDialog.ConfirmDanger(
                    $"Run this PowerShell command with the rights of SMDWin?\n\n{label}\n\n{shown}",
                    "Run PowerShell command", "Run"))
            {
                SMDWin.Services.ToolActivityLog.Add("PowerShell", $"Cancelled: {label}", "info");
                return;
            }
            SMDWin.Services.ToolActivityLog.Add("PowerShell", $"Run: {label} — {(cmd.Length > 160 ? cmd.Substring(0, 160) + "…" : cmd)}", "info");

            if (PsOutputBar != null) PsOutputBar.Visibility = Visibility.Visible;
            if (TxtPsStatus != null) TxtPsStatus.Text = L($"Running '{label}'…", $"Se rulează '{label}'…", $"'{label}' wird ausgeführt…", $"Exécution de « {label} »…");

            string result;
            try
            {
                result = await Task.Run(async () =>
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName               = "powershell.exe",
                        Arguments              = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{cmd.Replace("\"", "\\\"")}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        UseShellExecute        = false,
                        CreateNoWindow         = true,
                    };
                    using var proc = System.Diagnostics.Process.Start(psi)!;
                    var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                    var stderrTask = proc.StandardError.ReadToEndAsync();
                    bool exited = proc.WaitForExit(30_000);
                    if (!exited) { try { proc.Kill(); } catch (Exception ex) { AppLogger.Debug(ex.Message); } }
                    string stdout = await stdoutTask;
                    string stderr = await stderrTask;
                    return string.IsNullOrEmpty(stderr) ? stdout : stdout + "\n\n[EROARE]\n" + stderr;
                });
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"PowerShell command failed: {label}");
                result = $"[EROARE] {ex.Message}";
            }

            bool hasOutput = !string.IsNullOrWhiteSpace(result);
            if (TxtPsStatus != null)
                TxtPsStatus.Text = hasOutput
                    ? L($"'{label}' done — {result.Length} chars", $"'{label}' executat — {result.Length} chars", $"'{label}' fertig — {result.Length} Zeichen", $"« {label} » terminé — {result.Length} caractères")
                    : L($"'{label}' done (no output)", $"'{label}' executat (fără output)", $"'{label}' fertig (keine Ausgabe)", $"« {label} » terminé (aucune sortie)");

            if (hasOutput) ShowPsOutputWindow(label, result);
        }

        private void ShowPsOutputWindow(string title, string output)
        {
            var win = new Window
            {
                Title  = $"PowerShell — {title}",
                Width  = 720, Height = 480,
                MinWidth = 500, MinHeight = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner       = this,
                ResizeMode  = ResizeMode.CanResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background  = System.Windows.Media.Brushes.Transparent,
                FontFamily  = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"),
            };
            System.Windows.Shell.WindowChrome.SetWindowChrome(win, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0, ResizeBorderThickness = new Thickness(6),
                CornerRadius = new CornerRadius(0), GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false,
            });
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // title bar
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // Own title bar (title + close X) + drag
            var psTitleBar = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            psTitleBar.ColumnDefinitions.Add(new ColumnDefinition());
            psTitleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var psTitleTb = new TextBlock
            {
                Text = $"PowerShell — {title}", FontSize = Ui.FontL, FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)(TryFindResource("TextPrimaryBrush") ?? System.Windows.Media.Brushes.White),
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
            var psBtnX = Net.NetWindowHelper.CloseIconButton(30, Ui.FontM);
            psBtnX.Click += (_, _) => win.Close();
            Grid.SetColumn(psBtnX, 1);
            psTitleBar.Children.Add(psTitleTb);
            psTitleBar.Children.Add(psBtnX);
            psTitleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) win.DragMove(); };
            Grid.SetRow(psTitleBar, 0);
            grid.Children.Add(psTitleBar);
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            var bdr = new Border
            {
                Background   = (System.Windows.Media.Brush)(TryFindResource("BgHoverBrush") ?? System.Windows.Media.Brushes.DimGray),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Child = scroll,
            };
            scroll.Content = new TextBlock
            {
                Text        = output.TrimEnd(),
                FontFamily  = new System.Windows.Media.FontFamily("Consolas, Courier New"),
                FontSize    = Ui.FontM,
                Foreground  = (System.Windows.Media.Brush)(TryFindResource("TextPrimaryBrush") ?? System.Windows.Media.Brushes.White),
                TextWrapping = TextWrapping.NoWrap,
            };
            Grid.SetRow(bdr, 1);
            grid.Children.Add(bdr);
            var btnClose = new Button
            {
                Content = "Close", HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0), Width = 100, Height = 32,
                Style = (Style)TryFindResource("OutlineButtonStyle"),
            };
            btnClose.Click += (_, _) => win.Close();
            Grid.SetRow(btnClose, 2);
            grid.Children.Add(btnClose);
            win.Content = new Border
            {
                Background = (System.Windows.Media.Brush)(TryFindResource("BgDarkBrush") ?? System.Windows.Media.Brushes.Black),
                BorderBrush = (System.Windows.Media.Brush)(TryFindResource("CardBorderBrush") ?? System.Windows.Media.Brushes.Gray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Child = grid,
            };
            win.Show();
        }

    } // end partial class MainWindow
} // end namespace SMDWin.Views

// ── PowerShell command model ──────────────────────────────────────────────
namespace SMDWin.Models
{
    public class PsCommandEntry
    {
        public string Name    { get; set; } = "";
        public string Command { get; set; } = "";
    }
}

// ── Native Methods for Surface Scan ───────────────────────────────────────
namespace SMDWin.Views
{
    internal static class NativeMethods
    {
        public const uint GENERIC_READ        = 0x80000000;
        public const uint FILE_SHARE_READ     = 0x00000001;
        public const uint FILE_SHARE_WRITE    = 0x00000002;
        public const uint OPEN_EXISTING       = 3;
        public const uint FILE_FLAG_NO_BUFFERING    = 0x20000000;
        public const uint FILE_FLAG_RANDOM_ACCESS   = 0x10000000;
        public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        public const uint ES_CONTINUOUS       = 0x80000000;
        public const uint ES_SYSTEM_REQUIRED  = 0x00000001;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint SetThreadExecutionState(uint esFlags);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        public static extern IntPtr CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern IntPtr OpenThread(ThreadAccess dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern uint SuspendThread(IntPtr hThread);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern int ResumeThread(IntPtr hThread);

        [Flags]
        public enum ThreadAccess : int
        {
            SuspendResume = 0x0002,
            Terminate     = 0x0001,
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(
            IntPtr hFile, IntPtr lpBuffer, uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetFilePointerEx(
            IntPtr hFile, long liDistanceToMove,
            out long lpNewFilePointer, uint dwMoveMethod);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(
            IntPtr hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize,
            IntPtr lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);
    }
}
