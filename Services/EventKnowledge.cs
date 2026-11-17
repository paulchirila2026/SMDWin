using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SMDWin.Services
{
    /// <summary>
    /// Database of Windows events explained in plain words (Data/EventDB/events.json, ~200 entries, sources in
    /// Data/EventDB/SOURCES.md). Looked up by Event ID + source (the full provider name or one of the short names
    /// in the Source column). Loaded once, on first use.
    /// </summary>
    public static class EventKnowledge
    {
        public sealed class Entry
        {
            public string Provider { get; set; } = "";
            public List<string>? Aliases { get; set; }
            public long Id { get; set; }
            public string Log { get; set; } = "";
            public string Severity { get; set; } = "";   // routine | watch | serious
            public string Title { get; set; } = "";
            public string Meaning { get; set; } = "";
            public List<string>? Steps { get; set; }
            public string? Page { get; set; }
        }

        private static Dictionary<long, List<Entry>>? _byId;
        private static readonly object _lock = new();

        public static int Count { get { Load(); return _byId!.Values.Sum(v => v.Count); } }

        private static void Load()
        {
            if (_byId != null) return;
            lock (_lock)
            {
                if (_byId != null) return;
                var map = new Dictionary<long, List<Entry>>();
                try
                {
                    var asm = typeof(EventKnowledge).Assembly;
                    using var rs = asm.GetManifestResourceStream("SMDWinBundle/EventDB/events.json");
                    if (rs != null)
                    {
                        var list = JsonSerializer.Deserialize<List<Entry>>(rs, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
                        foreach (var e in list)
                        {
                            if (!map.TryGetValue(e.Id, out var l)) map[e.Id] = l = new List<Entry>();
                            l.Add(e);
                        }
                    }
                    else AppLogger.Warning("EventKnowledge: embedded events.json not found");
                }
                catch (Exception ex) { AppLogger.Warning(ex, "EventKnowledge.Load"); }
                _byId = map;
            }
        }

        private static string Norm(string s) => s.Replace("Microsoft-Windows-", "", StringComparison.OrdinalIgnoreCase).Trim();

        /// <summary>The entry for (source, id) or null. Exact name match first, then the short form.</summary>
        public static Entry? Find(string? source, long id) => Localize(FindEnglish(source, id));

        private sealed class RoEntry { public string Title { get; set; } = ""; public string Meaning { get; set; } = ""; public List<string>? Steps { get; set; } }
        private static Dictionary<string, RoEntry>? _ro;
        private static readonly Dictionary<Entry, Entry> _roCache = new();

        /// <summary>For Romanian: title, explanation and steps from events.ro.json (the other fields stay).</summary>
        private static Entry? Localize(Entry? e)
        {
            if (e == null || LanguageService.CurrentCode != "ro") return e;
            lock (_lock)
            {
                if (_ro == null)
                {
                    _ro = new Dictionary<string, RoEntry>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        using var rs = typeof(EventKnowledge).Assembly.GetManifestResourceStream("SMDWinBundle/EventDB/events.ro.json");
                        if (rs != null)
                            _ro = JsonSerializer.Deserialize<Dictionary<string, RoEntry>>(rs, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                                  ?? _ro;
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "EventKnowledge.Localize"); }
                }
                if (_roCache.TryGetValue(e, out var cached)) return cached;
                Entry result = e;
                if (_ro.TryGetValue($"{e.Provider}|{e.Id}", out var r) && !string.IsNullOrWhiteSpace(r.Title))
                    result = new Entry
                    {
                        Provider = e.Provider, Aliases = e.Aliases, Id = e.Id, Log = e.Log, Severity = e.Severity, Page = e.Page,
                        Title = r.Title, Meaning = string.IsNullOrWhiteSpace(r.Meaning) ? e.Meaning : r.Meaning,
                        Steps = r.Steps is { Count: > 0 } ? r.Steps : e.Steps
                    };
                _roCache[e] = result;
                return result;
            }
        }

        private static Entry? FindEnglish(string? source, long id)
        {
            Load();
            if (string.IsNullOrWhiteSpace(source) || !_byId!.TryGetValue(id, out var cands)) return null;
            string src = source.Trim(), srcN = Norm(src);
            foreach (var e in cands)
            {
                if (string.Equals(e.Provider, src, StringComparison.OrdinalIgnoreCase)) return e;
                if (e.Aliases != null && e.Aliases.Any(a => string.Equals(a, src, StringComparison.OrdinalIgnoreCase))) return e;
            }
            foreach (var e in cands)
            {
                if (string.Equals(Norm(e.Provider), srcN, StringComparison.OrdinalIgnoreCase)) return e;
                if (e.Aliases != null && e.Aliases.Any(a => string.Equals(Norm(a), srcN, StringComparison.OrdinalIgnoreCase))) return e;
            }
            return null;
        }
    }
}
