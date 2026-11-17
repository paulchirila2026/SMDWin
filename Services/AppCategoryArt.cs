using System;
using System.Collections.Generic;
using System.Linq;

namespace SMDWin.Services
{
    /// <summary>Kind of application from its process name: decides the category shown and its drawing.</summary>
    public enum AppKind { Browser, Communication, Gaming, Remote, Cloud, Media, Development, System, Other }

    public static class AppKinds
    {
        private static readonly Dictionary<string, AppKind> ByName = Create();

        private static Dictionary<string, AppKind> Create()
        {
            var d = new Dictionary<string, AppKind>(StringComparer.OrdinalIgnoreCase);
            void Add(AppKind k, params string[] names) { foreach (var n in names) d[n] = k; }
            Add(AppKind.Browser, "chrome", "firefox", "msedge", "opera", "brave", "iexplore", "vivaldi", "chromium", "msedgewebview2");
            Add(AppKind.Communication, "discord", "slack", "teams", "ms-teams", "zoom", "telegram", "whatsapp", "skype", "signal", "viber", "outlook", "thunderbird");
            Add(AppKind.Gaming, "steam", "steamwebhelper", "epicgameslauncher", "origin", "upc", "gog", "galaxyclient", "riotclientservices", "battle.net", "eadesktop");
            Add(AppKind.Remote, "anydesk", "teamviewer", "teamviewer_service", "mstsc", "rustdesk", "parsecd", "ultraviewer");
            Add(AppKind.Cloud, "onedrive", "dropbox", "googledrivefs", "drive", "megasync", "nextcloud", "icloudservices");
            Add(AppKind.Media, "spotify", "vlc", "itunes", "plex", "netflix", "obs64", "obs", "tidal");
            Add(AppKind.Development, "code", "devenv", "git", "node", "dotnet", "java", "javaw", "python", "pycharm64", "idea64", "rider64", "docker", "ssh");
            Add(AppKind.System, "svchost", "lsass", "services", "wininit", "csrss", "smss", "system", "dns", "searchhost", "spoolsv", "msmpeng", "wuauserv", "dashost");
            return d;
        }

        public static AppKind Classify(string? processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return AppKind.Other;
            string n = processName.Trim();
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
            return ByName.TryGetValue(n, out var k) ? k : AppKind.Other;
        }

        /// <summary>Display name of the category (also used for grouping).</summary>
        public static string Label(AppKind kind) => kind switch
        {
            AppKind.Browser       => "Browser",
            AppKind.Communication => "Communication",
            AppKind.Gaming        => "Gaming",
            AppKind.Remote        => "Remote access",
            AppKind.Cloud         => "Cloud sync",
            AppKind.Media         => "Media",
            AppKind.Development   => "Development",
            AppKind.System        => "System",
            _                     => "Other",
        };
    }

    /// <summary>Icons for the application categories, in a 40 x 40 box, built from the same shapes as the other illustrations.</summary>
    public static class AppArt
    {
        public const double Size = 40;

        private static ArtShape R(double x, double y, double w, double h, ArtFill f, double r = 0, double o = 1) => ComputerArt.R(x, y, w, h, f, r, o);
        private static ArtShape Disc(double cx, double cy, double d, ArtFill f, double o = 1) => ComputerArt.Disc(cx, cy, d, f, o);
        private static ArtShape Poly(ArtFill f, params (double X, double Y)[] p) => ComputerArt.Poly(f, 1, p);

        private static readonly ArtDrawing[] Cache = new ArtDrawing[Enum.GetValues<AppKind>().Length];

        /// <summary>The category drawing, built once (list rows request it on every scroll).</summary>
        public static ArtDrawing For(AppKind kind) => Cache[(int)kind] ??= Build(kind);

