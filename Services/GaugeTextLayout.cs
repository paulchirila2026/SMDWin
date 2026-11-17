using System;

namespace SMDWin.Services
{
    /// <summary>
    /// Layout of the text in the centre of a dial (270° arc): the digits sit exactly on the arc centre, with the small
    /// caption under them. The digits themselves are centred, not the text line box — the box has empty space above and
    /// below, which pushed the value up from the centre.
    /// </summary>
    public static class GaugeTextLayout
    {
        /// <param name="centerY">Centre of the arc.</param>
        /// <param name="valueBaseline">Distance from the top of the value line to the text baseline.</param>
        /// <param name="capsHeight">Height of the digits (capitals) at the size used.</param>
        /// <returns>Where the value line is drawn and where the caption below it starts.</returns>
        public static (double valueTop, double captionTop) Center(double centerY, double valueBaseline, double capsHeight)
        {
            double inkBottom = centerY + capsHeight / 2;                 // linia de baza a cifrelor
            double gap = Math.Max(2, capsHeight * 0.22);
            return (inkBottom - valueBaseline, inkBottom + gap);
        }
    }
}
