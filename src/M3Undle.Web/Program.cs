using M3Undle.Core;
using M3Undle.Core.M3u;
using Microsoft.AspNetCore.Diagnostics;
using M3Undle.Web.Api;
using M3Undle.Web.Application;
using M3Undle.Web.Application.Downstream;
using M3Undle.Web.Components;
using M3Undle.Web.Components.Account;
using M3Undle.Web.Data;
using M3Undle.Web.Logging;
using M3Undle.Web.Observability;
using M3Undle.Web.Security;
using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.GeneratedHls;
using M3Undle.Web.Streaming.Observability;
using M3Undle.Web.Streaming.Resolution;
using M3Undle.Web.Streaming.Sessions;
using M3Undle.Web.Streaming.Compatibility;
using M3Undle.Web.Streaming.Upstream;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using OpenTelemetry.Metrics;
using Serilog;
using Serilog.Formatting.Compact;
using System.Data.Common;
using System.Threading.RateLimiting;

// Static web assets initialization runs during CreateBuilder and can fail before the app
// has a chance to configure a custom web root. Create the likely content-root candidates
// up front so startup works from the project folder, solution folder, and bin output.
EnsureWebRootExists();

var builder = WebApplication.CreateBuilder(args);
var runtimePaths = RuntimePaths.Resolve(builder.Configuration, builder.Environment);
const string MediaSurfaceCorsPolicy = "MediaSurfaceCors";
const string ApplicationSurfaceCorsPolicy = "ApplicationSurfaceCors";

if (Path.GetDirectoryName(runtimePaths.DatabasePath) is { Length: > 0 } dbDir)
    Directory.CreateDirectory(dbDir);

// Register logging infrastructure before AddSerilog so the broadcast sink can be injected
builder.Services.AddSingleton<InMemoryLogStore>();
builder.Services.AddSingleton<LogBroadcastSink>();

builder.Services.AddSerilog((services, lc) =>
{
    lc.ReadFrom.Configuration(builder.Configuration)
      .ReadFrom.Services(services);

    var loggingCfg = builder.Configuration.GetSection("M3Undle:Logging");
    var filePath = Path.Combine(runtimePaths.LogDirectory, "app-.log");
    var sizeLimit = loggingCfg.GetValue<long?>("FileSizeLimitBytes") ?? 10_485_760L;
    var retainCount = loggingCfg.GetValue<int?>("RetainedFileCount") ?? 31;
    var rollOnSize = loggingCfg.GetValue<bool?>("RollOnFileSizeLimit") ?? true;
    var useJson = loggingCfg.GetValue<bool?>("UseJsonFormat") ?? false;
    var template = loggingCfg["OutputTemplate"]
        ?? "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] [{EventType,-12}] {Message:lj}{NewLine}{Exception}";

    if (useJson)
        lc.WriteTo.File(new CompactJsonFormatter(), filePath,
            fileSizeLimitBytes: sizeLimit, retainedFileCountLimit: retainCount,
            rollOnFileSizeLimit: rollOnSize, rollingInterval: RollingInterval.Day);
    else
        lc.WriteTo.File(filePath, outputTemplate: template,
            fileSizeLimitBytes: sizeLimit, retainedFileCountLimit: retainCount,
            rollOnFileSizeLimit: rollOnSize, rollingInterval: RollingInterval.Day);

    lc.WriteTo.Sink(services.GetRequiredService<LogBroadcastSink>());
});

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options =>
    {
        options.ClientTimeoutInterval = TimeSpan.FromMinutes(5);
        options.KeepAliveInterval = TimeSpan.FromSeconds(30);
    });

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddAuthorization(options =>
    options.AddPolicy(UiAccessPolicy.Name, policy => policy.Requirements.Add(new UiAccessRequirement())));
builder.Services.AddSingleton<IAuthorizationHandler, UiAccessHandler>();
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context => HandleApiAuthRedirectAsync(context, StatusCodes.Status401Unauthorized);
    options.Events.OnRedirectToAccessDenied = context => HandleApiAuthRedirectAsync(context, StatusCodes.Status403Forbidden);
});

var sqliteInterceptor = new SqliteConnectionInterceptor();
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options
        .UseSqlite(runtimePaths.DatabaseConnectionString)
        .AddInterceptors(sqliteInterceptor)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.FirstWithoutOrderByAndFilterWarning)));
