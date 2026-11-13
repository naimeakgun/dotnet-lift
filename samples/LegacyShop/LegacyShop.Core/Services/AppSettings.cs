using System.Configuration;
using System.Globalization;

namespace LegacyShop.Core.Services
{
    public static class AppSettings
    {
        public static decimal VatRate
        {
            get
            {
                var raw = ConfigurationManager.AppSettings["VatRate"] ?? "0.20";
                return decimal.Parse(raw, CultureInfo.InvariantCulture);
            }
        }

        public static string ShopName
        {
            get { return ConfigurationManager.AppSettings["ShopName"]; }
        }
    }
}
