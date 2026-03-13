using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace M3Undle.Web.Security;

public static class ClientAccessHttpContextExtensions
{
    private static readonly object ItemKey = new();

    internal static void SetResolvedClientAccess(this HttpContext context, ResolvedClientAccess access)
        => context.Items[ItemKey] = access;

    public static ResolvedClientAccess GetResolvedClientAccess(this HttpContext context)
    {
        if (context.Items.TryGetValue(ItemKey, out var value) && value is ResolvedClientAccess access)
            return access;

        throw new InvalidOperationException("Resolved client access context was not found on this request.");
    }

    public static string ApplyClientAccessQuery(this string url, HttpContext context)
    {
        if (!context.Items.TryGetValue(ItemKey, out var value) || value is not ResolvedClientAccess access)
            return url;

        var queryCredential = access.UrlCredential;
        if (queryCredential is null)
            return url;

        return QueryHelpers.AddQueryString(url, new Dictionary<string, string?>
        {
            ["username"] = queryCredential.Username,
            ["password"] = queryCredential.Password,
        });
    }
}
