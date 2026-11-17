using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SMDWin.Services
{
    /// <summary>
    /// Loads and serves localisation strings from JSON language pack files.
    /// Built-in packs (en, ro) are embedded; additional packs can be imported.
    /// </summary>
    public static class LanguageService
    {
        // The loaded dictionary: section → key → value
        private static Dictionary<string, Dictionary<string, string>> _data = new();
        private static string _currentCode = "en";

        // ── Language pack metadata ─────────────────────────────────────────────
        public static string CurrentCode   => _currentCode;

        public static System.Globalization.CultureInfo UiCulture => _currentCode switch
        {
            "ro" => new System.Globalization.CultureInfo("ro-RO"),
            "de" => new System.Globalization.CultureInfo("de-DE"),
            "fr" => new System.Globalization.CultureInfo("fr-FR"),
            _    => new System.Globalization.CultureInfo("en-US"),
        };
        public static string CurrentName   => Get("Meta", "Name", _currentCode);

        // The packs included in the exe (en/ro/fr/de), extracted by AppPaths.BundleDirectory.
        private static string LangFolder =>
            Path.Combine(AppPaths.BundleDirectory, "Languages");

        // Packs imported by the user: next to the exe (portable, they survive new builds).
        private static string UserLangFolder => AppPaths.Data("Languages");

        // ── Discover available packs ───────────────────────────────────────────
        public static List<(string Code, string Name, string Path)> GetAvailablePacks()
        {
            var result = new List<(string, string, string)>();

            // Built-in packs embedded as content files
            foreach (var code in new[] { "en", "ro", "fr", "de" })
            {
                var path = Path.Combine(LangFolder, $"{code}.json");
                if (File.Exists(path))
                {
                    string name = code switch { "en" => "English", "ro" => "Română", "fr" => "Français", "de" => "Deutsch", _ => code };
                    try
                    {
                        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path));
                        if (raw != null && raw.TryGetValue("Meta", out var meta))
                        {
                            var metaDict = JsonSerializer.Deserialize<Dictionary<string, string>>(meta.GetRawText());
                            if (metaDict != null && metaDict.TryGetValue("Name", out var n)) name = n;
                        }
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "LanguageService.unknown"); }
                    result.Add((code, name, path));
                }
            }

            // Extra imported packs (the user's folder; the bundle has only the 4 included ones)
            var seenExtra = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in new[] { UserLangFolder, LangFolder })
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.GetFiles(folder, "*.json"))
                {
                    string code = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    if (code == "en" || code == "ro" || code == "fr" || code == "de") continue; // already added
                    if (!seenExtra.Add(code)) continue; // the same package in both folders
                    try
                    {
                        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(file));
                        string name = code, displayCode = code;
                        if (raw != null && raw.TryGetValue("Meta", out var meta))
                        {
                            var metaDict = JsonSerializer.Deserialize<Dictionary<string, string>>(meta.GetRawText());
                            if (metaDict != null)
                            {
                                if (metaDict.TryGetValue("Name", out var n)) name = n;
                                if (metaDict.TryGetValue("Code", out var c)) displayCode = c;
                            }
                        }
                        result.Add((displayCode, name, file));
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "LanguageService.unknown"); }
                }
            }

            return result;
        }

        // ── Load a language pack ───────────────────────────────────────────────
        public static bool Load(string code)
        {
            var packs = GetAvailablePacks();
            var pack = packs.Find(p => p.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

            string? json = null;

            if (!string.IsNullOrEmpty(pack.Path) && File.Exists(pack.Path))
            {
                try { json = File.ReadAllText(pack.Path); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
            }

            if (json == null)
            {
                // Fallback to English
                var en = packs.Find(p => p.Code == "en");
                if (!string.IsNullOrEmpty(en.Path) && File.Exists(en.Path))
                    try { json = File.ReadAllText(en.Path); } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }
            }

            if (json == null) return false;

            try
            {
                var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (root == null) return false;

                _data = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (section, elem) in root)
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(elem.GetRawText());
                    if (dict != null)
                        _data[section] = dict;
                }

                _currentCode = code.ToLowerInvariant();
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, $"LanguageService.Load failed for code '{code}'");
                return false;
            }
        }


        // ── Get a string ───────────────────────────────────────────────────────
        public static string Get(string section, string key, string? fallback = null)
        {
            if (_data.TryGetValue(section, out var dict) && dict.TryGetValue(key, out var val))
                return val;
            return fallback ?? $"[{section}.{key}]";
        }

        // Convenience accessors
        public static string S(string section, string key) => Get(section, key);
        public static string S(string section, string key, string fallback) => Get(section, key, fallback);
    }
}
