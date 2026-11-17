
namespace SMDWin.Models
{
    public sealed class SmartInfoRow
    {
        private const string Gray = "#9CA3AF";

        public string IdHex => "";
        public string Name { get; init; } = "";
        public string CurrentValue => "";
        public string WorstValue => "";
        public string ThresholdValue => "";
        public string RawDisplay => "";
        public string FriendlyDescription { get; init; } = "";
        public string TrafficLightColor { get; init; } = Gray;
        public string StatusColor => TrafficLightColor;
        /// <summary>Empty on informational rows; "OK"/"WARNING" only where there is a verdict (e.g. the disk state).</summary>
        public string StatusText { get; init; } = "";
        public string AttributeTooltip => FriendlyDescription;
        public string RowBackground => "Transparent";
    }
}
