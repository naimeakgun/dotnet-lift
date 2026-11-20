using System.Collections.Generic;
using System.Web;
using System.Web.Mvc;
using System.Web.Security;
using LegacyShop.Core.Models;
using LegacyShop.Core.Services;

namespace LegacyShop.Web.Controllers
{
    [Authorize]
    public class CartController : Controller
    {
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Add(int productId, int quantity)
        {
            var cart = Session["Cart"] as List<OrderLine> ?? new List<OrderLine>();
            cart.Add(new OrderLine { ProductId = productId, Quantity = quantity });
            Session["Cart"] = cart;
            Session["CartCount"] = cart.Count;

            var cookie = new HttpCookie("cart-snapshot",
                System.Convert.ToBase64String(SnapshotSerializer.Serialize(cart)));
            Response.Cookies.Add(cookie);

            return RedirectToAction("Index", "Home");
        }

        public ActionResult Checkout()
        {
            var user = HttpContext.User.Identity.Name;
            var source = Request.QueryString["source"];
            ViewBag.Message = "Checkout for " + user + " from " + source;
            return View();
        }

        public ActionResult Logout()
        {
            FormsAuthentication.SignOut();
            return RedirectToAction("Index", "Home");
        }
    }
}
