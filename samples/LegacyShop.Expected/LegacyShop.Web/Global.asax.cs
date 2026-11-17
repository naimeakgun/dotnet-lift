using System;
using System.Web;
using System.Web.Http;
using System.Web.Mvc;
using System.Web.Optimization;
using System.Web.Routing;

namespace LegacyShop.Web
{
    public class MvcApplication : HttpApplication
    {
        protected void Application_Start()
        {
            AreaRegistration.RegisterAllAreas();
            GlobalConfiguration.Configure(WebApiConfig.Register);
            FilterConfig.RegisterGlobalFilters(GlobalFilters.Filters);
            RouteConfig.RegisterRoutes(RouteTable.Routes);
            BundleConfig.RegisterBundles(BundleTable.Bundles);
            // dotnet-lift: libraries read the signed-in user through CurrentUser instead of HttpContext.
            LegacyShop.Data.Infrastructure.CurrentUser.Use(() => HttpContext.Current?.User);
        }

        protected void Application_Error(object sender, EventArgs e)
        {
            var ex = Server.GetLastError();
            System.Diagnostics.Trace.TraceError(ex != null ? ex.ToString() : "Unknown error");
        }
    }
}
