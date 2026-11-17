using System.Text.RegularExpressions;

namespace SMDWin.Services
{
    public static class EventSubscriptionAuditor
    {
        public readonly struct SubscriptionCounts
        {
            public readonly int SubscribeCount;
            public readonly int UnsubscribeCount;
            public bool IsBalanced => SubscribeCount == UnsubscribeCount;

            public SubscriptionCounts(int subscribeCount, int unsubscribeCount)
            {
                SubscribeCount = subscribeCount;
                UnsubscribeCount = unsubscribeCount;
            }
        }

        /// <summary>Counts the occurrences of "eventName +=" and "eventName -=" in source code text already read.
        /// Does not touch the disk — easy to test with made-up source, and reusable for any similar static event.</summary>
        public static SubscriptionCounts CountSubscriptions(string sourceText, string eventName)
        {
            if (string.IsNullOrEmpty(sourceText) || string.IsNullOrEmpty(eventName))
                return new SubscriptionCounts(0, 0);

            string escaped = Regex.Escape(eventName);
            int subs = Regex.Matches(sourceText, escaped + @"\s*\+=").Count;
            int unsubs = Regex.Matches(sourceText, escaped + @"\s*-=").Count;
            return new SubscriptionCounts(subs, unsubs);
        }
    }
}
