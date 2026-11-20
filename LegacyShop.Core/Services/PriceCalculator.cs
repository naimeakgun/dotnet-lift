using System.Linq;
using LegacyShop.Core.Models;

namespace LegacyShop.Core.Services
{
    public class PriceCalculator
    {
        private readonly decimal _vatRate;

        public PriceCalculator()
            : this(AppSettings.VatRate)
        {
        }

        public PriceCalculator(decimal vatRate)
        {
            _vatRate = vatRate;
        }

        public decimal Total(Order order)
        {
            var net = order.Lines.Sum(l => l.UnitPrice * l.Quantity);
            return decimal.Round(net * (1 + _vatRate), 2);
        }
    }
}
