using System.Collections.Generic;
using System.Web.Http;
using LegacyShop.Core.Models;
using LegacyShop.Data.Repositories;

namespace LegacyShop.Web.Controllers
{
    [RoutePrefix("api/products")]
    public class ProductsApiController : ApiController
    {
        private readonly ProductRepository _products = new ProductRepository();

        [HttpGet, Route("")]
        public IEnumerable<Product> GetAll()
        {
            return _products.GetAll();
        }

        [HttpGet, Route("{id:int}")]
        public IHttpActionResult Get(int id)
        {
            var product = _products.Find(id);
            if (product == null)
            {
                return NotFound();
            }
            return Ok(product);
        }
    }
}
