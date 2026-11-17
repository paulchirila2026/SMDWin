using System.Net;

namespace SMDWin.Services
{
    /// <summary>
    /// Workshop name from Settings (e.g. the repair shop's name), put in the header of PDF / HTML reports and of the
    /// diagnostic package. Empty = reports stay unbranded.
    /// </summary>
    public static class ReportBranding
    {
        public static string Workshop => SettingsService.Current?.WorkshopMode == true ? (SettingsService.Current.WorkshopName ?? "").Trim() : "";
        public static string Phone    => (SettingsService.Current?.WorkshopPhone ?? "").Trim();
        public static string Contact  => (SettingsService.Current?.WorkshopContact ?? "").Trim();

        /// <summary>"Workshop · phone", without the empty pieces.</summary>
        public static string NameAndPhone =>
            Workshop.Length == 0 ? "" : Phone.Length == 0 ? Workshop : $"{Workshop} · {Phone}";

        /// <summary>"Workshop · phone  ·  rest", or just "rest" when no name is set.</summary>
        public static string Prefix(string rest) => Workshop.Length == 0 ? rest : $"{NameAndPhone}  ·  {rest}";

        /// <summary>Lines for the text reports and the diagnostic package (empty when no name is set).</summary>
        public static string TextBlock =>
            Workshop.Length == 0 ? "" : NameAndPhone + (Contact.Length > 0 ? "\n" + Contact : "");

        /// <summary>Lines in the HTML report header (empty when no name is set).</summary>
        public static string HtmlLine => Workshop.Length == 0 ? "" :
            $"<p style='font-weight:700;opacity:1'>{WebUtility.HtmlEncode(NameAndPhone)}</p>" +
            (Contact.Length > 0 ? $"<p style='opacity:.8'>{WebUtility.HtmlEncode(Contact)}</p>" : "");
    }
}
