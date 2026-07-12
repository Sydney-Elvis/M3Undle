namespace M3Undle.Web.Security;

// Endpoint redirects must stay app-local: redirect targets are built from
// request-derived values (Xtream path credentials, stream keys), and a raw
// Response.Redirect gives an attacker-influenced Location header. LocalRedirect
// rejects any URL that is not app-local, so use this instead of
// HttpContext.Response.Redirect in endpoint handlers.
internal static class LocalRedirectExtensions
{
    public static Task RedirectLocalAsync(this HttpContext context, string localUrl)
        => TypedResults.LocalRedirect(localUrl).ExecuteAsync(context);
}