if (builder.Environment.IsDevelopment())
    builder.Services.AddDatabaseDeveloperPageExceptionFilter();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks()
    .AddCheck<M3UndleReadinessHealthCheck>("m3undle_ready", tags: ["ready"]);
builder.Services.AddM3UndleOpenApi();
builder.Services.AddValidation();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient(string.Empty, client =>
{
    // No HttpClient-level timeout — per-provider CancellationTokenSource controls the deadline.
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddCors(options =>
{
    options.AddPolicy(MediaSurfaceCorsPolicy, policy =>
        policy.AllowAnyOrigin()
            .WithMethods(HttpMethods.Get));

    options.AddPolicy(ApplicationSurfaceCorsPolicy, policy =>
        policy.SetIsOriginAllowed(origin => IsConfiguredApplicationOrigin(origin, builder.Configuration))
            .AllowAnyHeader()
            .AllowAnyMethod());
});
builder.Services.AddScoped(sp =>
{
    var navigation = sp.GetRequiredService<NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(navigation.BaseUri), Timeout = TimeSpan.FromMinutes(10) };
});
builder.Services.AddSingleton<PlaylistParser>();
builder.Services.AddSingleton<EnvironmentVariableService>();
builder.Services.AddSingleton<EndpointUrlService>();
builder.Services.AddSingleton<SecretEncryptionService>();
builder.Services.AddScoped<ConfigYamlService>();

// Named HttpClient for stream relay — no body timeout (live streams run indefinitely)
builder.Services.AddHttpClient("stream-relay", client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});

// Named HttpClient for EPG fetching — automatic gzip/deflate/brotli decompression
builder.Services.AddHttpClient("epg", client =>
{
    client.DefaultRequestHeaders.AcceptEncoding.TryParseAdd("gzip, deflate, br");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression = System.Net.DecompressionMethods.All,
});

// Named HttpClient for downstream notifications (Jellyfin, Emby, webhooks)
builder.Services.AddHttpClient("downstream", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.Configure<RefreshOptions>(builder.Configuration.GetSection("M3Undle:Refresh"));
builder.Services.Configure<SnapshotOptions>(builder.Configuration.GetSection("M3Undle:Snapshot"));
builder.Services.Configure<AdaptiveLockoutOptions>(builder.Configuration.GetSection("Identity:AdaptiveLockout"));
builder.Services.Configure<HdHomeRunOptions>(builder.Configuration.GetSection("M3Undle:HdHomeRun"));
builder.Services.Configure<ClientEndpointAccessOptions>(builder.Configuration.GetSection("M3Undle:EndpointAccess"));
builder.Services.Configure<StreamProxyOptions>(builder.Configuration.GetSection("M3Undle:Streaming"));
builder.Services.Configure<BufferOptions>(builder.Configuration.GetSection("M3Undle:Streaming:Buffer"));
builder.Services.Configure<ReconnectOptions>(builder.Configuration.GetSection("M3Undle:Streaming:Reconnect"));
builder.Services.Configure<GeneratedHlsOptions>(builder.Configuration.GetSection("M3Undle:Streaming:GeneratedHls"));
builder.Services.Configure<CleanRelayOptions>(builder.Configuration.GetSection("M3Undle:Streaming:CleanRelay"));
builder.Services.Configure<ObservabilityOptions>(builder.Configuration.GetSection("M3Undle:Observability"));
var metricsScrapePath = NormalizeObservabilityPath(builder.Configuration["M3Undle:Observability:Metrics:Path"], "/metrics");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = static async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);

        context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
        await context.HttpContext.Response.WriteAsync("Rate limit exceeded.", cancellationToken);
    };

    options.GlobalLimiter = PartitionedRateLimiter.CreateChained<HttpContext>(
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var group = GetObservabilityRateLimitGroup(context.Request.Path, metricsScrapePath);
            if (group is null)
                return RateLimitPartition.GetNoLimiter("observability-unlimited-concurrency");

            var key = $"{group}:{GetRateLimitClientKey(context)}";
            return RateLimitPartition.GetConcurrencyLimiter(key, _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = group switch
                {
                    "metrics" => 1,
                    "diagnostics" => 2,
                    _ => 4,
                },
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });
        }),
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var group = GetObservabilityRateLimitGroup(context.Request.Path, metricsScrapePath);
            if (group is null)
                return RateLimitPartition.GetNoLimiter("observability-unlimited-window");

            var key = $"{group}:{GetRateLimitClientKey(context)}";
            return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = group switch
                {
                    "metrics" => 12,
                    "diagnostics" => 30,
                    _ => 60,
                },
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            });
        }));
});
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter(M3UndleMetrics.MeterName)
        .AddPrometheusExporter());
