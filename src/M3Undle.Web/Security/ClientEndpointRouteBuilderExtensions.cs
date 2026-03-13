namespace M3Undle.Web.Security;

public static class ClientEndpointRouteBuilderExtensions
{
    public static RouteGroupBuilder MapClientSurface(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(string.Empty);
        group.AddEndpointFilter<ClientEndpointAccessFilter>();
        return group;
    }
}
