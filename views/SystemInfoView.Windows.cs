using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SMDWin.Services;
using WpfButton  = System.Windows.Controls.Button;
using WpfPanel   = System.Windows.Controls.Panel;
using WpfCanvas  = System.Windows.Controls.Canvas;
using WpfHAlign  = System.Windows.HorizontalAlignment;
using WpfVAlign  = System.Windows.VerticalAlignment;
using WpfPath    = System.Windows.Shapes.Path;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfCursors = System.Windows.Input.Cursors;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace SMDWin.Views
{
    /// <summary>
    /// System Information, the Windows tab: four cards (Windows, License, Protection, System) on the same grid as the
    /// Hardware tab. The data comes from <see cref="WindowsInfoReader"/>; only the cards and their drawings are here.
    /// </summary>
    public partial class SystemInfoView
    {
        private WindowsInfo? _winInfo;
        private static WindowsInfo? _winInfoCache;
        // the read in progress (a single, shared one): the page waits for it on load, and if it takes too long the cards come later
        private static Task<WindowsInfo>? _winInfoTask;
        private static Task<WindowsInfo> WinInfoTask()
        {
            var t = _winInfoTask;
            if (t == null || t.IsFaulted || t.IsCanceled) _winInfoTask = t = Task.Run(WindowsInfoReader.Read);
            return t;
        }
        /// <summary>Starts reading the Windows data in the background (license, protection, updates — a few seconds of WMI on
        /// laptops), so the first open of the System Info page does not wait for it.</summary>
        internal static void PrewarmWindowsInfo()
        {
            try { _ = WinInfoTask(); }
            catch (Exception ex) { AppLogger.Debug("SystemInfoView.PrewarmWindowsInfo: " + ex.Message); }
        }

        // The number of days without an update after which the timeline warns
        private const int UpdateStaleDays = 60;

        // The Windows data is read in the background after the hardware data; when it arrives, the Windows cards are added at
        // the end of the page
        private async Task LoadWindowsInfoAsync()
        {
            if (_windowsLoaded) return;
            _windowsLoaded = true;
            try
            {
                // the same Windows data serves all page rebuilds (theme etc.); it is re-read only on "force"
                _winInfo = _winInfoCache ??= await WinInfoTask();
                RebuildAllCardsLayout();
                if (_pendingFocus != null) _ = Dispatcher.BeginInvoke(new Action(ApplyPendingFocus), System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "SystemInfoView.LoadWindowsInfoAsync");
                _windowsLoaded = false;
            }
        }

        private static string Ws(string key) => LanguageService.S("WindowsInfo", key);

        private static bool Has(string? value) => !string.IsNullOrWhiteSpace(value) && value != "—";

        private static string Date(DateTime? dt) => dt.HasValue ? DateFormatHelper.FormatDate(dt.Value) : "—";

        // ── Desene ───────────────────────────────────────────────────────────
        private static Shape Fill(Shape s, string brushKey, double opacity = 1)
        {
            s.SetResourceReference(Shape.FillProperty, brushKey);
            s.Opacity = opacity;
            return s;
        }

        private static WpfPath Stroke(string data, string brushKey, double thickness, bool round = true)
        {
            var p = new WpfPath
            {
                Data = Geometry.Parse(data), StrokeThickness = thickness, Fill = WpfBrushes.Transparent,
                StrokeStartLineCap = round ? PenLineCap.Round : PenLineCap.Flat,
                StrokeEndLineCap = round ? PenLineCap.Round : PenLineCap.Flat,
                StrokeLineJoin = PenLineJoin.Round,
            };
            p.SetResourceReference(Shape.StrokeProperty, brushKey);
            return p;
        }

        private static FrameworkElement Framed(WpfCanvas canvas, double width = HeroCards.ArtW, double height = HeroCards.ArtH)
            => new Viewbox { Width = width, Height = height, Stretch = Stretch.Uniform, Child = canvas };

        // the Windows logo blue (a brand colour, as in the drawings in Services/HardwareArt)
        private static readonly SolidColorBrush WindowsBlue = FrozenBrush(System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4));
        private static readonly SolidColorBrush WindowsBlueDark = FrozenBrush(System.Windows.Media.Color.FromRgb(0x00, 0x4E, 0x8C));
        private static SolidColorBrush FrozenBrush(System.Windows.Media.Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        /// <summary>The Windows logo, blue: four close squares on Windows 11, four panels in perspective on Windows 10.</summary>
        internal static FrameworkElement WindowsLogo(bool windows11, bool ltsc = false)
        {
            var c = new WpfCanvas { Width = 110, Height = 90 };
            // LTSC: the bottom-right square is darker and carries the "LTSC" text
            void Poly(bool ltscPane, params (double X, double Y)[] pts)
            {
                var poly = new Polygon { Points = new PointCollection(pts.Select(p => new System.Windows.Point(p.X, p.Y))) };
                poly.Fill = ltscPane ? WindowsBlueDark : WindowsBlue;
                c.Children.Add(poly);
            }
            void LtscText(double left, double top, double width, double height)
            {
                var tb = new TextBlock
                {
                    Text = "LTSC", Width = width, TextAlignment = TextAlignment.Center,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable, Segoe UI"),
                    FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.White,
                };
                WpfCanvas.SetLeft(tb, left); WpfCanvas.SetTop(tb, top + (height - 19) / 2);
                c.Children.Add(tb);
            }
            if (windows11)
            {
                // four squares of 43 with a gap of 3 between them, as in the original logo
                foreach (double top in new[] { 2.0, 48.0 })
                    foreach (double left in new[] { 10.5, 56.5 })
                    {
                        bool pane = ltsc && top > 40 && left > 40;
                        Poly(pane, (left, top), (left + 43, top), (left + 43, top + 43), (left, top + 43));
                        if (pane) LtscText(left, top, 43, 43);
                    }
            }
            else
            {
                Poly(false, (8, 14), (51, 9), (51, 43), (8, 43));
                Poly(false, (54, 8.5), (102, 1), (102, 43), (54, 43));
                Poly(false, (8, 46), (51, 46), (51, 80), (8, 76));
                Poly(ltsc, (54, 46), (102, 46), (102, 88), (54, 83));
                if (ltsc) LtscText(54, 46, 48, 40);
            }
            return Framed(c);
        }

        /// <summary>The license badge: a sheet with a ribbon and a check (licensed) or a cross (not licensed).</summary>
        internal static FrameworkElement LicenseBadge(bool licensed)
        {
            string key = licensed ? "StatusOkBrush" : "StatusCritBrush";
            var c = new WpfCanvas { Width = 110, Height = 90 };
            var sheet = new Border { Width = 82, Height = 66, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(2) };
            sheet.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
            sheet.SetResourceReference(Border.BorderBrushProperty, "TextSecondaryBrush");
            WpfCanvas.SetLeft(sheet, 6); WpfCanvas.SetTop(sheet, 6); c.Children.Add(sheet);
            for (int i = 0; i < 3; i++)
            {
                var line = new Border { Width = i == 2 ? 28 : 46, Height = 4, CornerRadius = new CornerRadius(2), Opacity = 0.5 };
                line.SetResourceReference(Border.BackgroundProperty, "TextSecondaryBrush");
                WpfCanvas.SetLeft(line, 16); WpfCanvas.SetTop(line, 16 + i * 11); c.Children.Add(line);
            }
            var seal = new Ellipse { Width = 40, Height = 40 };
            Fill(seal, key);
            WpfCanvas.SetLeft(seal, 62); WpfCanvas.SetTop(seal, 44); c.Children.Add(seal);
            var mark = licensed ? Stroke("M72,64 L79,71 L91,57", "BgCardBrush", 4.5) : Stroke("M74,58 L90,74 M90,58 L74,74", "BgCardBrush", 4.5);
            c.Children.Add(mark);
            return Framed(c);
        }

        private static string StateBrush(bool? on) => on == true ? "StatusOkBrush" : on == false ? "StatusWarnBrush" : "StatusNaBrush";

        /// <summary>The protection ring: a segment for each protection (green = active, yellow = off, grey = not read).</summary>
        internal static FrameworkElement ProtectionRing(bool?[] states, int active, int known)
        {
            var c = new WpfCanvas { Width = 100, Height = 100 };
            int n = Math.Max(1, states.Length);
            double step = 360.0 / n;
            const double r = 38, cx = 50, cy = 50;
            double sweep = step - 14;
            for (int i = 0; i < n; i++)
            {
                double a0 = -90 + i * step + 7, a1 = a0 + sweep;
                System.Windows.Point P(double deg) => new(cx + r * Math.Cos(deg * Math.PI / 180), cy + r * Math.Sin(deg * Math.PI / 180));
                var start = P(a0);
                var fig = new PathFigure { StartPoint = start };
                fig.Segments.Add(new ArcSegment(P(a1), new System.Windows.Size(r, r), 0, false, SweepDirection.Clockwise, true));
                var arc = new WpfPath { Data = new PathGeometry(new[] { fig }), StrokeThickness = 9, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                // the unlit segment (protection off or not read) is a faint trace, so it does not look lit on the Light theme;
                // the antivirus and the firewall turned off stay orange
                bool warn = (i == 2 || i == 3) && states[i] == false;
                if (states[i] == true || warn)
                    arc.SetResourceReference(Shape.StrokeProperty, StateBrush(states[i]));
                else
                {
                    arc.SetResourceReference(Shape.StrokeProperty, "TextSecondaryBrush");
                    arc.Opacity = 0.28;
                }
                c.Children.Add(arc);
            }
            var label = new TextBlock { Text = known > 0 ? $"{active}/{known}" : "?", FontSize = 22, FontWeight = FontWeights.SemiBold, Width = 100, TextAlignment = TextAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            WpfCanvas.SetTop(label, 34); c.Children.Add(label);
            return Framed(c);
        }

        // ── Cronologie: instalare → ultima actualizare → azi ──────────────────
        private FrameworkElement UpdateTimeline(WindowsInfo w)
        {
            var host = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            if (!w.InstalledOn.HasValue && !w.LastUpdateOn.HasValue) return host;
            DateTime today = DateTime.Now;
            DateTime start = w.InstalledOn ?? w.LastUpdateOn!.Value;
            DateTime update = w.LastUpdateOn ?? start;
            if (update < start) start = update;
            double total = Math.Max(1, (today - start).TotalDays);
            double firstPart = Math.Clamp((update - start).TotalDays / total, 0.08, 0.92);
            int sinceUpdate = Math.Max(0, (int)(today - update).TotalDays);
            bool stale = w.LastUpdateOn.HasValue && sinceUpdate > UpdateStaleDays;
            string updateBrush = stale ? "StatusWarnBrush" : "AccentBrush";

            var line = new Grid { Height = 14 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(firstPart, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - firstPart, GridUnitType.Star) });
            System.Windows.Shapes.Rectangle Bar(string brushKey, double opacity)
            {
                var r = new System.Windows.Shapes.Rectangle { Height = 3, RadiusX = 1.5, RadiusY = 1.5, VerticalAlignment = WpfVAlign.Center };
                Fill(r, brushKey, opacity);
                return r;
            }
            var seg1 = Bar("AccentBrush", 1); Grid.SetColumn(seg1, 0); line.Children.Add(seg1);
            var seg2 = Bar(stale ? "StatusWarnBrush" : "TextSecondaryBrush", stale ? 1 : 0.4); Grid.SetColumn(seg2, 1); line.Children.Add(seg2);
            Ellipse Dot(string brushKey, WpfHAlign align, int column)
            {
                var d = new Ellipse { Width = 12, Height = 12, HorizontalAlignment = align, VerticalAlignment = WpfVAlign.Center };
                Fill(d, brushKey);
                Grid.SetColumn(d, column);
                return d;
            }
            line.Children.Add(Dot("AccentBrush", WpfHAlign.Left, 0));
            var mid = Dot(updateBrush, WpfHAlign.Right, 0); mid.Margin = new Thickness(0, 0, -6, 0); line.Children.Add(mid);
            var end = Dot("TextSecondaryBrush", WpfHAlign.Right, 1); line.Children.Add(end);
            host.Children.Add(line);

            var labels = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            for (int i = 0; i < 3; i++) labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            void AddLabel(int col, string caption, string value, WpfHAlign align, string valueBrush)
            {
                var sp = new StackPanel { HorizontalAlignment = align };
                sp.Children.Add(HeroText(caption, Ui.FontXS, "TextSecondaryBrush"));
                var v = HeroText(value, Ui.FontM, valueBrush, semibold: true);
                HeroCards.TwoLines(v);
                v.ToolTip = value;
                sp.Children.Add(v);
                Grid.SetColumn(sp, col); labels.Children.Add(sp);
            }
            AddLabel(0, L("installed", "instalat", "installiert", "installé"), Date(w.InstalledOn), WpfHAlign.Left, "TextPrimaryBrush");
            AddLabel(1, L("last update", "ultima actualizare", "letztes Update", "dernière mise à jour"), Date(w.LastUpdateOn), WpfHAlign.Center, stale ? "StatusWarnBrush" : "TextPrimaryBrush");
            AddLabel(2, L("today", "azi", "heute", "aujourd'hui"), Date(today), WpfHAlign.Right, "TextPrimaryBrush");
            host.Children.Add(labels);

            if (stale)
            {
                var warn = HeroText(L($"No update installed for {sinceUpdate} days.", $"Nicio actualizare instalată de {sinceUpdate} de zile.",
                    $"Seit {sinceUpdate} Tagen kein Update installiert.", $"Aucune mise à jour installée depuis {sinceUpdate} jours."), Ui.FontS, "StatusWarnBrush", wrap: true);
                warn.Margin = new Thickness(0, 8, 0, 0);
                host.Children.Add(warn);
            }
            return host;
        }

        // ── The cards ────────────────────────────────────────────────────────
        private Border BuildWindowsOsCard(WindowsInfo w)
        {
            var hue = SpecSheet.Hue("Windows");
            var body = HeroCards.PictureBody(new Viewbox { Width = 66, Height = 54, Child = WindowsLogo(w.IsWindows11, w.IsLongTermChannel) }, out var facts);

            string version = Has(w.DisplayVersion) ? w.DisplayVersion : "";
            if (version.Length > 0)
            {
                facts.Children.Add(HeroFact("Windows", hue, version, Ws("Version")));
                if (Has(w.FullBuild))
                    facts.Children.Add(HeroFact("Cpu", hue, w.FullBuild, Ws("Build")));
            }
            if (Has(w.Architecture))
                facts.Children.Add(HeroFact("Cpu", hue, w.Architecture, Ws("Architecture")));
            if (w.IsLongTermChannel)
                facts.Children.Add(HeroFact("Clock", hue, "LTSC", L("long-term servicing channel", "canal cu suport pe termen lung", "Long-Term-Servicing-Channel", "canal de maintenance à long terme"), "StatusWarnBrush"));
            var support = WindowsFacts.SupportFor(w.BuildNumber, w.Edition);
            if (support != WindowsSupport.Supported)
                facts.Children.Add(HeroFact("Shield", hue, Ws("SupportEnded"),
                    Ws(support == WindowsSupport.Windows10End ? "SupportEndedWin10" : "SupportEndedLegacy"), "StatusWarningBrush", Ws("SupportEndedTip")));

            // the number of pending updates comes from the Health check (it needs a network query, so it is not done here)
            if (DiagnosticOrchestrator.LastPendingUpdates is int pending && pending > 0)
                facts.Children.Add(HeroFact("Shield", hue, pending.ToString(),
                    L(pending == 1 ? "update pending" : "updates pending", pending == 1 ? "actualizare în așteptare" : "actualizări în așteptare",
                      pending == 1 ? "Update ausstehend" : "Updates ausstehend", pending == 1 ? "mise à jour en attente" : "mises à jour en attente"),
                    "StatusWarnBrush"));

            var content = new StackPanel();
            content.Children.Add(body);
            content.Children.Add(UpdateTimeline(w));
            return HeroCards.Card("Windows", Ws("SecOs"), string.IsNullOrWhiteSpace(w.ProductName) ? "Windows" : w.ProductName, content);
        }

        private Border BuildLicenseCard(WindowsInfo w)
        {
            var hue = SpecSheet.Hue("Key");
            bool licensed = w.Licensed;
            var body = HeroCards.PictureBody(LicenseBadge(licensed), out var facts);
            HeroCards.SingleColumn(facts);

            // only the license type, as Windows reports it (OEM / Retail / Volume), without interpretation
            if (w.LicenseChannel.Length > 0)
                facts.Children.Add(HeroFact("Key", hue, w.LicenseChannel == "Volume" ? "Volume (MAK/KMS)" : w.LicenseChannel,
                    L("License type", "Tip licență", "Lizenztyp", "Type de licence")));
            if (Has(w.RegisteredUser))
                facts.Children.Add(HeroFact("System", hue, w.RegisteredUser, Ws("RegisteredTo")));
            // the product key does not appear in the card
            facts.Children.Add(HeroFact("Key", hue,
                Has(w.ProductKey) ? Ws("ProductKey") : Ws("DigitalLicense"),
                L("activation", "activare", "Aktivierung", "activation")));

            string title = !w.LicenseRead ? "—" : licensed ? Ws("Licensed") : Ws("Unlicensed");
            return HeroCards.Card("Key", Ws("SecLicense"), title, body,
                new HeroCards.CardOptions { TitleBrushKey = !w.LicenseRead ? "TextSecondaryBrush" : licensed ? "StatusOkBrush" : "StatusCritBrush" });
        }


        private Border BuildProtectionCard(WindowsInfo w)
        {
            var hue = SpecSheet.Hue("Shield");
            bool? tpm = w.TpmRead ? !string.IsNullOrEmpty(w.TpmVersion) : (bool?)null;
            bool? av = w.AntivirusRead ? w.Antivirus is { Enabled: true, UpToDate: true } : (bool?)null;
            // five protections: Secure Boot · TPM · antivirus · firewall · BitLocker (the system disk)
            var states = new bool?[] { w.SecureBoot, tpm, av, w.Firewall, w.DiskEncrypted };
            var (active, known) = WindowsFacts.ProtectionScore(states);
            var body = HeroCards.PictureBody(ProtectionRing(states, active, known), out var facts);

            // each cell: the small label above (what it is), the bold value below (the state)
            string on = Ws("Enabled"), off = Ws("Disabled");
            if (w.SecureBoot.HasValue)
                facts.Children.Add(HeroFact("Shield", hue, w.SecureBoot.Value ? on : off, Ws("SecureBoot"), w.SecureBoot.Value ? "TextPrimaryBrush" : "TextSecondaryBrush"));
            if (tpm.HasValue)
                facts.Children.Add(HeroFact("Socket", hue, tpm.Value ? $"v{w.TpmVersion}" : Ws("NotPresent"), Ws("Tpm"),
                    tpm.Value ? "TextPrimaryBrush" : "TextSecondaryBrush"));
            if (w.AntivirusRead)
            {
                string state = w.Antivirus == null ? Ws("NoAntivirus") : !w.Antivirus.Enabled ? off : !w.Antivirus.UpToDate ? Ws("AntivirusOutdated") : on;
                string avValue = av == true && w.Antivirus != null ? w.Antivirus.Name : state;
                facts.Children.Add(HeroFact("Shield", hue, avValue, Ws("Antivirus"),
                    av == true ? "TextPrimaryBrush" : w.Antivirus == null ? "StatusCritBrush" : "StatusWarnBrush", state));
            }
            if (w.Firewall.HasValue)
                facts.Children.Add(HeroFact("Shield", hue, w.Firewall.Value ? on : off, L("Firewall", "Firewall", "Firewall", "Pare-feu"),
                    w.Firewall.Value ? "TextPrimaryBrush" : "StatusCritBrush"));
            if (w.DiskEncrypted.HasValue)
                facts.Children.Add(HeroFact("Key", hue, Ws(w.DiskEncrypted.Value ? "DiskEncrypted" : "DiskNotEncrypted"), Ws("DiskEncryption"),
                    tip: w.DiskEncrypted.Value ? Ws("DiskEncryptedTip") : null));
            if (Has(w.BootMode))
                facts.Children.Add(HeroFact("Bolt", hue, w.BootMode, Ws("BootMode")));

            string title = known > 0 ? string.Format(Ws("ProtectionScore"), active, known) : Ws("SecBoot");
            return HeroCards.Card("Shield", Ws("SecProtection"), title, body,
                new HeroCards.CardOptions { TitleBrushKey = av == false ? "StatusWarnBrush" : "TextPrimaryBrush" });
        }

    }
}