        public static ArtDrawing Build(AppKind kind)
        {
            var s = new List<ArtShape>();
            switch (kind)
            {
                case AppKind.Browser:
                    s.Add(Disc(20, 20, 35, ArtFill.LidShade));
                    s.Add(Disc(20, 20, 31, ArtFill.Accent));
                    s.Add(R(18.5, 5, 3, 30, ArtFill.White, 1.5, 0.75));
                    s.Add(R(5, 18.5, 30, 3, ArtFill.White, 1.5, 0.75));
                    break;

                case AppKind.Communication:
                    s.Add(R(3, 6, 24, 17, ArtFill.Accent, 5));
                    s.Add(Poly(ArtFill.Accent, (8, 21), (8, 29), (16, 21)));
                    s.Add(R(16, 16, 21, 15, ArtFill.Lid, 5, 0.95));
                    s.Add(Poly(ArtFill.Lid, (31, 29), (31, 36), (24, 29)));
                    for (int i = 0; i < 3; i++) s.Add(Disc(9.5 + i * 6.5, 14.5, 3, ArtFill.White));
                    break;

                case AppKind.Gaming:
                    s.Add(R(3, 11, 34, 20, ArtFill.Lid, 10));
                    s.Add(R(3, 22, 10, 12, ArtFill.LidShade, 5)); s.Add(R(27, 22, 10, 12, ArtFill.LidShade, 5));
                    s.Add(R(9, 18.5, 10, 3.4, ArtFill.Die, 1)); s.Add(R(12.3, 15.2, 3.4, 10, ArtFill.Die, 1));
                    s.Add(Disc(27.5, 17.5, 4.4, ArtFill.Accent)); s.Add(Disc(32, 22.5, 4.4, ArtFill.Ok));
                    break;

                case AppKind.Remote:
                    s.Add(R(3, 5, 34, 23, ArtFill.Lid, 3));
                    s.Add(R(6, 8, 28, 17, ArtFill.Die, 1.5));
                    s.Add(R(17, 28, 6, 5, ArtFill.LidShade));
                    s.Add(R(11, 33, 18, 3, ArtFill.LidShade, 1.5));
                    s.Add(Poly(ArtFill.Accent, (15, 10), (15, 22), (18.2, 19), (21, 24), (23.6, 22.8), (20.8, 17.8), (25, 17.8)));
                    break;

                case AppKind.Cloud:
                    s.Add(Disc(13.5, 24, 15, ArtFill.Accent)); s.Add(Disc(22, 18, 19, ArtFill.Accent)); s.Add(Disc(30, 24.5, 13, ArtFill.Accent));
                    s.Add(R(11, 24, 25, 9, ArtFill.Accent, 3));
                    s.Add(Poly(ArtFill.White, (20, 14), (26, 20.5), (22, 20.5), (22, 29), (18, 29), (18, 20.5), (14, 20.5)));
                    break;

                case AppKind.Media:
                    s.Add(R(3, 7, 34, 26, ArtFill.Lid, 6));
                    s.Add(R(3, 28, 34, 5, ArtFill.LidShade, 4, 0.85));
                    s.Add(Poly(ArtFill.Accent, (16, 12), (16, 26), (28.5, 19)));
                    break;

                case AppKind.Development:
                    s.Add(R(3, 6, 34, 28, ArtFill.Lid, 4));
                    s.Add(R(3, 6, 34, 6, ArtFill.LidShade, 4));
                    s.Add(Poly(ArtFill.Accent, (9, 16), (11.4, 14), (19, 21), (11.4, 28), (9, 26), (14.2, 21)));
                    s.Add(R(21, 25.5, 11, 2.6, ArtFill.White, 1, 0.85));
                    break;

                case AppKind.System:
                    s.Add(R(17, 3, 6, 9, ArtFill.Lid, 1)); s.Add(R(17, 28, 6, 9, ArtFill.Lid, 1));
                    s.Add(R(3, 17, 9, 6, ArtFill.Lid, 1)); s.Add(R(28, 17, 9, 6, ArtFill.Lid, 1));
                    foreach (var (cx, cy) in new[] { (9.5, 9.5), (30.5, 9.5), (9.5, 30.5), (30.5, 30.5) })
                        s.Add(Poly(ArtFill.Lid, (cx, cy - 4.8), (cx + 4.8, cy), (cx, cy + 4.8), (cx - 4.8, cy)));
                    s.Add(Disc(20, 20, 26, ArtFill.Lid));
                    s.Add(Disc(20, 20, 12, ArtFill.Card));
                    s.Add(Disc(20, 20, 5, ArtFill.Accent));
                    break;

                default:
                    s.Add(R(5, 13, 30, 21, ArtFill.Lid, 3));
                    s.Add(R(3, 8, 34, 8, ArtFill.LidShade, 2));
                    s.Add(R(16, 8, 8, 12, ArtFill.Accent, 1, 0.9));
                    break;
            }
            return new ArtDrawing(Size, Size, s);
        }
    }
}
