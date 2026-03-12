using M3Undle.Core.M3u;
using Microsoft.AspNetCore.Diagnostics;
using M3Undle.Web.Api;
using M3Undle.Web.Application;
using M3Undle.Web.Components;
using M3Undle.Web.Components.Account;
using M3Undle.Web.Data;
using M3Undle.Web.Logging;
using M3Undle.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MudBlazor.Services;
using Serilog;
using Serilog.Formatting.Compact;
using System.Data.Common;

// Static web assets initialization runs during CreateBuilder and can fail before the app
// has a chance to configure a custom web root. Create the likely content-root candidates
// up front so startup works from the project folder, solution folder, and bin output.
EnsureWebRootExists();

var builder = WebApplication.CreateBuilder(args);
var runtimePaths = RuntimePaths.Resolve(builder.Configuration, builder.Environment);

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
    options.UseSqlite(runtimePaths.DatabaseConnectionString).AddInterceptors(sqliteInterceptor));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddValidation();
builder.Services.AddHttpClient();
builder.Services.AddScoped(sp =>
{
    var navigation = sp.GetRequiredService<NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(navigation.BaseUri), Timeout = TimeSpan.FromMinutes(10) };
});
builder.Services.AddSingleton<PlaylistParser>();
builder.Services.AddSingleton<EnvironmentVariableService>();
builder.Services.AddSingleton<SecretEncryptionService>();
builder.Services.AddScoped<ConfigYamlService>();

// Named HttpClient for stream relay — no body timeout (live streams run indefinitely)
builder.Services.AddHttpClient("stream-relay", client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});

builder.Services.Configure<RefreshOptions>(builder.Configuration.GetSection("M3Undle:Refresh"));
builder.Services.Configure<SnapshotOptions>(builder.Configuration.GetSection("M3Undle:Snapshot"));
builder.Services.Configure<HdHomeRunOptions>(builder.Configuration.GetSection("M3Undle:HdHomeRun"));
builder.Services.Configure<ClientEndpointAccessOptions>(builder.Configuration.GetSection("M3Undle:EndpointAccess"));
builder.Services.PostConfigure<SnapshotOptions>(options =>
{
    options.Directory = RuntimePaths.ResolveDirectory(
        configuredPath: options.Directory,
        dataDirectory: runtimePaths.DataDirectory,
        defaultRelativePath: "snapshots");
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost |
        ForwardedHeaders.XForwardedPrefix;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddSingleton(runtimePaths);
builder.Services.AddSingleton<AppEventBus>();
builder.Services.AddSingleton<ProviderFetcher>();
builder.Services.AddScoped<SnapshotBuilder>();
builder.Services.AddScoped<HdHomeRunLineupService>();
builder.Services.AddScoped<ILineupRenderer, ActiveSnapshotLineupRenderer>();
builder.Services.AddSingleton<IM3USerializer, M3uSerializer>();
builder.Services.AddSingleton<IXmlTvSerializer, XmlTvSerializer>();
builder.Services.AddSingleton<HdHomeRunDeviceService>();
builder.Services.AddHostedService<HdHomeRunDiscoveryService>();
builder.Services.AddSingleton<ISiteSettingsService, SiteSettingsService>();
builder.Services.AddScoped<IEndpointSecurityService, EndpointSecurityService>();
builder.Services.AddScoped<ICredentialValidator, DbCredentialValidator>();
builder.Services.AddScoped<IProfileResolver, ActiveProfileResolver>();
builder.Services.AddScoped<IAccessResolver, ClientEndpointAccessResolver>();
builder.Services.AddScoped<ClientEndpointAccessFilter>();
builder.Services.AddScoped<ProviderPageService>();
builder.Services.AddScoped<ChannelMappingPageService>();
builder.Services.AddScoped<ChannelListPageService>();
builder.Services.AddSingleton<ChannelStatsService>();
builder.Services.AddSingleton<SnapshotRefreshService>();
builder.Services.AddSingleton<IRefreshTrigger>(sp => sp.GetRequiredService<SnapshotRefreshService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<SnapshotRefreshService>());

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredLength = 6;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.Configure<PasswordHasherOptions>(options =>
    options.IterationCount = builder.Configuration.GetValue("Identity:PasswordHasherIterationCount", 100_000));

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();
builder.Services.AddMudServices();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.Migrate();
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
}

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
    if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
    {
        var feature = ctx.Features.Get<IStatusCodePagesFeature>();
        if (feature is not null) feature.Enabled = false;
    }
    await next(ctx);
});

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
app.MapChannelListApiEndpoints();
app.MapSiteSettingsApiEndpoints();
app.MapHdHomeRunEndpoints();
app.MapCompatibilityEndpoints();
app.MapHealthChecks("/health");

app.Run();

static async Task SeedAdminAccountIfNeededAsync(IServiceProvider services)
{
    var env = services.GetRequiredService<EnvironmentVariableService>();
    var authEnabled = string.Equals(env.GetValue("M3UNDLE_AUTH_ENABLED")?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    if (!authEnabled) return;

    await using var scope = services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    if (await db.Users.AsNoTracking().AnyAsync()) return;

    var adminUser = env.GetValue("M3UNDLE_ADMIN_USER")?.Trim() ?? "admin";
    var adminPassword = env.GetValue("M3UNDLE_ADMIN_PASSWORD")?.Trim()
        ?? throw new InvalidOperationException(
            "M3UNDLE_ADMIN_PASSWORD must be set when M3UNDLE_AUTH_ENABLED=true and no admin account exists.");

    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var user = new ApplicationUser { UserName = adminUser, Email = adminUser, EmailConfirmed = true };
    var result = await userManager.CreateAsync(user, adminPassword);
    if (!result.Succeeded)
        throw new InvalidOperationException(
            $"Failed to create admin account: {string.Join(", ", result.Errors.Select(e => e.Description))}");

    services.GetRequiredService<ILogger<Program>>()
        .LogInformation("Admin account created from environment variables.");
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
        cmd.CommandText = "PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
    }
}

