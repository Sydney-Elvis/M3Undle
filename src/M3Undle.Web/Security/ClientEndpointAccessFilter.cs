using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace M3Undle.Web.Security;

internal sealed class ClientEndpointAccessFilter(
    IAccessResolver accessResolver,
    IOptions<ClientEndpointAccessOptions> options)
    : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var resolved = await accessResolver.ResolveAsync(http, http.RequestAborted);

        if (!resolved.IsSuccess)
            return BuildFailureResult(http, resolved.FailureReason, options.Value.Realm);

        http.SetResolvedClientAccess(resolved.Access!);
        return await next(context);
    }

    private static IResult BuildFailureResult(HttpContext context, ClientAccessFailureReason reason, string realm)
    {
        if (reason is ClientAccessFailureReason.MissingCredentials or ClientAccessFailureReason.InvalidCredentials)
        {
            context.Response.Headers.WWWAuthenticate = $"Basic realm=\"{realm}\", charset=\"UTF-8\"";
            return TypedResults.Unauthorized();
        }

        if (reason == ClientAccessFailureReason.NoActiveProfile)
        {
            return TypedResults.Problem("No active profile is available for endpoint delivery.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return TypedResults.Problem("Endpoint access resolution failed.", statusCode: StatusCodes.Status500InternalServerError);
    }
}