builder.Services.AddSingleton<IConfigureOptions<StreamProxyOptions>, StreamProxyDbOptionsConfigurator>();
builder.Services.AddSingleton<IConfigureOptions<BufferOptions>, BufferDbOptionsConfigurator>();
builder.Services.AddSingleton<IConfigureOptions<ReconnectOptions>, ReconnectDbOptionsConfigurator>();
builder.Services.AddSingleton<IConfigureOptions<GeneratedHlsOptions>, GeneratedHlsDbOptionsConfigurator>();
builder.Services.AddSingleton<IValidateOptions<StreamProxyOptions>, StreamProxyOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<BufferOptions>, BufferOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<ReconnectOptions>, ReconnectOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<GeneratedHlsOptions>, GeneratedHlsOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<CleanRelayOptions>, CleanRelayOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<ObservabilityOptions>, ObservabilityOptionsValidator>();
builder.Services.AddOptions<StreamProxyOptions>().ValidateOnStart();
builder.Services.AddOptions<BufferOptions>().ValidateOnStart();
builder.Services.AddOptions<ReconnectOptions>().ValidateOnStart();
builder.Services.AddOptions<GeneratedHlsOptions>().ValidateOnStart();
builder.Services.AddOptions<CleanRelayOptions>().ValidateOnStart();
builder.Services.AddOptions<ObservabilityOptions>().ValidateOnStart();
builder.Services.PostConfigure<SnapshotOptions>(options =>
{
    options.Directory = RuntimePaths.ResolveDirectory(
        configuredPath: options.Directory,
        dataDirectory: runtimePaths.DataDirectory,
        defaultRelativePath: "snapshots");
});
builder.Services.PostConfigure<GeneratedHlsOptions>(options =>
{
    options.Directory = RuntimePaths.ResolveDirectory(
        configuredPath: options.Directory,
        dataDirectory: runtimePaths.DataDirectory,
        defaultRelativePath: "hls-work");
});
builder.Services.Configure<ReverseProxyOptions>(builder.Configuration.GetSection("M3Undle:ReverseProxy"));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost |
        ForwardedHeaders.XForwardedPrefix;

    var proxyConfig = builder.Configuration.GetSection("M3Undle:ReverseProxy").Get<ReverseProxyOptions>()
        ?? new ReverseProxyOptions();

    options.ForwardLimit = proxyConfig.ForwardLimit;

    foreach (var ip in proxyConfig.TrustedProxies)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var address))
            throw new InvalidOperationException(
                $"M3Undle:ReverseProxy:TrustedProxies contains an invalid IP address: '{ip}'.");
        options.KnownProxies.Add(address);
    }

    foreach (var cidr in proxyConfig.TrustedNetworks)
    {
        if (!System.Net.IPNetwork.TryParse(cidr, out var network))
            throw new InvalidOperationException(
                $"M3Undle:ReverseProxy:TrustedNetworks contains an invalid CIDR network: '{cidr}'.");
        options.KnownIPNetworks.Add(network);
    }
});
builder.Services.AddSingleton(runtimePaths);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(AppBuildInfo.ForEntryAssembly());
builder.Services.AddSingleton<AppEventBus>();
builder.Services.AddSingleton<IEventService, EventService>();
builder.Services.AddSingleton<RefreshActivityTracker>();
builder.Services.AddSingleton<XtreamSeriesExpansionService>();
builder.Services.AddSingleton<IXtreamSeriesExpansionQueue>(sp => sp.GetRequiredService<XtreamSeriesExpansionService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<XtreamSeriesExpansionService>());
builder.Services.AddSingleton<XtreamLineupClient>();
builder.Services.AddSingleton<ProviderFetcher>();
builder.Services.AddSingleton<M3Undle.Core.Epg.XmltvParser>();
builder.Services.AddSingleton<M3Undle.Web.Application.Epg.EpgSourceFetcher>();
builder.Services.AddScoped<M3Undle.Web.Application.Epg.EpgChannelMapper>();
builder.Services.AddSingleton<M3Undle.Web.Application.Epg.EpgCompiler>();
builder.Services.AddScoped<SnapshotBuilder>();
builder.Services.AddScoped<HdHomeRunLineupService>();
builder.Services.AddScoped<ILineupRenderer, ActiveSnapshotLineupRenderer>();
builder.Services.AddSingleton<IM3USerializer, M3uSerializer>();
builder.Services.AddSingleton<IXmlTvSerializer, XmlTvSerializer>();
builder.Services.AddSingleton<HdHomeRunTunerCountResolver>();
builder.Services.AddSingleton<HdHomeRunDeviceService>();
builder.Services.AddHostedService<HdHomeRunDiscoveryService>();
builder.Services.AddSingleton<ISiteSettingsService, SiteSettingsService>();
builder.Services.AddScoped<IAdaptiveLockoutService, AdaptiveLockoutService>();
builder.Services.AddScoped<IEndpointSecurityService, EndpointSecurityService>();
builder.Services.AddScoped<IStreamingSettingsService, StreamingSettingsService>();
builder.Services.AddSingleton<IHdHomeRunSettingsService, HdHomeRunSettingsService>();
builder.Services.AddScoped<IGeneratedHlsSettingsService, GeneratedHlsSettingsService>();
builder.Services.AddScoped<IRefreshScheduleService, RefreshScheduleService>();
builder.Services.AddScoped<IDownstreamIntegrationService, DownstreamIntegrationService>();
builder.Services.AddSingleton<IDownstreamAdapter, JellyfinAdapter>();
builder.Services.AddSingleton<IDownstreamAdapter, EmbyAdapter>();
builder.Services.AddSingleton<IDownstreamAdapter, WebhookAdapter>();
builder.Services.AddSingleton<IApplicationRestartService, ApplicationRestartService>();
builder.Services.AddScoped<ICredentialValidator, DbCredentialValidator>();
builder.Services.AddScoped<IProfileResolver, ActiveProfileResolver>();
builder.Services.AddScoped<IAccessResolver, ClientEndpointAccessResolver>();
builder.Services.AddScoped<ClientEndpointAccessFilter>();
builder.Services.AddScoped<XtreamPathCredentialFilter>();
builder.Services.AddSingleton<XtreamStreamIdCache>();
builder.Services.AddScoped<ProviderPageService>();
builder.Services.AddScoped<ChannelMappingPageService>();
builder.Services.AddScoped<CustomGroupPageService>();
builder.Services.AddScoped<ChannelListPageService>();
builder.Services.AddScoped<EpgPageService>();
builder.Services.AddSingleton<ChannelStatsService>();
builder.Services.AddSingleton<DashboardStatsService>();
builder.Services.AddSingleton<LineupStatusService>();
builder.Services.AddSingleton<ProfilesPageService>();
builder.Services.AddSingleton<SnapshotRefreshService>();
builder.Services.AddSingleton<IRefreshTrigger>(sp => sp.GetRequiredService<SnapshotRefreshService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<SnapshotRefreshService>());
builder.Services.AddHostedService<DownstreamNotificationService>();
builder.Services.AddSingleton<HdHomeRunTunerManager>();
builder.Services.AddSingleton<StreamingRegistry>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StreamingRegistry>());
builder.Services.AddSingleton<StreamingDiagnosticsStore>();
builder.Services.AddSingleton<StreamChannelHealthEventRecorder>();
builder.Services.AddSingleton<IStreamChannelHealthEventRecorder>(sp => sp.GetRequiredService<StreamChannelHealthEventRecorder>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<StreamChannelHealthEventRecorder>());
builder.Services.AddSingleton<IStreamChannelHealthProfileService, StreamChannelHealthProfileService>();
builder.Services.AddSingleton<InternalRelaySecretService>();
builder.Services.AddSingleton<UpstreamFailureStrikeStore>();
builder.Services.AddSingleton<StreamAdmissionBackoffStore>();
builder.Services.AddSingleton<UpstreamStreamConnector>();
builder.Services.AddSingleton<GeneratedHlsSessionManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GeneratedHlsSessionManager>());
builder.Services.AddSingleton<ChannelSessionManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChannelSessionManager>());
builder.Services.AddScoped<StreamRequestResolver>();
builder.Services.AddSingleton<HlsManifestRewriter>();
builder.Services.AddSingleton<HlsProxyService>();
builder.Services.AddScoped<IObservabilitySettingsService, ObservabilitySettingsService>();
builder.Services.AddScoped<IMetricsTokenService, MetricsTokenService>();
builder.Services.AddSingleton<M3UndleMetrics>();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredLength = 6;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = builder.Configuration.GetValue("Identity:Lockout:MaxFailedAccessAttempts", 5);
        options.Lockout.DefaultLockoutTimeSpan = builder.Configuration.GetValue("Identity:Lockout:DefaultLockoutTimeSpan", TimeSpan.FromMinutes(5));
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.Configure<PasswordHasherOptions>(options =>
    options.IterationCount = builder.Configuration.GetValue("Identity:PasswordHasherIterationCount", 100_000));

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();
builder.Services.AddMudServices();

