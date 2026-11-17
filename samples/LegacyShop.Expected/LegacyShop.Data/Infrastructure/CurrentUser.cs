using System;
using System.Security.Principal;

namespace LegacyShop.Data.Infrastructure
{
    /// <summary>
    /// The signed-in user, supplied by the host application. Added by dotnet-lift to replace HttpContext.Current.User,
    /// so this library no longer depends on System.Web.
    /// ASP.NET (System.Web), in Application_Start:  CurrentUser.Use(() => HttpContext.Current?.User);
    /// ASP.NET Core, in Program.cs:  CurrentUser.Use(() => app.Services.GetRequiredService&lt;IHttpContextAccessor&gt;().HttpContext?.User);
    /// </summary>
    public static class CurrentUser
    {
        private static Func<IPrincipal> _provider = () => null;

        /// <summary>Sets where the current user comes from. Call once at startup.</summary>
        public static void Use(Func<IPrincipal> provider)
        {
            _provider = provider ?? (() => null);
        }

        /// <summary>The current user, or null when there is none (no request, not signed in).</summary>
        public static IPrincipal Principal
        {
            get { return _provider(); }
        }
    }
}
