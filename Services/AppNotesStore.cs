using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SMDWin.Services
{
    /// <summary>
    /// Technician notes about installed programs (Applications page): a short text per program, kept in a JSON file
    /// in the app's data folder. The key is the program name + publisher, so a note survives a program update.
    /// </summary>
    public sealed class AppNotesStore
    {
        public const int MaxNoteLength = 600;

        private readonly string _file;
        private readonly object _lock = new();
        private Dictionary<string, string>? _notes;

        public AppNotesStore(string file) => _file = file;

        public static string KeyOf(string? name, string? publisher)
            => ((name ?? "").Trim() + "|" + (publisher ?? "").Trim()).ToLowerInvariant();

        private Dictionary<string, string> Notes()
        {
            if (_notes != null) return _notes;
            try
            {
                if (File.Exists(_file))
                    _notes = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file));
            }
            catch (Exception ex) { AppLogger.Warning(ex, "AppNotesStore.Load"); }
            return _notes ??= new Dictionary<string, string>();
        }

        public int Count { get { lock (_lock) return Notes().Count; } }

        public string Get(string? name, string? publisher)
        {
            lock (_lock) return Notes().TryGetValue(KeyOf(name, publisher), out var note) ? note : "";
        }

        public bool Has(string? name, string? publisher) => Get(name, publisher).Length > 0;

        /// <summary>
        /// Saves the note (an empty note deletes it). Returns true if anything changed.
        /// The file is written next to the old one first and then swapped in, so an interruption never leaves a half-written file.
        /// </summary>
        public bool Set(string? name, string? publisher, string? note)
        {
            string text = (note ?? "").Trim();
            if (text.Length > MaxNoteLength) text = text[..MaxNoteLength];
            lock (_lock)
            {
                var notes = Notes();
                string key = KeyOf(name, publisher);
                notes.TryGetValue(key, out var old);
                if ((old ?? "") == text) return false;
                if (text.Length == 0) notes.Remove(key); else notes[key] = text;
                try
                {
                    string? dir = Path.GetDirectoryName(_file);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    string temp = _file + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(notes, new JsonSerializerOptions { WriteIndented = true }));
                    File.Move(temp, _file, overwrite: true);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "AppNotesStore.Save"); }
                return true;
            }
        }
    }
}