var app = builder.Build();
var buildInfo = app.Services.GetRequiredService<AppBuildInfo>();

app.Logger.LogInformation(
    "Starting M3Undle {Version} (BuildDateUtc={BuildDateUtc}, BuildNumber={BuildNumber})",
    buildInfo.Version,
    buildInfo.BuildDateUtc ?? "unknown",
    buildInfo.BuildNumber ?? "n/a");

List<string> appliedMigrations;
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    appliedMigrations = db.Database.GetPendingMigrations().ToList();
    db.Database.Migrate();
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    db.Database.ExecuteSqlRaw(
        "CREATE TABLE IF NOT EXISTS xtream_series_cache (" +
        "provider_id TEXT NOT NULL, " +
        "series_id INTEGER NOT NULL, " +
        "last_modified_epoch INTEGER NOT NULL, " +
        "episodes_json TEXT NOT NULL, " +
        "CONSTRAINT PK_xtream_series_cache PRIMARY KEY (provider_id, series_id), " +
        "CONSTRAINT FK_xtream_series_cache_providers_provider_id FOREIGN KEY (provider_id) REFERENCES providers (provider_id) ON DELETE CASCADE);"
    );

    var streamingSettings = scope.ServiceProvider.GetRequiredService<IStreamingSettingsService>();
    await streamingSettings.ClearRestartRequiredAsync();
}

