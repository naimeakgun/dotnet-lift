using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Runtime.Caching;
using LegacyShop.Core.Models;

namespace LegacyShop.Data.Repositories
{
    public class ProductRepository
    {
        private const string AllProductsKey = "products:all";

        public IList<Product> GetAll()
        {
            var cached = MemoryCache.Default[AllProductsKey] as IList<Product>;
            if (cached != null)
            {
                return cached;
            }

            using (var db = new ShopDbContext())
            {
                var products = db.Products.OrderBy(p => p.Name).ToList();
                MemoryCache.Default.Set(AllProductsKey, products, DateTime.UtcNow.AddMinutes(5));
                return products;
            }
        }

        public Product Find(int id)
        {
            using (var db = new ShopDbContext())
            {
                return db.Products.Find(id);
            }
        }

        public string CurrentUserName()
        {
            // A data layer reaching into the web request: very common in legacy code.
            return HttpContext.Current != null && HttpContext.Current.User != null
                ? HttpContext.Current.User.Identity.Name
                : "anonymous";
        }
    }
}
