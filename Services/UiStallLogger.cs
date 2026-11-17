using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Threading;

namespace SMDWin.Services
{
    /// <summary>
    /// Logs any UI thread operation that takes over 500 ms: what ran (type + method), with what priority, and how much of
    /// the time was a garbage collector (GC) pause. So a freeze can be traced to its cause, without guessing.
    /// For a DispatcherTimer tick it shows the handler (e.g. MainWindow.OnTempTick), and for an "await" continuation the
    /// async method it comes from. <see cref="Mark"/> places markers inside a long operation: on a freeze it shows between
    /// which markers the time was lost.
    /// </summary>
    internal static class UiStallLogger
    {
        private const int ThresholdMs = 500;
        private static readonly Stack<(Stopwatch Sw, TimeSpan Gc, int G0, int G1, int G2)> _stack = new();
        private static readonly List<(string Label, long Ms)> _marks = new();
        private static FieldInfo? _methodField, _argsField, _tickField;
        private static bool _installed;

        /// <summary>Blocks of the window over the threshold since start, and their total length (read by the self-test).</summary>
        public static int StallCount;
        public static long StallTotalMs;
        public static long LongestStallMs;
        private static int _uiThreadId;

        public static void Install(Dispatcher dispatcher)
        {
            if (_installed) return;
            _installed = true;
            _uiThreadId = Environment.CurrentManagedThreadId;
            try
            {
                dispatcher.Hooks.OperationStarted += (_, e) =>
                {
                    if (_stack.Count == 0) _marks.Clear();
                    _stack.Push((Stopwatch.StartNew(), GC.GetTotalPauseDuration(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)));
                };
                dispatcher.Hooks.OperationCompleted += (_, e) =>
                {
                    if (_stack.Count == 0) return;
                    var s = _stack.Pop();
                    long ms = s.Sw.ElapsedMilliseconds;
                    if (ms < ThresholdMs) return;
                    // over 2 minutes: the computer was in sleep / hibernation with the operation running, not a window freeze
                    if (ms > 120_000) return;
                    StallCount++;
                    StallTotalMs += ms;
                    if (ms > LongestStallMs) LongestStallMs = ms;
                    try
                    {
                        double gcMs = (GC.GetTotalPauseDuration() - s.Gc).TotalMilliseconds;
                        string marks = "";
                        if (_stack.Count == 0 && _marks.Count > 0)
                        {
                            var sb = new StringBuilder(" | repere: ");
                            for (int i = 0; i < _marks.Count && i < 24; i++)
                                sb.Append(i > 0 ? ", " : "").Append(_marks[i].Label).Append(" +").Append(_marks[i].Ms).Append("ms");
                            marks = sb.ToString();
                        }
                        AppLogger.Warning($"UI-STALL {ms} ms: {Describe(e.Operation)} | priority {e.Operation.Priority} | GC pause {gcMs:F0} ms " +
                            $"(collections gen0/1/2: +{GC.CollectionCount(0) - s.G0}/+{GC.CollectionCount(1) - s.G1}/+{GC.CollectionCount(2) - s.G2}){marks}");
                    }
                    catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                };
            }
            catch (Exception ex) { AppLogger.Debug($"UiStallLogger.Install: {ex.Message}"); }
        }

        /// <summary>Marker in the current UI thread operation (ignored on other threads). Costs a few tens of nanoseconds.</summary>
        public static void Mark(string label)
        {
            if (!_installed || Environment.CurrentManagedThreadId != _uiThreadId || _stack.Count == 0) return;
            if (_marks.Count >= 64) return;
            // time since the start of the outermost operation (the last in the stack = the first started)
            long ms = 0;
            foreach (var item in _stack) ms = item.Sw.ElapsedMilliseconds;
            _marks.Add((label, ms));
        }

        private static string Describe(DispatcherOperation op)
        {
            try
            {
                const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
                _methodField ??= typeof(DispatcherOperation).GetField("_method", Private);
                _argsField ??= typeof(DispatcherOperation).GetField("_args", Private);
                if (_methodField?.GetValue(op) is not Delegate d) return "operatie necunoscuta";
                string name = DescribeDelegate(d, 0);

                object? args = _argsField?.GetValue(op);
                if (args is object[] array && array.Length > 0) args = array[0];
                if (args is DispatcherTimer timer)
                    return $"DispatcherTimer({timer.Interval.TotalMilliseconds:0} ms) → {TickHandlers(timer)}";
                if (args is Delegate inner)
                    return $"{name} → {DescribeDelegate(inner, 0)}";
                return name;
            }
            catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            return "operatie necunoscuta";
        }

        // the Tick event handlers (the event's private field, in DispatcherTimer)
        private static string TickHandlers(DispatcherTimer timer)
        {
            try
            {
                _tickField ??= typeof(DispatcherTimer).GetField("Tick", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_tickField?.GetValue(timer) is Delegate tick)
                    return string.Join(" + ", tick.GetInvocationList().Select(h => DescribeDelegate(h, 0)));
            }
            catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            return "handler necunoscut";
        }

        private static string DescribeDelegate(Delegate d, int depth)
        {
            try
            {
                object? target = d.Target;
                Type? targetType = target?.GetType();
                // the continuation of an "await": the target is the state machine box → the async method name is in the generic type
                if (targetType != null && targetType.IsGenericType)
                {
                    var arg = targetType.GetGenericArguments().LastOrDefault();
                    if (arg != null && arg.Name.Contains('<'))
                        return $"await {arg.DeclaringType?.Name}.{arg.Name}";
                }
                // continuation wrapper (ContinuationWrapper): the real delegate is in a field
                if (targetType != null && depth < 3 && targetType.Name.Contains("Continuation", StringComparison.Ordinal))
                {
                    foreach (var f in targetType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
                        if (typeof(Delegate).IsAssignableFrom(f.FieldType) && f.GetValue(target) is Delegate next && !ReferenceEquals(next, d))
                            return DescribeDelegate(next, depth + 1);
                }
                string type = d.Method.DeclaringType?.Name ?? "?";
                // lambda: the generated type "<>c__DisplayClass…" says nothing → use the type that contains it
                if (type.StartsWith("<>", StringComparison.Ordinal) && d.Method.DeclaringType?.DeclaringType is { } outer)
                    type = outer.Name;
                return $"{type}.{d.Method.Name}";
            }
            catch { return "?"; }
        }
    }
}