_ = app.Services.GetRequiredService<M3UndleMetrics>();

await PublishStartupEventsAsync(app, buildInfo, appliedMigrations);

await SeedAdminAccountIfNeededAsync(app.Services);

// Configure the HTTP request pipeline.
app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
    // Only redirect to HTTPS if running with HTTPS support (not in pure HTTP containers)
    if (app.Configuration.GetValue<string>("ASPNETCORE_HTTPS_PORTS") is not null)
        app.UseHttpsRedirection();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
// Don't redirect API errors through Blazor's /not-found page — preserve the real status code.
app.Use(static async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        || IsClientDeliveryPath(ctx.Request.Path))
    {
        var feature = ctx.Features.Get<IStatusCodePagesFeature>();
        if (feature is not null) feature.Enabled = false;
    }
    await next(ctx);
});

app.UseRouting();
app.UseRateLimiter();
app.UseMiddleware<MetricsEndpointSecurityMiddleware>();
app.UseOpenTelemetryPrometheusScrapingEndpoint(context =>
{
    var metricsOptions = context.RequestServices.GetRequiredService<IOptions<ObservabilityOptions>>();
    return context.Request.Path.Equals(metricsOptions.Value.Metrics.NormalizedPath, StringComparison.OrdinalIgnoreCase);
});
app.UseMiddleware<HttpMetricsMiddleware>();
app.UseWhen(
    context => IsMediaSurfacePath(context.Request.Path),
    branch => branch.UseCors(MediaSurfaceCorsPolicy));
