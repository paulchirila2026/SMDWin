using System;
using System.Text.RegularExpressions;

namespace SMDWin.Services
{
    /// <summary>What is known about the router: address, MAC, manufacturer by MAC and, if the router announces itself
    /// through UPnP, the model.</summary>
    public sealed record RouterIdentity(string Gateway, string Mac, string Vendor, string Manufacturer, string Model, string FriendlyName)
    {
        /// <summary>
        /// Display name: "TP-Link Archer AX10" (UPnP), otherwise the friendly name, otherwise the manufacturer by MAC
        /// ("TP-LINK TECHNOLOGIES CO.,LTD." → "TP-LINK").
        /// </summary>
        public string DisplayName
        {
            get
            {
                string maker = ShortVendor(Manufacturer.Length > 0 ? Manufacturer : Vendor);
                if (Model.Length > 0)
                    return maker.Length > 0 && !Model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? $"{maker} {Model}" : Model;
                if (FriendlyName.Length > 0) return FriendlyName;
                return maker;
            }
        }

        /// <summary>True when the model is known too (not just the manufacturer from the MAC).</summary>
        public bool HasModel => Model.Length > 0 || FriendlyName.Length > 0;

        /// <summary>The manufacturer name without the legal form ("Co.,Ltd.", "Inc.", "Technologies"...).</summary>
        public static string ShortVendor(string? vendor)
        {
            string v = (vendor ?? "").Trim();
            if (v.Length == 0) return "";
            string shortened = Regex.Replace(v, @"[,\s]+(Co\b|Ltd\b|Inc\b|Corp\b|Corporation\b|Limited\b|GmbH\b|LLC\b|S\.?A\b|Technologies\b|Technology\b).*$", "", RegexOptions.IgnoreCase).Trim(' ', ',', '.');
            return shortened.Length > 0 ? shortened : v;
        }
    }
}
