namespace M3Undle.Web.Application.Notifications.Providers;

public static class NotificationProviderRegistration
{
    /// <summary>
    /// Registers the shipped providers and their destination adapters. Adding a provider means adding one line to each list
    /// (plus its own editor); routing, incidents and the worker are untouched.
    /// </summary>
    public static IServiceCollection AddNotificationProviders(this IServiceCollection services)
    {
        services.AddSingleton<INotificationProvider, SmtpNotificationProvider>();
        services.AddSingleton<INotificationProvider, MatrixNotificationProvider>();
        services.AddSingleton<INotificationDestinationAdapter, MatrixDestinationAdapter>();
        services.AddSingleton<INotificationDestinationAdapter, SmtpDestinationAdapter>();
        services.AddMatrixHttpClient();
        return services;
    }

    // Redirects are never followed: Matrix requests carry a bearer token that must not travel to a redirect target. Internal
    // self-hosted homeservers stay reachable because the destination is an administrator-saved setting, not request input.
    public static IHttpClientBuilder AddMatrixHttpClient(this IServiceCollection services) =>
        services.AddHttpClient(MatrixNotificationProvider.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(15),
            })
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30));
}
