using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace SMDWin.Services
{
    /// <summary>
    /// SMDWin Theme Manager v3 — 2 base themes (Dark / Light) x 4 accent colors
    /// (Blue / Red / Green / Orange). Sidebar gets an accent-tinted gradient.
    /// </summary>
    public static class ThemeManager
    {
        public static event Action? ThemeChanged;

        /// <summary>Old names of the same state — all get the same colour. New code: StatusGood / StatusCheck / StatusProblem.</summary>
        internal static readonly string[] StatusGoodNames    = { "StatusGood", "StatusOk", "StatusSuccess", "Ok" };
        internal static readonly string[] StatusCheckNames   = { "StatusCheck", "StatusWarn", "StatusWarning", "Warn" };
        internal static readonly string[] StatusProblemNames = { "StatusProblem", "StatusCrit", "StatusError", "Crit" };

        // ── Accent palette ────────────────────────────────────────────────────
        public static readonly Dictionary<string, string> AccentHex = new()
        {
            // Accent colors — balanced between Fluent subtle and visible at a glance.
            // Dark theme uses lighter variants (auto-adjusted by ThemeManager light-mode logic),
            // light theme uses these base values directly.
            ["Blue"]   = "#1D6FE8",   // vivid blue — more visible than old #0F6CBD
            ["Red"]    = "#DC2626",
                                      // higher than green (47) -> read as pink/cyclamen, not red. #DC2626 (green=blue=38) is a "clean" red with the same
                                      // lightness (R channel still 220).
            ["Green"]  = "#22A85F",   // emerald green — vivid but not neon
            ["Orange"] = "#E07C28",   // warm orange — consistent with brand
            ["Purple"] = "#7C5CE6",
            ["Teal"]   = "#0E95A3",
        };

        /// <summary>
        /// True for accents that can be confused with status colours (red, orange, yellow, green).
        /// With such an accent the data (charts, dials, bars) stays blue — "DataBrush" — so a red chart does not look like a
        /// problem at 40 °C. Buttons, toggles and tabs keep using the accent.
        /// </summary>
        public static bool IsStatusLikeAccent(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            if (max <= 0 || d / max < 0.30) return false;           // grey / very pale: does not resemble a status
            double h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
            return h < 70 || (h >= 85 && h <= 165) || h >= 335;      // red/orange/yellow · green · red-pink
        }

        // ── Base themes ───────────────────────────────────────────────────────
        public static readonly Dictionary<string, Dictionary<string, string>> Themes = new()
        {
            ["Dark"] = new()
            {
                // a deep navy page: the aurora lights and the glass cards stand out on it
                ["BgDark"]        = "#0A0E16",
                ["BgCard"]        = "#161B25",   // opaque: popups, menus, dialogs (cards use CardSurface)
                ["BgHover"]       = "#1C2230",
                ["TextPrimary"]   = "#FFFFFF",
                ["TextSecondary"] = "#D4E4F7",
                ["Border"]        = "#2A3345",
                ["SidebarBg"]     = "#0B0F18",
                ["NavActive"]     = "#1E3A64",
                ["NavActiveFg"]   = "#93C5FD",
                ["ChartGreen"]    = "#22C55E",
                ["ChartGreenDim"] = "#16A34A",
            },
            ["Light"] = new()
            {
                // Blue Frost — crisp whites, a very pale blue-grey page background,
                // pure white cards, a sidebar with a subtle accent gradient (metal feel)
                ["BgDark"]        = "#EBEEF4",   // page bg — a pale blue-grey (half the blue of the first proposal), so the white cards float (+ the spotlight and two faint blue-grey lights)
                ["BgCard"]        = "#FFFFFF",   // pure white cards — clear contrast against the page
                ["BgHover"]       = "#F5F8FC",   // very subtle hover on Light (the shadow provides the accent)
                ["TextPrimary"]   = "#0F172A",   // slate-900 — near-black, contrastant
                ["TextSecondary"] = "#3B475A",   // slate, good contrast on small text
                ["Border"]        = "#CBD5E1",   // slate-300 — clear border, but not harsh
                ["SidebarBg"]     = "#FFFFFF",   // white — the gradient comes from ApplySidebarGradient
                ["NavActive"]     = "#DBEAFE",   // blue-100 — active item bg subtil
                ["NavActiveFg"]   = "#1D4ED8",   // blue-700 — accent text vizibil
                ["ChartGreen"]    = "#16A34A",
                ["ChartGreenDim"] = "#15803D",
            },
        };

        // Legacy aliases
        public static readonly Dictionary<string, string> LegacyAliases = new()
        {
            ["Dark Navy"]     = "Dark",
            ["Dark Slate"]    = "Dark",
            ["Dark Midnight"] = "Dark",
            ["Dark Red"]      = "Dark",
            ["Gray"]          = "Light",
            ["Light Clean"]   = "Light",
            ["Light Warm"]    = "Light",
            ["Light Refined"] = "Light",
        };

        public static bool IsDark(string? themeName) =>
            themeName is "Dark" or "Dark Navy" or "Dark Slate" or "Dark Midnight" or "Dark Red";
        public static bool IsLight(string? themeName) =>
            themeName is "Light" or "Light Clean" or "Light Warm" or "Light Refined" or "Gray";

        public static string Normalize(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Dark";
            if (LegacyAliases.TryGetValue(name, out var mapped)) return mapped;
            return Themes.ContainsKey(name) ? name : "Dark";
        }

        public static string ResolveAuto() => ResolveAutoTheme(
            SettingsService.Current.AutoDarkTheme,
            SettingsService.Current.AutoLightTheme);

        public static bool WindowsIsDark()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var val = key?.GetValue("AppsUseLightTheme");
                return val is int i && i == 0;
            }
            catch { return true; }
        }

        public static string GetWindowsAccentColorHex()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\DWM");
                var val = key?.GetValue("AccentColor");
                if (val is int argb)
                {
                    uint u = unchecked((uint)argb);
                    byte r = (byte)(u & 0xFF);
                    byte g = (byte)((u >> 8) & 0xFF);
                    byte b = (byte)((u >> 16) & 0xFF);
                    return $"#{r:X2}{g:X2}{b:X2}";
                }
            }
            catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            return "#0078D4"; // fallback — the default Windows blue, if the registry cannot be read
        }

        public static string ResolveAutoTheme(string? autoDark, string? autoLight)
        {
            bool isDark = WindowsIsDark();
            string dark  = Normalize(autoDark  ?? "Dark");
            string light = Normalize(autoLight ?? "Light");
            return isDark ? dark : light;
        }

        // ── Accent helpers ────────────────────────────────────────────────────
        public static string GetAccentHex(string? accentName)
        {
            var custom = SettingsService.Current.CustomAccentHex;
            if (!string.IsNullOrWhiteSpace(custom) && IsValidHexColor(custom))
                return custom;
            if (accentName != null && AccentHex.TryGetValue(accentName, out var hex)) return hex;
            return AccentHex["Blue"];
        }

        /// <summary>The colour of a preset (Blue / Red / Green / Orange), without the custom accent.</summary>
        public static string GetPresetAccentHex(string name) =>
            AccentHex.TryGetValue(name, out var hex) ? hex : AccentHex["Blue"];

        private static bool IsValidHexColor(string hex) =>
            System.Text.RegularExpressions.Regex.IsMatch(hex, @"^#[0-9A-Fa-f]{6}$");

        // Metal brush gradient — returns 4 stops: top, midHi (sheen), mid, bot
        // Vertical stops: 0% top-dark | 35% midHi-bright (accent sheen) | 65% mid | 100% bot-dark
        // Sidebar gradient — subtle, almost flat, with a slight accent glint at 35%
        // Dark: brighter than the base (#0A0F1A), toned accent glow
        // Light: darker than the white cards (#FFF), contrasting with the content
        private static (string top, string midHi, string mid, string bot) GetSidebarGradientHex(string themeName, string accentHex)
        {
            bool isDark = IsDark(themeName);
            try
            {
                var ac = ParseHex(accentHex);
                if (isDark)
                {
                    // Dark sidebar: almost black (#070B12), minimal variation between stops
                    // The accent colour contributes only a subtle thread (0.03–0.05 factor)
                    byte rBase = (byte)Math.Min(255, 0x07 + (int)(ac.R * 0.03));
                    byte gBase = (byte)Math.Min(255, 0x0B + (int)(ac.G * 0.025));
                    byte bBase = (byte)Math.Min(255, 0x12 + (int)(ac.B * 0.04));

                    // Top: baza
                    byte rTop = rBase;
                    byte gTop = gBase;
                    byte bTop = bBase;

                    // MidHi (35%): minimal accent glint — +5 lightness max
                    byte rMH = (byte)Math.Min(255, rBase + 5 + (int)(ac.R * 0.04));
                    byte gMH = (byte)Math.Min(255, gBase + 4 + (int)(ac.G * 0.03));
                    byte bMH = (byte)Math.Min(255, bBase + 4 + (int)(ac.B * 0.05));

                    // Mid (65%): almost like the base
                    byte rMid = (byte)Math.Max(0, rBase - 1);
                    byte gMid = (byte)Math.Max(0, gBase - 1);
                    byte bMid = (byte)Math.Max(0, bBase - 1);

                    // Bot: -3 from the base
                    byte rBot = (byte)Math.Max(0, rBase - 3);
                    byte gBot = (byte)Math.Max(0, gBase - 3);
                    byte bBot = (byte)Math.Max(0, bBase - 2);

                    return (
                        string.Format("#{0:X2}{1:X2}{2:X2}", rTop, gTop, bTop),
                        string.Format("#{0:X2}{1:X2}{2:X2}", rMH,  gMH,  bMH),
                        string.Format("#{0:X2}{1:X2}{2:X2}", rMid, gMid, bMid),
                        string.Format("#{0:X2}{1:X2}{2:X2}", rBot, gBot, bBot));
                }
                else
                {
                    // Light: almost white, subtle accent tint — clean, not "dirty"
                    // Base: #F0F4FA — very pale bluish white, lighter than the page bg (#EEF2F7)
                    // but visually distinct from the white cards (#FFF) through the shadow
                    byte rBase = (byte)Math.Min(255, 0xF0 + (int)(ac.R * 0.02));
                    byte gBase = (byte)Math.Min(255, 0xF4 + (int)(ac.G * 0.015));
                    byte bBase = (byte)Math.Min(255, 0xFA + (int)(ac.B * 0.025));

                    // Top: baza
                    byte rTop = rBase;
                    byte gTop = gBase;
                    byte bTop = bBase;

                    // MidHi (35%): subtle accent glint — +6 lightness, minimal accent
                    byte rMH = (byte)Math.Min(255, rBase + 6 + (int)(ac.R * 0.04));
                    byte gMH = (byte)Math.Min(255, gBase + 5 + (int)(ac.G * 0.03));
                    byte bMH = (byte)Math.Min(255, bBase + 4 + (int)(ac.B * 0.05));

                    // Mid (65%): -3 from the base
                    byte rMid = (byte)Math.Max(0, rBase - 3);
                    byte gMid = (byte)Math.Max(0, gBase - 3);
                    byte bMid = (byte)Math.Max(0, bBase - 2);

                    // Bot: -6 from the base — visual weight at the bottom, but still light
                    byte rBot = (byte)Math.Max(0, rBase - 6);
                    byte gBot = (byte)Math.Max(0, gBase - 6);
                    byte bBot = (byte)Math.Max(0, bBase - 5);

                    return (
                        string.Format("#{0:X2}{1:X2}{2:X2}", rTop, gTop, bTop),
                        string.Format("#{0:X2}{1:X2}{2:X2}", rMH,  gMH,  bMH),
                        string.Format("#{0:X2}{1:X2}{2:X2}", rMid, gMid, bMid),
                        string.Format("#{0:X2}{1:X2}{2:X2}", rBot, gBot, bBot));
                }
            }
            catch
            {
                return isDark
                    ? ("#070B12", "#0C1018", "#060A10", "#04070E")
                    : ("#F0F4FA", "#F6F8FE", "#EDF1F7", "#EAEEf5");
            }
        }

        /// <summary>WPF-UI keys of the ordinary buttons → our resource ("" = transparent). Also used by Ui.UseAppScrollBars,
        /// for elements that receive the WPF-UI dictionaries directly (those take precedence over the app resources).</summary>
        internal static readonly (string Key, string Source)[] ButtonResourceMap =
        {
            ("ButtonBackground", "BtnNeutralBrush"),
            ("ButtonBackgroundPointerOver", "BtnNeutralHoverBrush"),
            ("ButtonBackgroundPressed", "BtnNeutralPressedBrush"),
            ("ButtonBorderBrush", "BtnNeutralBorderBrush"),
            ("ButtonBorderBrushPointerOver", "BtnNeutralBorderBrush"),
            ("ButtonBorderBrushPressed", "BtnNeutralBorderBrush"),
        };

        public static void Apply(string rawThemeName, ResourceDictionary resources)
        {
            if (rawThemeName == "Auto") return;

            string themeName = Normalize(rawThemeName);
            if (!Themes.TryGetValue(themeName, out var t)) return;

            bool isLight = IsLight(themeName);
            bool isDark  = IsDark(themeName);
            string accentHex = GetAccentHex(SettingsService.Current.AccentName);

            bool isAppDict = ReferenceEquals(resources, System.Windows.Application.Current?.Resources);
            string signature = $"{themeName}|{accentHex}|{_customAccentHex ?? ""}";
            if (isAppDict && signature == _lastAppliedSignature) return;

            var brushesBefore = isAppDict ? SnapshotBrushes(resources) : null;

            resources.BeginInit();

            // IconBrush — kept as a fallback for any old reference, but the sidebar icons NO longer depend on it (see the swap below).
            resources["IconBrush"] = new SolidColorBrush(
                isLight ? Color.FromRgb(0x1C, 0x19, 0x17)   // dark text on light
                        : Color.FromRgb(0xFA, 0xFA, 0xF9));  // cream on dark

            if (isAppDict)
            {
                try
                {
                    var app = System.Windows.Application.Current;
                    if (app != null)
                    {
                        string wantedFile = isLight ? "IconsLight.xaml" : "IconsDark.xaml";
                        bool alreadyLoaded = app.Resources.MergedDictionaries.Any(d =>
                            d.Source != null && d.Source.OriginalString.EndsWith(wantedFile, StringComparison.OrdinalIgnoreCase));
                        if (!alreadyLoaded)
                        {
                            for (int i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
                            {
                                var src = app.Resources.MergedDictionaries[i].Source?.OriginalString ?? "";
                                if (src.EndsWith("IconsDark.xaml", StringComparison.OrdinalIgnoreCase) ||
                                    src.EndsWith("IconsLight.xaml", StringComparison.OrdinalIgnoreCase))
                                    app.Resources.MergedDictionaries.RemoveAt(i);
                            }
                            app.Resources.MergedDictionaries.Add(new ResourceDictionary
                            {
                                Source = new Uri($"Views/{wantedFile}", UriKind.Relative)
                            });
                        }
                    }
                }
                catch (Exception ex) { AppLogger.Debug($"ThemeManager icon-dict swap: {ex.Message}"); }
            }

            // ── Lift tokens — swapped per theme ─────────────────────────────────
            void SetLift(string key, string hex)
            {
                try
                {
                    resources[key] = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(hex)!);
                }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            }

            if (isLight)
            {
                SetLift("Lift.Canvas",           "#F3F4F6");
                SetLift("Lift.Surface",          "#FAFAFA");
                SetLift("Lift.Surface2",         "#E9EEF5");
                SetLift("Lift.Border",           "#D1CDC9");
                SetLift("Lift.Border2",          "#C4C0BB");
                SetLift("Lift.Text",             "#1C1917");
                SetLift("Lift.TextMuted",        "#44403C");
                SetLift("Lift.TextSoft",         "#57534E");
                SetLift("Lift.TextDim",          "#A8A29E");
                SetLift("Lift.AccentBrush",      accentHex);
                // Hover: slightly lighter variant
                var acL = ParseHex(accentHex);
                resources["Lift.AccentHover"] = new SolidColorBrush(
                    Color.FromRgb(
                        (byte)Math.Min(255, acL.R + 20),
                        (byte)Math.Min(255, acL.G + 20),
                        (byte)Math.Min(255, acL.B + 20)));
                resources["Lift.AccentPress"] = new SolidColorBrush(
                    Color.FromRgb(
                        (byte)Math.Max(0, acL.R - 20),
                        (byte)Math.Max(0, acL.G - 20),
                        (byte)Math.Max(0, acL.B - 20)));
                SetLift("Lift.PrimaryBg",        "#1C1917");
                SetLift("Lift.PrimaryBgHover",   "#292524");
                SetLift("Lift.PrimaryBgPress",   "#0C0A09");
                SetLift("Lift.PrimaryFg",        "#FFFFFF");
                SetLift("Lift.DangerText",       "#B91C1C");
                SetLift("Lift.DangerBorder",     "#FECACA");
                SetLift("Lift.DangerHoverBg",    "#FEF2F2");
                SetLift("Lift.DangerHoverBorder","#F87171");
                SetLift("Lift.DangerPressBg",    "#FEE2E2");
                // Shadows light
                resources["Lift.Shadow1"] = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius=3, ShadowDepth=1, Direction=270,
                    Opacity=0.10, Color=Color.FromRgb(0x1C,0x19,0x17),
                    RenderingBias=System.Windows.Media.Effects.RenderingBias.Performance
                };
                resources["Lift.Shadow2"] = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius=6, ShadowDepth=2, Direction=270,
                    Opacity=0.16, Color=Color.FromRgb(0x1C,0x19,0x17),
                    RenderingBias=System.Windows.Media.Effects.RenderingBias.Performance
                };
                resources["Lift.ShadowPrimary"] = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius=4, ShadowDepth=1, Direction=270,
                    Opacity=0.22, Color=Color.FromRgb(0x1C,0x19,0x17),
                    RenderingBias=System.Windows.Media.Effects.RenderingBias.Performance
                };
            }
            else
            {
                SetLift("Lift.Canvas",           "#0C0A09");
                SetLift("Lift.Surface",          "#242D3D");
                SetLift("Lift.Surface2",         "#2E3A4E");  // hover: brighter
                SetLift("Lift.Border",           "#3D5070");  // a visible border on dark
                SetLift("Lift.Border2",          "#6B6560");
                SetLift("Lift.Text",             "#FAFAF9");
                SetLift("Lift.TextMuted",        "#D6D3D1");
                SetLift("Lift.TextSoft",         "#A8A29E");
                SetLift("Lift.TextDim",          "#78716C");
                SetLift("Lift.AccentBrush",      accentHex);
                var acD = ParseHex(accentHex);
                resources["Lift.AccentHover"] = new SolidColorBrush(
                    Color.FromRgb(
                        (byte)Math.Min(255, acD.R + 30),
                        (byte)Math.Min(255, acD.G + 30),
                        (byte)Math.Min(255, acD.B + 30)));
                resources["Lift.AccentPress"] = new SolidColorBrush(
                    Color.FromRgb(
                        (byte)Math.Min(255, acD.R + 50),
                        (byte)Math.Min(255, acD.G + 50),
                        (byte)Math.Min(255, acD.B + 50)));
                SetLift("Lift.PrimaryBg",        "#FAFAF9");
                SetLift("Lift.PrimaryBgHover",   "#E7E5E4");
                SetLift("Lift.PrimaryBgPress",   "#D6D3D1");
                SetLift("Lift.PrimaryFg",        "#0C0A09");
                SetLift("Lift.DangerText",       "#FCA5A5");
                SetLift("Lift.DangerBorder",     "#7F1D1D");
                SetLift("Lift.DangerHoverBg",    "#450A0A");
                SetLift("Lift.DangerHoverBorder","#B91C1C");
                SetLift("Lift.DangerPressBg",    "#7F1D1D");
                // Shadows dark
                resources["Lift.Shadow1"] = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius=6, ShadowDepth=1, Direction=270,
                    Opacity=0.55, Color=Colors.Black,
                    RenderingBias=System.Windows.Media.Effects.RenderingBias.Performance
                };
                resources["Lift.Shadow2"] = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius=12, ShadowDepth=2, Direction=270,
                    Opacity=0.70, Color=Colors.Black,
                    RenderingBias=System.Windows.Media.Effects.RenderingBias.Performance
                };
                resources["Lift.ShadowPrimary"] = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius=6, ShadowDepth=1, Direction=270,
                    Opacity=0.45, Color=Colors.Black,
                    RenderingBias=System.Windows.Media.Effects.RenderingBias.Performance
                };
            }
            // ────────────────────────────────────────────────────────────────

            void Set(string key, string hex)
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(hex)!;
                    resources[key + "Color"] = color;
                    resources[key + "Brush"] = new SolidColorBrush(color);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "ThemeManager.unknown"); }
            }

            void SetColor(string key, string hex)
            {
                try
                {
                    var c = (Color)ColorConverter.ConvertFromString(hex)!;
                    resources[key + "Color"] = c;
                    resources[key + "Brush"] = new SolidColorBrush(c);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "ThemeManager.unknown"); }
            }

            // Base palette
            Set("BgDark",        t["BgDark"]);
            Set("BgCard",        t["BgCard"]);
            Set("BgHover",       t["BgHover"]);
            // Input background: white on Light (not hover blue), neutral on Dark
            SetColor("BgInput",  isLight ? "#FFFFFF" : t["BgHover"]);
            // Selection controls (dropdown/ComboBox): on Dark, BgInput (#1E2635) is almost identical to BgCard (#1C2431) and the
            // card outline has ~27% opacity -> the box looks flat and blends into the card. Slightly raised background + visible outline.
            SetColor("ControlFill",   isLight ? "#FFFFFF" : "#243044");
            SetColor("ControlStroke", isLight ? t["Border"] : "#3A4861");
            // choice controls (boxes, toggles, grouped buttons): visible outline and background in both themes
            SetColor("InputBorder", isLight ? "#9AA3AF" : "#5A6371");
            // outline-free buttons: a grey track + the chosen option as a lighter, raised pill;
            // ordinary buttons have a neutral background, slightly different from the card. Blue stays only on the main action.
            // ordinary buttons are "raised white" (as in Windows 11): white / grey-blue background, a fine darker outline at the bottom
            SetColor("SegTrack",    isLight ? "#E6E9EE" : "#1A1F27");
            SetColor("SegOn",       isLight ? "#FFFFFF" : "#3D485C");
            SetColor("SelBorder",   isLight ? "#1F000000" : "#33FFFFFF");
            SetColor("BtnNeutral",        isLight ? "#FFFFFF" : "#2B3240");
            SetColor("BtnNeutralHover",   isLight ? "#F6F7F9" : "#333B4B");
            SetColor("BtnNeutralPressed", isLight ? "#EEF0F3" : "#252B37");
            {
                var edge = new System.Windows.Media.LinearGradientBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isLight ? "#E3E6EA" : "#3D4555"),
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isLight ? "#C9CED6" : "#1E232C"),
                    90);
                edge.Freeze();
                resources["BtnNeutralBorderBrush"] = edge;
            }
            // WPF-UI buttons (Secondary): the same colours, without the darker bottom outline
            foreach (var (k, src) in ButtonResourceMap)
                resources[k] = src == "" ? System.Windows.Media.Brushes.Transparent : resources[src];
            Set("Accent",        accentHex);
            Set("TextPrimary",   t["TextPrimary"]);
            Set("TextSecondary", t["TextSecondary"]);
            Set("Border",        t["Border"]);
            Set("Sidebar",       t["SidebarBg"]);
            Set("SidebarBg",     t["SidebarBg"]); // alias used by the badge BorderBrush in XAML

            // ── Card surfaces ────────────────────────────────────────────────
            // Dark: glass cards — translucent white over the aurora background. Light: plain white cards on an even page.
            // BgCard stays opaque, for popups, menus and dialogs that sit over other content.
            SetColor("CardSurface",  isLight ? "#FFFFFF"   : "#0CFFFFFF");
            SetColor("CardEdge",     isLight ? "#140F172A" : "#17FFFFFF");
            SetColor("CardFoot",     isLight ? "#F8FAFC"   : "#07FFFFFF");
            SetColor("CardFootLine", isLight ? "#100F172A" : "#10FFFFFF");
            SetColor("CardHover",    isLight ? "#FBFCFE"   : "#12FFFFFF");
            resources["CardHoverBg"] = resources["CardHoverBrush"];

            // ── One colour per component: the icon colour; no tile behind it (Windows look — icons sit on the card) ──
            SetColor("CompCpu",      isLight ? "#2563EB" : "#8FBBFF");
            SetColor("CompCpuTile", "#00FFFFFF");
            SetColor("CompGpu",      isLight ? "#7C3AED" : "#C4B5FD");
            SetColor("CompGpuTile", "#00FFFFFF");
            SetColor("CompRam",      isLight ? "#0F766E" : "#5EEAD4");
            SetColor("CompRamTile", "#00FFFFFF");
            SetColor("CompDisk",     isLight ? "#C2410C" : "#FDBA74");
            SetColor("CompDiskTile", "#00FFFFFF");
            SetColor("CompNet",      isLight ? "#0369A1" : "#7DD3FC");
            SetColor("CompNetTile", "#00FFFFFF");
            SetColor("CompOs",       isLight ? "#2563EB" : "#8FBBFF");
            SetColor("CompOsTile", "#00FFFFFF");

            // ── The page colour under everything, opaque on both themes: the desktop wallpaper must not tint the window through
            //    Mica, so the app looks the same on every monitor ─────────
            {
                var bg = ParseHex(t["BgDark"]);
                resources["AuroraBaseBrush"] = new SolidColorBrush(Color.FromRgb(bg.R, bg.G, bg.B));
            }
            // the unlit segments of the dials: a neutral grey that stays visible on the translucent Dark cards
            SetColor("GaugeTrack", isLight ? t["Border"] : "#2EFFFFFF");

            // NavActive/NavActiveFg tinted by accent
            try
            {
                var ac = ParseHex(accentHex);
                if (isDark)
                {
                    // Dark: more visible — alpha 60 instead of 45
                    var navBg = Color.FromArgb(60, ac.R, ac.G, ac.B);
                    var navFg = Color.FromRgb(255, 255, 255);
                    resources["NavActiveColor"]   = navBg;
                    resources["NavActiveBrush"]   = new SolidColorBrush(navBg);
                    resources["NavActiveFgColor"] = navFg;
                    resources["NavActiveFgBrush"] = new SolidColorBrush(navFg);

                    var rim   = Color.FromArgb(90, ac.R, ac.G, ac.B);
                    var inner = Color.FromArgb(40, ac.R, ac.G, ac.B);
                    resources["NavActiveRimColor"]   = rim;
                    resources["NavActiveInnerColor"] = inner;
                    resources["NavActiveRimBrush"]   = new SolidColorBrush(rim);
                    resources["NavActiveInnerBrush"] = new SolidColorBrush(inner);
                }
                else
                {
                    // Light: pale accent with dark accent text — NEVER white on blue
                    var navBg = Color.FromArgb(18, ac.R, ac.G, ac.B);  // very subtle tint
                    var navFg = Color.FromRgb(
                        (byte)Math.Max(0, ac.R - 40),
                        (byte)Math.Max(0, ac.G - 40),
                        (byte)Math.Max(0, ac.B - 40));                  // darker accent

                    resources["NavActiveColor"]   = navBg;
                    resources["NavActiveBrush"]   = new SolidColorBrush(navBg);
                    resources["NavActiveFgColor"] = navFg;
                    resources["NavActiveFgBrush"] = new SolidColorBrush(navFg);

                    // Rim: accent left border — the visible indicator
                    var rim   = Color.FromArgb(255, ac.R, ac.G, ac.B);
                    var inner = Color.FromArgb(12, ac.R, ac.G, ac.B);
                    resources["NavActiveRimColor"]   = rim;
                    resources["NavActiveInnerColor"] = inner;
                    resources["NavActiveRimBrush"]   = new SolidColorBrush(rim);
                    resources["NavActiveInnerBrush"] = new SolidColorBrush(inner);
                }
            }
            catch
            {
                Set("NavActive",   t["NavActive"]);
                Set("NavActiveFg", t["NavActiveFg"]);
            }

            // Card border
            if (isDark)
            {
                try
                {
                    var bc = ParseHex(t["Border"]);
                    // opaque outline, visible on Dark (at 27 % opacity the cards were lost on the background)
                    var cardBorder = ParseHex("#30363F");
                    resources["CardBorderColor"] = cardBorder;
                    resources["CardBorderBrush"] = new SolidColorBrush(cardBorder);
                    var border2 = Color.FromArgb(220, bc.R, bc.G, bc.B);
                    resources["BorderColor"]  = border2;
                    resources["BorderBrush2"] = new SolidColorBrush(border2);
                }
                catch { Set("CardBorder", t["Border"]); }
            }
            else
            {
                Set("CardBorder", t["Border"]);
                // Light theme: also set BorderBrush2 to match the light border
                try
                {
                    var bc = ParseHex(t["Border"]);
                    resources["BorderColor"]  = bc;
                    resources["BorderBrush2"] = new SolidColorBrush(bc);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "ThemeManager.unknown"); }

                // Light theme: replace card shadow — black shadows are invisible on light bg.
                // Blue Frost: an accent-tinted shadow, more visible, gives the cards real depth.
                try
                {
                    var ac = ParseHex(accentHex);
                    // Moderate bluish shadow — visible on the #EEF2F7 bg, not aggressive
                    resources["CardShadowEffect"] = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        BlurRadius     = 16,
                        ShadowDepth    = 2,
                        Direction      = 270,
                        Color          = Color.FromRgb(
                            (byte)Math.Max(0, ac.R - 80),
                            (byte)Math.Max(0, ac.G - 60),
                            (byte)Math.Max(0, ac.B - 20)),
                        Opacity        = 0.14,
                        RenderingBias  = System.Windows.Media.Effects.RenderingBias.Performance,
                    };
                }
                catch (Exception ex) { AppLogger.Warning(ex, "ThemeManager.LightCardShadow"); }
            }

            Set("NavAccentBar", accentHex);

            // RowSelect
            try
            {
                var ac = ParseHex(accentHex);
                byte alpha = isLight ? (byte)55 : (byte)42;
                var rowSel = new SolidColorBrush(Color.FromArgb(alpha, ac.R, ac.G, ac.B));
                rowSel.Freeze();
                resources["RowSelectBrush"] = rowSel;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ThemeManager.unknown"); }

            // AccentHover
            try
            {
                var ac = ParseHex(accentHex);
                byte rh = (byte)Math.Min(255, ac.R + (isDark ? 30 : -20));
                byte gh = (byte)Math.Min(255, ac.G + (isDark ? 30 : -20));
                byte bh = (byte)Math.Min(255, ac.B + (isDark ? 30 : -20));
                var hover = new SolidColorBrush(Color.FromRgb(rh, gh, bh));
                hover.Freeze();
                resources["AccentHoverBrush"] = hover;
                resources["AccentHoverColor"] = Color.FromRgb(rh, gh, bh);
            }
            catch { resources["AccentHoverBrush"] = resources.Contains("AccentBrush") ? resources["AccentBrush"] : new SolidColorBrush(ParseHex(accentHex)); }

            Set("ActionGreen", isLight ? "#16A34A" : "#22C55E");
            Set("ActionRed",   isLight ? "#DC2626" : "#EF4444");

            resources["PrimaryButtonFgColor"] = System.Windows.Media.Colors.White;
            resources["PrimaryButtonFgBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White);

            // PrimaryButton gradient — on Light the primary button gets an accent gradient; on Dark it stays solid (a gradient is
            // too intense on dark)
            if (isLight)
            {
                try
                {
                    var ac = ParseHex(accentHex);
                    // Gradient: accent -> darker accent (15% darker)
                    byte r2 = (byte)Math.Max(0, ac.R - 38);
                    byte g2 = (byte)Math.Max(0, ac.G - 38);
                    byte b2 = (byte)Math.Max(0, ac.B - 38);
                    var grad = new System.Windows.Media.LinearGradientBrush(
                        new System.Windows.Media.GradientStopCollection
                        {
                            new System.Windows.Media.GradientStop(Color.FromRgb(ac.R, ac.G, ac.B), 0.0),
                            new System.Windows.Media.GradientStop(Color.FromRgb(r2, g2, b2), 1.0),
                        },
                        new System.Windows.Point(0, 0),
                        new System.Windows.Point(1, 1));
                    resources["AccentBrush"] = grad;
                    resources["AccentTextBrush"] = grad;
                }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); /* falls back to the solid colour already set */ }
            }
            else
            {
                // Dark theme: lighten accent by ~20% for better visibility on dark backgrounds
                try
                {
                    var ac = ParseHex(accentHex);
                    byte rl = (byte)Math.Min(255, ac.R + 40);
                    byte gl = (byte)Math.Min(255, ac.G + 40);
                    byte bl = (byte)Math.Min(255, ac.B + 40);
                    var lightBrush = new System.Windows.Media.SolidColorBrush(Color.FromRgb(rl, gl, bl));
                    lightBrush.Freeze();
                    resources["AccentBrush"] = lightBrush;
                    resources["AccentColor"] = Color.FromRgb(rl, gl, bl);

                    byte rt = (byte)(ac.R + (255 - ac.R) * 0.45);
                    byte gt = (byte)(ac.G + (255 - ac.G) * 0.45);
                    byte bt = (byte)(ac.B + (255 - ac.B) * 0.45);
                    var textBrush = new System.Windows.Media.SolidColorBrush(Color.FromRgb(rt, gt, bt));
                    textBrush.Freeze();
                    resources["AccentTextBrush"] = textBrush;
                }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); /* falls back to the solid colour already set */ }
            }

            // ── Status colours: a single palette (the "SMD Win" design system) ─────
            // Good · Check · Problem · Info. On Light, small text is readable on the card (≥ 4.5 : 1).
            // All old names (StatusOk / StatusSuccess / Ok …) get the same colour, so a state never has different shades on
            // different pages.
            string good    = isLight ? "#15803D" : "#46C07A";
            string check   = isLight ? "#B45309" : "#F2A33A";
            string problem = isLight ? "#DC2626" : "#F26464";
            string info    = isLight ? "#2563EB" : "#5B9BFF";
            foreach (var n in StatusGoodNames)    Set(n, good);
            foreach (var n in StatusCheckNames)   Set(n, check);
            foreach (var n in StatusProblemNames) Set(n, problem);
            Set("StatusInfo", info);
            Set("StatusNa", isLight ? "#6B7280" : "#8B939E");

            // Semi-transparent overlay (replaces Background="#15FFFFFF" inline usages)
            var overlayAlpha = isLight ? (byte)20 : (byte)21;
            var overlayClr   = Color.FromArgb(overlayAlpha, 255, 255, 255);
            resources["BgOverlayColor"] = overlayClr;
            resources["BgOverlayBrush"] = new SolidColorBrush(overlayClr);

            // ── Design handoff tokens (from Theme.Dark.xaml / Theme.Light.xaml) ──
            resources["Ok50Brush"]       = new SolidColorBrush(Color.FromArgb(isLight ? (byte)20 : (byte)25, isLight ? (byte)22  : (byte)34,  isLight ? (byte)163 : (byte)211, isLight ? (byte)74  : (byte)153));
            resources["Warn50Brush"]     = new SolidColorBrush(Color.FromArgb(isLight ? (byte)20 : (byte)25, isLight ? (byte)217 : (byte)251, isLight ? (byte)119 : (byte)191, isLight ? (byte)6   : (byte)36));
            resources["Crit50Brush"]     = new SolidColorBrush(Color.FromArgb(isLight ? (byte)20 : (byte)25, isLight ? (byte)220 : (byte)248, isLight ? (byte)38  : (byte)113, isLight ? (byte)38  : (byte)113));
            resources["InkBrush"]        = resources.Contains("TextPrimaryBrush")   ? resources["TextPrimaryBrush"]   : new SolidColorBrush(ParseHex(isLight ? "#161821" : "#F1F2F6"));
            resources["Ink2Brush"]       = resources.Contains("TextSecondaryBrush") ? resources["TextSecondaryBrush"] : new SolidColorBrush(ParseHex(isLight ? "#3A3D49" : "#C7CAD4"));
            resources["Ink3Brush"]       = new SolidColorBrush(ParseHex(isLight ? "#6B7280" : "#8E94A3"));
            resources["Ink4Brush"]       = new SolidColorBrush(ParseHex(isLight ? "#9CA3AF" : "#5E6473"));
            // Sidebar active item background — uses actual accent color
            var accentForSb = ParseHex(accentHex);
            // the active sidebar item (chosen, mockup "stilul 1 – pastilă tonală"): a flat tonal pill — a faint tint of the
            // accent, no glass, no shadow, no outline, 10 px corners; the icon in the accent, the text dark (Light) / light (Dark) and
            // SemiBold. Dark uses the middle step (26 %) — the lower one did not stand out on the dark menu.
            {
                // Windows navigation: a faint neutral highlight (the accent is the short bar on the left, in the button template)
                var pill = new SolidColorBrush(ParseHex(isLight ? "#0F000000" : "#14FFFFFF"));
                pill.Freeze();
                resources["SbItemActiveBase"] = pill;
                resources["SbItemActiveBg"]   = pill;
                resources["SbItemActiveEdge"] = new SolidColorBrush(Colors.Transparent);
                resources["SbItemActiveShadowColor"] = Colors.Transparent;
                resources["SbItemActiveShadowOpacity"] = 0.0;
                resources["SbItemActiveIconBrush"] = new SolidColorBrush(ParseHex(isLight ? "#2563EB" : "#5B9BFF"));
            }
            resources["SbItemActivePillFg"] = new SolidColorBrush(ParseHex(isLight ? "#15181D" : "#ECEEF1"));
            resources["SbItemActiveFg"]  = resources.Contains("AccentBrush") ? resources["AccentBrush"] : new SolidColorBrush(accentForSb);
            var railLight = Color.FromArgb(255,
                (byte)Math.Min(255, accentForSb.R + (255 - accentForSb.R) * 0.25),
                (byte)Math.Min(255, accentForSb.G + (255 - accentForSb.G) * 0.25),
                (byte)Math.Min(255, accentForSb.B + (255 - accentForSb.B) * 0.25));
            resources["SbRailBrush"] = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint   = new System.Windows.Point(0, 1),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(accentForSb, 0.0),
                    new GradientStop(railLight,   1.0),
                }
            };
            var railLighter = Color.FromArgb(255,
                (byte)Math.Min(255, accentForSb.R + (255 - accentForSb.R) * 0.42),
                (byte)Math.Min(255, accentForSb.G + (255 - accentForSb.G) * 0.42),
                (byte)Math.Min(255, accentForSb.B + (255 - accentForSb.B) * 0.42));
            resources["AccentSwitchGradientBrush"] = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint   = new System.Windows.Point(0.25, 1),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(railLighter, 0.0),
                    new GradientStop(accentForSb, 1.0),
                }
            };
            Set("ChartOrange", isLight ? "#EA580C" : "#F97316");
            // ─────────────────────────────────────────────────────────────────────

            var accentBase = ParseHex(accentHex);
            resources["AccentGradientBrush"] = new System.Windows.Media.SolidColorBrush(accentBase);
            resources["AccentColor"] = accentBase;
            resources["AccentBrush"] = new System.Windows.Media.SolidColorBrush(accentBase);
            // Data colour (charts, dials, bars): the accent, except accents that resemble a status.
            {
                var dataColor = IsStatusLikeAccent(accentBase) ? ParseHex(isLight ? "#2563EB" : "#5B9BFF") : accentBase;
                var dataBrush = new SolidColorBrush(dataColor); dataBrush.Freeze();
                var dataSoft  = new SolidColorBrush(Color.FromArgb(isLight ? (byte)0x18 : (byte)0x22, dataColor.R, dataColor.G, dataColor.B)); dataSoft.Freeze();
                resources["DataColor"]     = dataColor;
                resources["DataBrush"]     = dataBrush;
                resources["DataSoftBrush"] = dataSoft;
                // the far end of the data gradient (dials go from DataColor to this colour along the arc; charts fade into it)
                resources["DataEndColor"]  = ParseHex(isLight ? "#8B5CF6" : "#A78BFA");
            }
            resources["GreenGradientBrush"]   = new System.Windows.Media.SolidColorBrush(ParseHex("#107C10"));
            resources["RedGradientBrush"]     = new System.Windows.Media.SolidColorBrush(ParseHex("#C42B1C"));
            resources["PurpleGradientBrush"]  = new System.Windows.Media.SolidColorBrush(accentBase);
            resources["OutlineGradientBrush"] = new System.Windows.Media.SolidColorBrush(Colors.Transparent);

            // Throttle banner
            if (isLight)
            {
                SetColor("ThrottleBannerBg",    "#FEF9C3");
                SetColor("ThrottleBannerBorder", "#CA8A04");
                SetColor("ThrottleBannerFg",     "#713F12");
            }
            else
            {
                SetColor("ThrottleBannerBg",    "#7F1D1D");
                SetColor("ThrottleBannerBorder", "#EF4444");
                SetColor("ThrottleBannerFg",     "#FCA5A5");
            }

            if (isLight)
            {
                SetColor("StatusOkBg",    "#DCFCE7");  // light green (green-100)
                SetColor("StatusWarnBg",  "#FEF3C7");  // light yellow (amber-100)
                SetColor("StatusCritBg",  "#FEE2E2");  // light red (red-100)
                SetColor("StatusErrorBg", "#FEE2E2");
            }
            else
            {
                SetColor("StatusOkBg",    "#14532D");
                SetColor("StatusWarnBg",  "#451A03");
                SetColor("StatusCritBg",  "#450A0A");
                SetColor("StatusErrorBg", "#7F1D1D");
            }

            // DataGrid row colors
            SetColor("RowSeparator", isLight ? t["Border"] : "#1A2438");
            SetColor("RowAlt",       isLight ? "#E8EFF8"   : "#111620");  // more visible alternating rows
            SetColor("RowHover",     isLight ? "#D8E4F2"   : t["BgHover"]);
            SetColor("TableHeaderBg",isLight ? "#DDE6F2"   : "#0D1422");  // stronger header bg

            // Sidebar (and title bar) hover — a quiet neutral tint, not blue: the colour stays for the selected page only
            {
                var sideHov = isLight
                    ? Color.FromArgb(0x0D, 0x0F, 0x17, 0x2A)    // ~5 % slate on the light menu (#0D0F172A)
                    : Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF);   // ~6 % white on the dark menu (#0FFFFFFF)
                resources["SidebarHoverColor"] = sideHov;
                resources["SidebarHoverBrush"] = new SolidColorBrush(sideHov);
            }

            // Sidebar gradient (metal brush accent-tinted, 4 stopuri)
            var (sTop, sMidHi, sMid, sBot) = GetSidebarGradientHex(themeName, accentHex);
            resources["_SidebarTopHex"]   = sTop;
            resources["_SidebarMidHiHex"] = sMidHi;
            resources["_SidebarMidHex"]   = sMid;
            resources["_SidebarBotHex"]   = sBot;
            resources["_SidebarIsColorful"] = false;

            // Sidebar border accent-tinted
            try
            {
                var ac = ParseHex(accentHex);
                // Glass Border: right accent line — more visible alpha
                var sideBorder = isDark
                    ? Color.FromArgb(110, ac.R, ac.G, ac.B)
                    : Color.FromArgb(80,  ac.R, ac.G, ac.B);
                resources["SidebarBorderColor"] = sideBorder;
                resources["SidebarBorderBrush"] = new SolidColorBrush(sideBorder);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ThemeManager.unknown"); }

            SetColor("NavCatFg", isLight ? "#5B6677" : "#8EA3C7");   // the menu group titles: muted, but about 4.5:1 on the sidebar (the labels are drawn at 75% opacity)

            // Relief colours for the active nav item
            // Light: rim = pure white (bright highlight), inner = very pale blue
            // Dark:  rim = subtle accent, inner = darker than outer
            try
            {
                var ac = ParseHex(accentHex);
                Color rim, inner;
                if (isLight)
                {
                    rim   = Color.FromArgb(255, 255, 255, 255);
                    inner = Color.FromArgb(255, 235, 244, 255);
                }
                else
                {
                    rim   = Color.FromArgb(55, ac.R, ac.G, ac.B);
                    inner = Color.FromArgb(255, 18, 30, 52);
                }
                resources["NavActiveRimColor"]   = rim;
                resources["NavActiveInnerColor"] = inner;
                resources["NavActiveRimBrush"]   = new SolidColorBrush(rim);
                resources["NavActiveInnerBrush"] = new SolidColorBrush(inner);
            }
            catch
            {
                SetColor("NavActiveRimColor",   isLight ? "#FFFFFF" : "#1E3A5F");
                SetColor("NavActiveInnerColor", isLight ? "#EBF4FF" : "#0E1E38");
            }
            try
            {
                var c = ParseHex(t["ChartGreen"]);
                resources["ChartGreenColor"] = c;
                resources["ChartGreenBrush"] = new SolidColorBrush(c);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ThemeManager.unknown"); }

            // Sidebar text — high contrast on a white/frost sidebar
            try
            {
                // the pages not selected: text and icon in the secondary grey (sidebar decision) — about 7:1 on the Light menu,
                // 7.5:1 on the Dark one; the selected page stands out by its tinted pill, not by a brighter grey everywhere else
                var c = ParseHex(isLight ? "#4B5563" : "#A0A7B1");
                resources["SidebarTextColor"] = c;
                resources["SidebarTextBrush"] = new SolidColorBrush(c);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ThemeManager.unknown"); }
            try
            {
                // On Light: slate-900 (#0F172A) — almost black, maximum contrast
                // On Dark:  bluish white (#F4F8FF) — very visible on dark
                var c = ParseHex(isLight ? "#0F172A" : "#F4F8FF");
                resources["SidebarTextPrimaryColor"] = c;
                resources["SidebarTextPrimaryBrush"] = new SolidColorBrush(c);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ThemeManager.unknown"); }

            // Toast
            try
            {
                var bg = ParseHex(t["BgDark"]);
                var toastBg = Color.FromArgb(210, bg.R, bg.G, bg.B);
                resources["ToastBgColor"]  = toastBg;
                resources["ToastBgBrush"]  = new SolidColorBrush(toastBg);
                var toastBorder = isLight
                    ? Color.FromArgb(80,  0,   0,   0)
                    : Color.FromArgb(68, 255, 255, 255);
                resources["ToastBorderBrush"] = new SolidColorBrush(toastBorder);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ThemeManager.unknown"); }

            // Warning banner
            try
            {
                var warnBg = isLight ? ParseHex("#D97706") : ParseHex("#EF7C00");
                resources["WarningBannerBgColor"]  = warnBg;
                resources["WarningBannerBgBrush"]  = new SolidColorBrush(warnBg);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ThemeManager.unknown"); }

            try { resources.EndInit(); }
            catch (Exception ex) { AppLogger.Debug($"ThemeManager EndInit: {ex.Message}"); }

            if (brushesBefore != null) NoteReplacedBrushes(brushesBefore, resources);

            if (isAppDict)
            {
                try { ThemeChanged?.Invoke(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            }

            if (isAppDict) _lastAppliedSignature = signature;
        }

        private static string? _lastAppliedSignature;

        // ── Brushes left over from the previous theme ──────────────────────────────────────────
        // On every theme change the brushes in the resources are new objects. Elements bound through DynamicResource /
        // SetResourceReference get them automatically; those to which code assigned a brush taken from the resources once
        // (x.Foreground = FindResource("TextPrimaryBrush")) keep the old object — text or icons in the previous theme's colour.
        // This remembers which old object belongs to which key, so the window can bind them to the resource again
        // (MainWindow.RefreshThemedIcons).
        private static readonly Dictionary<System.Windows.Media.Brush, string> _staleBrushes = new(new SameObject());

        /// <summary>Compares brushes as objects (same instance), not by colour: two different brushes can have the same colour.</summary>
        private sealed class SameObject : IEqualityComparer<System.Windows.Media.Brush>
        {
            public bool Equals(System.Windows.Media.Brush? a, System.Windows.Media.Brush? b) => ReferenceEquals(a, b);
            public int GetHashCode(System.Windows.Media.Brush b) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(b);
        }

        /// <summary>If the brush is an object left over from an earlier theme, returns the key of the resource it came from.</summary>
        public static bool TryGetStaleBrushKey(System.Windows.Media.Brush brush, out string key) => _staleBrushes.TryGetValue(brush, out key!);

        /// <summary>true if at least one theme brush has been replaced since start.</summary>
        public static bool HasStaleBrushes => _staleBrushes.Count > 0;

        // The shared system brushes (Brushes.Transparent, Brushes.White …) are the same objects that XAML uses for
        // Background="Transparent" everywhere. If a theme resource was one of them, it must not be remembered as "stale":
        // every transparent panel would then be bound to that resource after the next theme change (and painted with it).
        private static readonly HashSet<System.Windows.Media.Brush> _sharedBrushes = BuildSharedBrushes();

        private static HashSet<System.Windows.Media.Brush> BuildSharedBrushes()
        {
            var set = new HashSet<System.Windows.Media.Brush>(new SameObject());
            try
            {
                foreach (var p in typeof(System.Windows.Media.Brushes).GetProperties(
                             System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                    if (p.GetValue(null) is System.Windows.Media.Brush b) set.Add(b);
            }
            catch (System.Exception ex) { AppLogger.Debug($"ThemeManager.BuildSharedBrushes: {ex.Message}"); }
            return set;
        }

        private static bool IsSharedBrush(System.Windows.Media.Brush b) =>
            _sharedBrushes.Contains(b) || (b is SolidColorBrush sc && sc.Color.A == 0);

        private static Dictionary<string, System.Windows.Media.Brush> SnapshotBrushes(ResourceDictionary resources)
        {
            var map = new Dictionary<string, System.Windows.Media.Brush>();
            try
            {
                foreach (var k in resources.Keys)
                    if (k is string name && resources[k] is System.Windows.Media.Brush b) map[name] = b;
            }
            catch (System.Exception ex) { AppLogger.Debug($"ThemeManager.SnapshotBrushes: {ex.Message}"); }
            return map;
        }

        private static void NoteReplacedBrushes(Dictionary<string, System.Windows.Media.Brush> before, ResourceDictionary resources)
        {
            try
            {
                if (_staleBrushes.Count > 4000) _staleBrushes.Clear();   // many theme changes in a row: it must not grow forever
                foreach (var (name, old) in before)
                    if (resources.Contains(name) && resources[name] is System.Windows.Media.Brush now && !ReferenceEquals(now, old)
                        && !IsSharedBrush(old))
                        _staleBrushes[old] = name;
            }
            catch (System.Exception ex) { AppLogger.Debug($"ThemeManager.NoteReplacedBrushes: {ex.Message}"); }
        }


        // ── DWM ───────────────────────────────────────────────────────────────
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MARGINS { public int Left, Right, Top, Bottom; }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

        private const int DWMWA_CAPTION_COLOR           = 35;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE  = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND                   = 2;
        private const int DWMWCP_ROUNDSMALL              = 3;
        private const int DWMWA_SYSTEMBACKDROP_TYPE      = 38;
        private const int DWMWA_MICA_EFFECT              = 1029;
        private const int DWM_SYSTEMBACKDROP_MICA        = 2;
        private const int DWM_SYSTEMBACKDROP_NONE        = 1;

        public static void ApplyRoundedCorners(IntPtr hwnd, bool small = false)
        {
            try
            {
                int pref = small ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ThemeManager.unknown"); }
        }

        public static void SetCaptionColor(IntPtr hwnd, string hex)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex)!;
                int colorref = (color.B << 16) | (color.G << 8) | color.R;
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorref, sizeof(int));
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ThemeManager.unknown"); }
        }

        public static void ApplyTitleBarColor(IntPtr hwnd, string rawThemeName)
        {
            try
            {
                string themeName = Normalize(rawThemeName);
                bool dark = IsDark(themeName);
                int darkMode = dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                bool useMica = SettingsService.Current.UseMica && IsWindows11();
                if (useMica)
                {
                    int colorNone = unchecked((int)0xFFFFFFFE);
                    DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorNone, sizeof(int));
                }
                else
                {
                    if (!Themes.TryGetValue(themeName, out var t)) return;
                    string hex = IsLight(themeName) ? t["BgDark"] : t["SidebarBg"];
                    var color  = (Color)ColorConverter.ConvertFromString(hex)!;
                    int colorref = (color.B << 16) | (color.G << 8) | color.R;
                    DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorref, sizeof(int));
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ThemeManager.unknown"); }
        }


        public static bool IsWindows11()
        {
            try
            {
                var v = Environment.OSVersion.Version;
                if (v.Major == 10 && v.Build >= 22000) return true;
                using var key = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key?.GetValue("CurrentBuildNumber") is string buildStr &&
                    int.TryParse(buildStr, out int build) && build >= 22000) return true;
                return false;
            }
            catch { return false; }
        }

        public static double MicaLayerOpacity { get; set; } = 1.0;

        public static void ApplyMica(IntPtr hwnd, bool enable, bool isDark)
        {
            if (!IsWindows11()) return;
            try
            {
                int darkMode = isDark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                var v = Environment.OSVersion.Version;
                if (v.Build >= 22621)
                {
                    int backdrop = enable ? DWM_SYSTEMBACKDROP_MICA : DWM_SYSTEMBACKDROP_NONE;
                    DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
                }
                else
                {
                    int mica = enable ? 1 : 0;
                    DwmSetWindowAttribute(hwnd, DWMWA_MICA_EFFECT, ref mica, sizeof(int));
                }

                if (enable)
                {
                    var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                    DwmExtendFrameIntoClientArea(hwnd, ref margins);
                }
                else
                {
                    var margins = new MARGINS { Left = 0, Right = 0, Top = 0, Bottom = 0 };
                    DwmExtendFrameIntoClientArea(hwnd, ref margins);
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ThemeManager.unknown"); }
        }

        // ── Custom Accent (compat API) ────────────────────────────────────────
        private static string? _customAccentHex = null;

        // ── Helpers ───────────────────────────────────────────────────────────
        public static Color ParseHex(string hex)
            => (Color)ColorConverter.ConvertFromString(hex)!;


        // ── Per-page nav accent colors ────────────────────────────────────────
        public static readonly Dictionary<string, string> NavPageAccents = new()
        {
            ["Dashboard"]   = "#3B82F6",
            ["Hardware"]    = "#8B5CF6",
            ["SysInfo"]     = "#8B5CF6",
            ["Diagnose"]    = "#10B981",
            ["Crash"]       = "#EF4444",
            ["Events"]      = "#F59E0B",
            ["Drivers"]     = "#06B6D4",
            ["Storage"]     = "#F97316",
            ["RAM"]         = "#A855F7",
            ["Network"]     = "#14B8A6",
            ["Services"]    = "#6366F1",
            ["Apps"]        = "#EC4899",
            ["Tools"]       = "#84CC16",
            ["Settings"]    = "#94A3B8",
            ["About"]       = "#64748B",
        };

        public static void SetNavPageAccent(string pageTag, ResourceDictionary resources)
        {
            if (!NavPageAccents.TryGetValue(pageTag, out var hex)) return;
            try
            {
                var c = ParseHex(hex);
                resources["NavPageAccentColor"] = c;
                resources["NavPageAccentBrush"] = new SolidColorBrush(c);
                resources["NavPageActiveBgBrush"] = new SolidColorBrush(Color.FromArgb(30, c.R, c.G, c.B));
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ThemeManager.unknown"); }
        }
    }
}
