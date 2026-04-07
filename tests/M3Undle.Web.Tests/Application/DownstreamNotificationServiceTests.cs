using M3Undle.Web.Application;
using M3Undle.Web.Application.Downstream;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Application;

[TestClass]
public sealed class DownstreamNotificationServiceTests
{
    [TestMethod]
    public async Task RefreshCompleted_FirstRun_NotifiesGuideOnlyIntegration()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "int-1",
                Name = "Guide Only",
                Kind = "webhook",
                BaseUrl = "http://localhost/hook",
                TriggerOnLineupUpdate = false,
                TriggerOnGuideUpdate = true,
                Enabled = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        var eventBus = new AppEventBus();
        var adapter = new RecordingAdapter("webhook");
        var envVars = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        var encryption = new SecretEncryptionService(envVars);
        using var service = new DownstreamNotificationService(
            eventBus,
            fixture.ScopeFactory,
            [adapter],
            encryption,
            NullLogger<DownstreamNotificationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        eventBus.Publish(AppEventKind.RefreshCompleted, succeeded: true, changeClass: null);
        await adapter.WaitForCountAsync(1, CancellationToken.None);

        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(1, adapter.CallCount);
    }

    private static async Task<TestFixture> CreateFixtureAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(connection));
        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        return new TestFixture(connection, provider);
    }

    private sealed class TestFixture(SqliteConnection connection, ServiceProvider provider) : IAsyncDisposable
    {
        public IServiceScopeFactory ScopeFactory => provider.GetRequiredService<IServiceScopeFactory>();

        public ApplicationDbContext CreateDbContext()
        {
            var scope = provider.CreateScope();
            return scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        }

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class RecordingAdapter(string kind) : IDownstreamAdapter
    {
        private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Kind => kind;
        public int CallCount { get; private set; }

        public Task<string?> NotifyAsync(string baseUrl, string? apiKey, string? webhookHeadersJson, DownstreamTrigger trigger, CancellationToken ct)
        {
            CallCount++;
            _called.TrySetResult();
            return Task.FromResult<string?>(null);
        }

        public async Task WaitForCountAsync(int expected, CancellationToken ct)
        {
            while (CallCount < expected)
            {
                await _called.Task.WaitAsync(ct);
                if (CallCount >= expected)
                    return;
            }
        }
    }
}