app.UseWhen(
    context => IsApplicationSurfacePath(context.Request.Path),
    branch => branch.UseCors(ApplicationSurfaceCorsPolicy));

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();
app.MapProviderApiEndpoints();
app.MapChannelFilterApiEndpoints();
app.MapCustomGroupApiEndpoints();
app.MapChannelListApiEndpoints();
app.MapSiteSettingsApiEndpoints();
app.MapHdHomeRunEndpoints();
app.MapCompatibilityEndpoints();
app.MapXtreamEndpoints();
app.MapEpgApiEndpoints();
app.MapDashboardApiEndpoints();
app.MapDownstreamApiEndpoints();
app.MapDiagnosticsApiEndpoints();
app.MapHealthChecks("/livez", new HealthCheckOptions
{
    Predicate = _ => false,
}).AllowAnonymous().ExcludeFromDescription();
app.MapHealthChecks("/readyz", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = M3UndleHealthResponseWriters.WriteReadinessAsync,
}).AllowAnonymous().ExcludeFromDescription();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = M3UndleHealthResponseWriters.WriteReadinessAsync,
}).AllowAnonymous().ExcludeFromDescription();
app.MapHealthChecks("/healthz", new HealthCheckOptions
{
    ResponseWriter = M3UndleHealthResponseWriters.WriteHealthSummaryAsync,
}).AllowAnonymous().ExcludeFromDescription();
app.MapHealthChecks("/health").ExcludeFromDescription();
app.MapM3UndleOpenApiEndpoints(app.Environment);

app.Run();

static async Task SeedAdminAccountIfNeededAsync(IServiceProvider services)
{
    var env = services.GetRequiredService<EnvironmentVariableService>();
    var authEnabled = IsFlagEnabled(env.GetValue("M3UNDLE_AUTH_ENABLED"));
    if (!authEnabled) return;

    await using var scope = services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var logger = services.GetRequiredService<ILogger<Program>>();
    var hasAnyUsers = await db.Users.AsNoTracking().AnyAsync();

    var adminUser = env.GetValue("M3UNDLE_ADMIN_USER")?.Trim() ?? "admin";
    var resetRequested = IsFlagEnabled(env.GetValue("M3UNDLE_ADMIN_PASSWORD_RESET"));

    if (!hasAnyUsers)
    {
        var adminPassword = env.GetValue("M3UNDLE_ADMIN_PASSWORD")?.Trim()
            ?? throw new InvalidOperationException(
                "M3UNDLE_ADMIN_PASSWORD must be set when M3UNDLE_AUTH_ENABLED=true and no admin account exists.");

        var user = new ApplicationUser { UserName = adminUser, Email = adminUser, EmailConfirmed = true };
        var createResult = await userManager.CreateAsync(user, adminPassword);
        if (!createResult.Succeeded)
            throw new InvalidOperationException(
                $"Failed to create admin account: {string.Join(", ", createResult.Errors.Select(e => e.Description))}");

        logger.LogInformation("Admin account created from environment variables.");
        return;
    }

    if (!resetRequested) return;

    var resetPassword = env.GetValue("M3UNDLE_ADMIN_PASSWORD")?.Trim()
        ?? throw new InvalidOperationException(
            "M3UNDLE_ADMIN_PASSWORD must be set when M3UNDLE_ADMIN_PASSWORD_RESET=true.");

    var existingAdmin = await userManager.FindByNameAsync(adminUser)
                        ?? await userManager.FindByEmailAsync(adminUser);
    if (existingAdmin is null)
        throw new InvalidOperationException(
            $"M3UNDLE_ADMIN_PASSWORD_RESET=true but user '{adminUser}' was not found.");

    var resetToken = await userManager.GeneratePasswordResetTokenAsync(existingAdmin);
    var resetResult = await userManager.ResetPasswordAsync(existingAdmin, resetToken, resetPassword);
    if (!resetResult.Succeeded)
        throw new InvalidOperationException(
            $"Failed to reset admin password: {string.Join(", ", resetResult.Errors.Select(e => e.Description))}");

    await userManager.ResetAccessFailedCountAsync(existingAdmin);
    await userManager.SetLockoutEndDateAsync(existingAdmin, null);
    if (existingAdmin.AdaptiveLockoutEscalated)
    {
        existingAdmin.AdaptiveLockoutEscalated = false;
        var updateResult = await userManager.UpdateAsync(existingAdmin);
        if (!updateResult.Succeeded)
            throw new InvalidOperationException(
                $"Admin password was reset but lockout state cleanup failed: {string.Join(", ", updateResult.Errors.Select(e => e.Description))}");
    }

    logger.LogWarning(
        "Admin password reset from environment variables for user '{AdminUser}'. " +
        "Set M3UNDLE_ADMIN_PASSWORD_RESET back to false after recovery.",
        adminUser);
}

