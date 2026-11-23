using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SMDWin.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Clipboard = System.Windows.Clipboard;

namespace SMDWin.Views
{
    /// <summary>
    /// The camera button in the title bar: a picture of the program window only (not the whole screen), to show the data to
    /// someone else. Saved as PNG in Data\Screenshots and copied to the clipboard, so it can be pasted straight into a chat or a
    /// document. Right click on the button opens the folder.
    /// The window is drawn from its own content (not copied from the screen), so other windows on top of it never appear in the
    /// picture; the background colour is painted first, because the window itself is transparent behind its content.
    /// </summary>
    public partial class MainWindow
    {
        private static string ScreenshotDir => AppPaths.Data("Screenshots");

        private void BtnScreenshot_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            // taken after the click has been fully processed (Background priority), so the picture shows the window as it is
            Dispatcher.InvokeAsync(TakeWindowScreenshot, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void BtnScreenshot_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                Directory.CreateDirectory(ScreenshotDir);
                System.Diagnostics.Process.Start("explorer.exe", $"\"{ScreenshotDir}\"");
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Open screenshot folder"); }
        }

        private void TakeWindowScreenshot()
        {
            try
            {
                if (Content is not FrameworkElement root || root.ActualWidth < 10 || root.ActualHeight < 10) return;
                var dpi = VisualTreeHelper.GetDpi(this);
                double w = root.ActualWidth, h = root.ActualHeight;
                int pw = (int)Math.Ceiling(w * dpi.DpiScaleX), ph = (int)Math.Ceiling(h * dpi.DpiScaleY);

                var bg = TryFindResource("AuroraBaseBrush") as Brush
                      ?? (TryFindResource("BgDarkColor") is Color c ? new SolidColorBrush(c) : Brushes.Black);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
                    dc.DrawRectangle(new VisualBrush(root)
                    {
                        Stretch = Stretch.None,
                        AlignmentX = AlignmentX.Left,
                        AlignmentY = AlignmentY.Top,
                    }, null, new Rect(0, 0, w, h));
                }
                var bmp = new RenderTargetBitmap(pw, ph, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                bmp.Render(dv);
                bmp.Freeze();

                Directory.CreateDirectory(ScreenshotDir);
                string page = string.IsNullOrEmpty(_currentPanel) ? "SMDWin" : _currentPanel;
                string path = Path.Combine(ScreenshotDir, $"SMDWin_{page}_{DateTime.Now:yyyy-MM-dd_HHmmss}.png");
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    enc.Save(fs);
                }

                bool copied = false;
                try { Clipboard.SetImage(bmp); copied = true; }
                catch (Exception ex) { AppLogger.Debug($"Screenshot clipboard: {ex.Message}"); }   // the clipboard can be busy

                AppLogger.Info($"Screenshot saved: {path}");
                Fx.Play(Fx.Sound.Shutter);
                Fx.Flash(this);
                Fx.Thumbnail(this, bmp,
                    copied ? T4("Saved and copied", "Salvată și copiată", "Gespeichert und kopiert", "Enregistrée et copiée")
                           : T4("Saved", "Salvată", "Gespeichert", "Enregistrée"),
                    T4("Open folder", "Deschide folderul", "Ordner öffnen", "Ouvrir le dossier"),
                    () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch (Exception ex) { AppLogger.Debug($"Open screenshot folder: {ex.Message}"); } });
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "TakeWindowScreenshot");
                ShowToastError(T4("The screenshot could not be saved", "Captura nu a putut fi salvată",
                                  "Das Bildschirmfoto konnte nicht gespeichert werden", "La capture n'a pas pu être enregistrée"));
            }
        }
    }
}
