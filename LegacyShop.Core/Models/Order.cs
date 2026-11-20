using System;
using System.Collections.Generic;

namespace LegacyShop.Core.Models
{
    [Serializable]
    public class Order
    {
        public Order()
        {
            Lines = new List<OrderLine>();
        }

        public int Id { get; set; }
        public string CustomerEmail { get; set; }
        public DateTime CreatedAt { get; set; }
        public List<OrderLine> Lines { get; set; }
    }

    [Serializable]
    public class OrderLine
    {
        public int ProductId { get; set; }
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
    }
}
