using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace SMDWin.Services
{
    public enum NotifCategory
    {
        Temperature,
        Throttle,
        Sensor,
        Network,
        Disk,
        System,
    }

    public enum NotifSeverity { Info = 0, Warning = 1, Critical = 2 }

    public class NotificationEntry
    {
        public Guid Id { get; } = Guid.NewGuid();
        public DateTime TimeUtc { get; init; } = DateTime.UtcNow;
        public NotifCategory Category { get; init; }
        public NotifSeverity Severity { get; init; }
        public string Message { get; init; } = "";

        /// <summary>"2 min ago", "3 h ago", otherwise the date (for the list in the notification centre).</summary>
        public string AgeLabel
        {
            get
            {
                var age = DateTime.UtcNow - TimeUtc;
                bool ro = LanguageService.CurrentCode == "ro";
                if (age.TotalSeconds < 60) return ro ? "acum" : "just now";
                if (age.TotalMinutes < 60) return ro ? $"acum {(int)age.TotalMinutes} min" : $"{(int)age.TotalMinutes} min ago";
                if (age.TotalHours < 24) return ro ? $"acum {(int)age.TotalHours} h" : $"{(int)age.TotalHours} h ago";
                return TimeUtc.ToLocalTime().ToString("dd MMM HH:mm");
            }
        }

        /// <summary>Group in the list: today / older.</summary>
        public string DayGroup
        {
            get
            {
                bool ro = LanguageService.CurrentCode == "ro";
                return TimeUtc.ToLocalTime().Date == DateTime.Now.Date ? (ro ? "Azi" : "Today") : (ro ? "Mai vechi" : "Earlier");
            }
        }

        public string TimeLabel => TimeUtc.ToLocalTime().ToString("HH:mm:ss");
        public string DateLabel => TimeUtc.ToLocalTime().ToString("dd MMM");

        public string CategoryIcon => Category switch
        {
            NotifCategory.Temperature => "🌡",
            NotifCategory.Throttle    => "🐢",
            NotifCategory.Sensor      => "🔧",
            NotifCategory.Network     => "📶",
            NotifCategory.Disk        => "💾",
            _                         => "ℹ",
        };

        public string CategoryLabel => Category switch
        {
            NotifCategory.Temperature => "Temperature",
            NotifCategory.Throttle    => "Throttle",
            NotifCategory.Sensor      => "Sensors",
            NotifCategory.Network     => "Network",
            NotifCategory.Disk        => "Storage",
            _                         => "System",
        };
    }

    public static class NotificationCenter
    {
        // ObservableCollection so the UI (badge count + window list) updates automatically through binding, without polling.
        public static readonly ObservableCollection<NotificationEntry> Entries = new();

        /// <summary>Number of UNREAD entries — reset when the user opens the window.</summary>
        public static int UnreadCount { get; private set; } = 0;

        public static event Action? Changed;

        // Anti-spam: the EXACT same (category + message) is not added again if the last identical entry is newer than this
        // interval — a sensor oscillating around the threshold (89.8 / 90.1 / 89.9 °C) must not fill the list with 50
        // identical rows in 2 minutes.
        private static readonly TimeSpan DedupWindow = TimeSpan.FromMinutes(2);

        public static void Add(NotifCategory category, NotifSeverity severity, string message)
        {
            void Do()
            {
                var last = Entries.FirstOrDefault(e => e.Category == category && e.Message == message);
                if (last != null && (DateTime.UtcNow - last.TimeUtc) < DedupWindow)
                    return; // duplicat recent, ignoram

                Entries.Insert(0, new NotificationEntry
                {
                    Category = category,
                    Severity = severity,
                    Message = message,
                });

                // Reasonable cap — thousands of entries are not kept in memory forever.
                while (Entries.Count > 300) Entries.RemoveAt(Entries.Count - 1);

                UnreadCount++;
                Changed?.Invoke();
            }

            if (System.Windows.Application.Current?.Dispatcher is Dispatcher d && !d.CheckAccess())
                d.BeginInvoke(Do);
            else
                Do();
        }

        public static void MarkAllRead()
        {
            if (UnreadCount == 0) return;
            UnreadCount = 0;
            Changed?.Invoke();
        }

        public static void Remove(Guid id)
        {
            var item = Entries.FirstOrDefault(e => e.Id == id);
            if (item != null) Entries.Remove(item);
            Changed?.Invoke();
        }

        public static void ClearAll()
        {
            Entries.Clear();
            UnreadCount = 0;
            Changed?.Invoke();
        }

        /// <summary>Highest current severity in the list — used for the title bar badge colour (red if there is at least one
        /// Critical, orange if only Warning, otherwise neutral).</summary>
        public static NotifSeverity HighestSeverity()
            => Entries.Count == 0 ? NotifSeverity.Info : Entries.Max(e => e.Severity);
    }
}
