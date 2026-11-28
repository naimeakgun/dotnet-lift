using System.Data.Entity;
using LegacyShop.Core.Models;

namespace LegacyShop.Data
{
    public class ShopDbContext : DbContext
    {
        public ShopDbContext()
            : base("name=ShopDb")
        {
        }

        public DbSet<Product> Products { get; set; }
        public DbSet<Order> Orders { get; set; }

        protected override void OnModelCreating(DbModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Product>().Property(p => p.Price).HasPrecision(18, 2);
            modelBuilder.Entity<OrderLine>().HasKey(l => new { l.ProductId, l.Quantity });
            base.OnModelCreating(modelBuilder);
        }
    }
}
