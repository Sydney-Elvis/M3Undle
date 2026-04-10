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

    [TestMethod]
    public async Task RefreshCompleted_NoMeaningfulChange_DoesNotNotify()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "int-none",
                Name = "No Change",
                Kind = "webhook",
                BaseUrl = "http://localhost/hook",
                TriggerOnLineupUpdate = true,
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

        eventBus.Publish(AppEventKind.RefreshCompleted, succeeded: true, changeClass: ChangeClasses.None);
        await Task.Delay(200);

        await service.StopAsync(CancellationToken.None);
        Assert.AreEqual(0, adapter.CallCount);
    }

    [TestMethod]
    public async Task RefreshCompleted_WhenAdapterReturnsError_PersistsLastNotifyError()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "int-error",
                Name = "Failure Adapter",
                Kind = "webhook",
                BaseUrl = "http://localhost/hook",
                TriggerOnLineupUpdate = true,
                TriggerOnGuideUpdate = true,
                Enabled = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        var eventBus = new AppEventBus();
        var adapter = new ErrorAdapter("webhook", "simulated downstream failure");
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

        eventBus.Publish(AppEventKind.RefreshCompleted, succeeded: true, changeClass: ChangeClasses.Lineup);
        await adapter.WaitForCountAsync(1, CancellationToken.None);
        await WaitUntilAsync(async () =>
        {
            await using var verify = fixture.CreateDbContext();
            var row = await verify.DownstreamIntegrations.SingleAsync(x => x.DownstreamIntegrationId == "int-error");
            return row.LastNotifyError == "simulated downstream failure";
        }, TimeSpan.FromSeconds(3));

        await service.StopAsync(CancellationToken.None);

        await using (var verify = fixture.CreateDbContext())
        {
            var row = await verify.DownstreamIntegrations.SingleAsync(x => x.DownstreamIntegrationId == "int-error");
            Assert.AreEqual("simulated downstream failure", row.LastNotifyError);
            Assert.IsNull(row.LastNotifiedUtc);
        }
    }

    [TestMethod]
    public async Task RefreshCompleted_WhenApiKeyDecryptFails_WritesDecryptErrorAndSkipsAdapter()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "int-decrypt",
                Name = "Decrypt Failure",
                Kind = "webhook",
                BaseUrl = "http://localhost/hook",
                ApiKeyEncrypted = "not-valid-base64",
                TriggerOnLineupUpdate = true,
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

        eventBus.Publish(AppEventKind.RefreshCompleted, succeeded: true, changeClass: ChangeClasses.Lineup);
        await WaitUntilAsync(async () =>
        {
            await using var verify = fixture.CreateDbContext();
            var row = await verify.DownstreamIntegrations.SingleAsync(x => x.DownstreamIntegrationId == "int-decrypt");
            return row.LastNotifyError is not null;
        }, TimeSpan.FromSeconds(3));

        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(0, adapter.CallCount);
        await using (var verify = fixture.CreateDbContext())
        {
            var row = await verify.DownstreamIntegrations.SingleAsync(x => x.DownstreamIntegrationId == "int-decrypt");
            Assert.IsNotNull(row.LastNotifyError);
            StringAssert.Contains(row.LastNotifyError, "Failed to decrypt API key:");
        }
    }

    [TestMethod]
    public async Task RefreshCompleted_NotifiesMatchingIntegrationsConcurrently()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "int-a",
                Name = "Concurrent A",
                Kind = "webhook",
                BaseUrl = "http://localhost/a",
                TriggerOnLineupUpdate = true,
                TriggerOnGuideUpdate = false,
                Enabled = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            setup.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "int-b",
                Name = "Concurrent B",
                Kind = "webhook",
                BaseUrl = "http://localhost/b",
                TriggerOnLineupUpdate = true,
                TriggerOnGuideUpdate = false,
                Enabled = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        var eventBus = new AppEventBus();
        var adapter = new ConcurrencyAdapter("webhook");
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

        eventBus.Publish(AppEventKind.RefreshCompleted, succeeded: true, changeClass: ChangeClasses.Lineup);
        await adapter.WaitForTwoStartedAsync(CancellationToken.None);
        adapter.ReleaseAll();
        await Task.Delay(150);

        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(2, adapter.CallCount);
        Assert.IsGreaterThanOrEqualTo(adapter.MaxInFlight, 2, $"Expected concurrent notifications, max in-flight was {adapter.MaxInFlight}.");
        await using (var verify = fixture.CreateDbContext())
        {
            var rows = await verify.DownstreamIntegrations
                .Where(x => x.DownstreamIntegrationId == "int-a" || x.DownstreamIntegrationId == "int-b")
                .ToListAsync();
            Assert.HasCount(2, rows);
            Assert.IsTrue(rows.All(x => x.LastNotifiedUtc.HasValue && x.LastNotifyError is null));
        }
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

    private sealed class ErrorAdapter(string kind, string error) : IDownstreamAdapter
    {
        private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Kind => kind;
        public int CallCount { get; private set; }

        public Task<string?> NotifyAsync(string baseUrl, string? apiKey, string? webhookHeadersJson, DownstreamTrigger trigger, CancellationToken ct)
        {
            CallCount++;
            _called.TrySetResult();
            return Task.FromResult<string?>(error);
        }

        public Task WaitForCountAsync(int expected, CancellationToken ct)
            => WaitUntilCountAsync(expected, ct);

        private async Task WaitUntilCountAsync(int expected, CancellationToken ct)
        {
            while (CallCount < expected)
            {
                await _called.Task.WaitAsync(ct);
                if (CallCount >= expected)
                    return;
            }
        }
    }

    private sealed class ConcurrencyAdapter(string kind) : IDownstreamAdapter
    {
        private readonly TaskCompletionSource _twoStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;
        private int _callCount;
        private int _maxInFlight;

        public string Kind => kind;
        public int CallCount => Volatile.Read(ref _callCount);
        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public async Task<string?> NotifyAsync(string baseUrl, string? apiKey, string? webhookHeadersJson, DownstreamTrigger trigger, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            var nowInFlight = Interlocked.Increment(ref _inFlight);
            UpdateMaxInFlight(nowInFlight);

            if (CallCount >= 2)
                _twoStarted.TrySetResult();

            try
            {
                await _twoStarted.Task.WaitAsync(ct);
                await _release.Task.WaitAsync(ct);
                return null;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public Task WaitForTwoStartedAsync(CancellationToken ct)
            => _twoStarted.Task.WaitAsync(ct);

        public void ReleaseAll()
            => _release.TrySetResult();

        private void UpdateMaxInFlight(int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxInFlight);
                if (candidate <= current)
                    return;

                if (Interlocked.CompareExchange(ref _maxInFlight, candidate, current) == current)
                    return;
            }
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;

            await Task.Delay(30);
        }

        Assert.Fail("Timed out waiting for expected condition.");
    }
}