static Task HandleApiAuthRedirectAsync(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
{
    if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = statusCode;
        return Task.CompletedTask;
    }

    context.Response.Redirect(context.RedirectUri);
    return Task.CompletedTask;
}

static bool IsClientDeliveryPath(PathString path)
    => IsMediaSurfacePath(path);

static bool IsFlagEnabled(string? value)
    => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

static string NormalizeObservabilityPath(string? path, string fallback)
{
    var candidate = string.IsNullOrWhiteSpace(path) ? fallback : path.Trim();
    return candidate.StartsWith('/') ? candidate : "/" + candidate;
}

static string? GetObservabilityRateLimitGroup(PathString path, string metricsPath)
{
    if (path.Equals(metricsPath, StringComparison.OrdinalIgnoreCase))
        return "metrics";

    if (path.StartsWithSegments("/api/admin/diagnostics", StringComparison.OrdinalIgnoreCase))
        return "diagnostics";

    if (path.Equals("/livez", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/readyz", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/health", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/health/ready", StringComparison.OrdinalIgnoreCase))
    {
        return "health";
    }

    return null;
}

static string GetRateLimitClientKey(HttpContext context)
    => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

static bool IsMediaSurfacePath(PathString path)
{
    return path.StartsWithSegments("/hdhr", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/tuner", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/tune", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/auto", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/stream", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/live", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/movie", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/vod", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/series", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/hls", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/m3u", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/xmltv", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/player_api.php", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/get.php", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/discover.json", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/lineup.json", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/lineup.xml", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/lineup.m3u", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/lineup_status.json", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/lineup.post", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/device.xml", StringComparison.OrdinalIgnoreCase);
}

static bool IsApplicationSurfacePath(PathString path)
    => !IsMediaSurfacePath(path);

static bool IsConfiguredApplicationOrigin(string origin, IConfiguration configuration)
{
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var requestOrigin))
        return false;

    var allowedOrigins = configuration
        .GetSection("M3Undle:Cors:ApplicationAllowedOrigins")
        .Get<string[]>() ?? [];

    var requestAuthority = requestOrigin.GetLeftPart(UriPartial.Authority);
    foreach (var candidateGroup in allowedOrigins)
    {
        var candidates = (candidateGroup ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var candidate in candidates)
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var configuredOrigin))
                continue;

            if (string.Equals(
                requestAuthority,
                configuredOrigin.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
    }

    return false;
}

static void EnsureWebRootExists()
{
    var candidateRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "wwwroot")),
    };

    var aspNetCoreContentRoot = Environment.GetEnvironmentVariable("ASPNETCORE_CONTENTROOT");
    if (!string.IsNullOrWhiteSpace(aspNetCoreContentRoot))
    {
        candidateRoots.Add(Path.Combine(aspNetCoreContentRoot, "wwwroot"));
    }

    foreach (var candidateRoot in candidateRoots)
    {
        try
        {
            Directory.CreateDirectory(candidateRoot);
        }
        catch
        {
            // Ignore non-fatal path issues here and let normal host startup surface
            // a real error if no usable web root can be established.
        }
    }
}

static async Task PublishStartupEventsAsync(WebApplication app, AppBuildInfo buildInfo, IReadOnlyCollection<string> appliedMigrations)
{
    try
    {
        var startupEvents = app.Services.GetRequiredService<IEventService>();
        await startupEvents.PublishAsync(
            SystemEventSeverity.Info,
            SystemEventTypes.AppRestarted,
            $"Application started (v{buildInfo.Version})");

        if (appliedMigrations.Count > 0)
        {
            await startupEvents.PublishAsync(
                SystemEventSeverity.Info,
                SystemEventTypes.DatabaseMigrationApplied,
                $"{appliedMigrations.Count} database migration(s) applied on startup",
                string.Join(", ", appliedMigrations.Select(m => m[(m.IndexOf('_') + 1)..])));
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Failed to publish startup system events.");
    }
}

sealed class SqliteConnectionInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => ApplyPragmas(connection);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ApplyPragmas(connection);
        return Task.CompletedTask;
    }

    private static void ApplyPragmas(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA busy_timeout=5000;
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA cache_size=-65536;
            PRAGMA temp_store=MEMORY;
            """;
        cmd.ExecuteNonQuery();
    }
}

public partial class Program;
