using M3Undle.Core.M3u;
using M3Undle.Web.Api;
using M3Undle.Web.Application;
using M3Undle.Web.Components;
using M3Undle.Web.Components.Account;
using M3Undle.Web.Data;
using M3Undle.Web.Logging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MudBlazor.Services;
using Serilog;
using Serilog.Formatting.Compact;
using System.Data.Common;

// Static web assets initialization expects the default web root directory to exist.
// Ensure it's present so startup doesn't fail in environments/checkouts where it is missing.
Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"));

var builder = WebApplication.CreateBuilder(args);

// Register logging infrastructure before AddSerilog so the broadcast sink can be injected
builder.Services.AddSingleton<InMemoryLogStore>();
builder.Services.AddSingleton<LogBroadcastSink>();

builder.Services.AddSerilog((services, lc) =>
{
    lc.ReadFrom.Configuration(builder.Configuration)
      .ReadFrom.Services(services);

    var loggingCfg = builder.Configuration.GetSection("M3Undle:Logging");
    var logDir = loggingCfg["LogDirectory"] ?? "Data/logs";
    if (!Path.IsPathRooted(logDir))
        logDir = Path.Combine(builder.Environment.ContentRootPath, logDir);
    Directory.CreateDirectory(logDir);

    var filePath = Path.Combine(logDir, "app-.log");
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
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
var sqliteInterceptor = new SqliteConnectionInterceptor();
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString).AddInterceptors(sqliteInterceptor));
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
builder.Services.AddScoped<ConfigYamlService>();

// Named HttpClient for stream relay — no body timeout (live streams run indefinitely)
builder.Services.AddHttpClient("stream-relay", client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});

builder.Services.Configure<RefreshOptions>(builder.Configuration.GetSection("M3Undle:Refresh"));
builder.Services.Configure<SnapshotOptions>(builder.Configuration.GetSection("M3Undle:Snapshot"));
builder.Services.AddSingleton<AppEventBus>();
builder.Services.AddSingleton<ProviderFetcher>();
builder.Services.AddScoped<SnapshotBuilder>();
builder.Services.AddSingleton<ISiteSettingsService, SiteSettingsService>();
builder.Services.AddSingleton<SnapshotRefreshService>();
builder.Services.AddSingleton<IRefreshTrigger>(sp => sp.GetRequiredService<SnapshotRefreshService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<SnapshotRefreshService>());

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = true;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();
builder.Services.AddMudServices();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.Migrate();
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
}

// Configure the HTTP request pipeline.
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

app.UseStaticFiles();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();
app.MapProviderApiEndpoints();
app.MapChannelFilterApiEndpoints();
app.MapCompatibilityEndpoints();
app.MapHealthChecks("/health");

app.Run();

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

