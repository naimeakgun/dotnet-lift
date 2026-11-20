using System.Configuration;
using System.Web.Mvc;
using LegacyShop.Data.Repositories;

namespace LegacyShop.Web.Controllers
{
    public class HomeController : Controller
    {
        private readonly ProductRepository _products = new ProductRepository();

        public ActionResult Index()
        {
            ViewBag.ShopName = ConfigurationManager.AppSettings["ShopName"];
            var products = _products.GetAll();
            return View(products);
        }

        [ChildActionOnly]
        public ActionResult MiniCart()
        {
            var count = Session["CartCount"] as int? ?? 0;
            return PartialView("_MiniCart", count);
        }

        public ActionResult Logo()
        {
            var path = Server.MapPath("~/Content/logo.png");
            return File(path, "image/png");
        }
    }
}
