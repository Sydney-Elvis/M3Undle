using M3Undle.Web.Data.Entities;
using M3Undle.Web.Data;
using M3Undle.Web.Streaming.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static M3Undle.Web.Streaming.Configuration.StreamingSettingsLimits;

namespace M3Undle.Web.Application;

public interface IStreamingSettingsService
{
    Task<StreamingSettingsState> GetSettingsAsync(CancellationToken ct = default);
    Task<StreamingSettingsUpdateResult> UpdateAsync(UpdateStreamingSettingsCommand command, CancellationToken ct = default);
    Task ClearRestartRequiredAsync(CancellationToken ct = default);
}

public sealed class StreamingSettingsService(
    ApplicationDbContext db,
    IOptions<StreamProxyOptions> proxyOptions,
    IOptions<BufferOptions> bufferOptions,
    IOptions<ReconnectOptions> reconnectOptions) : IStreamingSettingsService
{
    public async Task<StreamingSettingsState> GetSettingsAsync(CancellationToken ct = default)
    {
        var settings = await db.SiteSettings.AsNoTracking().OrderBy(x => x.Id).FirstOrDefaultAsync(ct)
            ?? new SiteSettings { Id = 1 };

        return new StreamingSettingsState(
            Saved: Map(settings),
            Applied: new StreamingSettingsSnapshot(
                StreamingEnabled: proxyOptions.Value.StreamingEnabled,
                MaxConcurrentSessions: proxyOptions.Value.MaxConcurrentSessions,
                IdleGraceSeconds: (int)Math.Round(proxyOptions.Value.IdleGrace.TotalSeconds),
                IdleGraceHardCapSeconds: (int)Math.Round(proxyOptions.Value.IdleGraceHardCap.TotalSeconds),
                BufferMaxBytesPerSession: bufferOptions.Value.MaxBytesPerSession,
                BufferMaxBytesHardCap: bufferOptions.Value.MaxBytesHardCap,
                BufferReadChunkSizeBytes: bufferOptions.Value.ReadChunkSizeBytes,
                ReconnectReadStallTimeoutSeconds: (int)Math.Round(reconnectOptions.Value.ReadStallTimeout.TotalSeconds),
                ReconnectOutageWindowSeconds: (int)Math.Round(reconnectOptions.Value.OutageWindow.TotalSeconds),
                ReconnectConnectTimeoutSeconds: (int)Math.Round(reconnectOptions.Value.ConnectTimeout.TotalSeconds)),
            RestartRequired: settings.StreamingSettingsRestartRequired);
    }

    public async Task<StreamingSettingsUpdateResult> UpdateAsync(UpdateStreamingSettingsCommand command, CancellationToken ct = default)
    {
        var settings = await db.SiteSettings.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new SiteSettings { Id = 1 };
            db.SiteSettings.Add(settings);
        }

        var errors = Validate(command).ToArray();
        if (errors.Length > 0)
        {
            return new StreamingSettingsUpdateResult(
                Succeeded: false,
                Error: string.Join(" ", errors),
                Settings: Map(settings),
                RestartRequired: settings.StreamingSettingsRestartRequired,
                Changed: false);
        }

        var changed =
            settings.StreamingEnabled != command.StreamingEnabled ||
            settings.StreamMaxConcurrentSessions != command.MaxConcurrentSessions ||
            settings.StreamIdleGraceSeconds != command.IdleGraceSeconds ||
            settings.StreamIdleGraceHardCapSeconds != command.IdleGraceHardCapSeconds ||
            settings.StreamBufferMaxBytesPerSession != command.BufferMaxBytesPerSession ||
            settings.StreamBufferMaxBytesHardCap != command.BufferMaxBytesHardCap ||
            settings.StreamBufferReadChunkSizeBytes != command.BufferReadChunkSizeBytes ||
            settings.StreamReconnectReadStallTimeoutSeconds != command.ReconnectReadStallTimeoutSeconds ||
            settings.StreamReconnectOutageWindowSeconds != command.ReconnectOutageWindowSeconds ||
            settings.StreamReconnectConnectTimeoutSeconds != command.ReconnectConnectTimeoutSeconds;

        settings.StreamingEnabled = command.StreamingEnabled;
        settings.StreamMaxConcurrentSessions = command.MaxConcurrentSessions;
        settings.StreamIdleGraceSeconds = command.IdleGraceSeconds;
        settings.StreamIdleGraceHardCapSeconds = command.IdleGraceHardCapSeconds;
        settings.StreamBufferMaxBytesPerSession = command.BufferMaxBytesPerSession;
        settings.StreamBufferMaxBytesHardCap = command.BufferMaxBytesHardCap;
        settings.StreamBufferReadChunkSizeBytes = command.BufferReadChunkSizeBytes;
        settings.StreamReconnectReadStallTimeoutSeconds = command.ReconnectReadStallTimeoutSeconds;
        settings.StreamReconnectOutageWindowSeconds = command.ReconnectOutageWindowSeconds;
        settings.StreamReconnectConnectTimeoutSeconds = command.ReconnectConnectTimeoutSeconds;

        if (changed)
            settings.StreamingSettingsRestartRequired = true;

        await db.SaveChangesAsync(ct);

        return new StreamingSettingsUpdateResult(
            Succeeded: true,
            Error: null,
            Settings: Map(settings),
            RestartRequired: settings.StreamingSettingsRestartRequired,
            Changed: changed);
    }

    public async Task ClearRestartRequiredAsync(CancellationToken ct = default)
    {
        var settings = await db.SiteSettings.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (settings is null || !settings.StreamingSettingsRestartRequired)
            return;

        settings.StreamingSettingsRestartRequired = false;
        await db.SaveChangesAsync(ct);
    }

    private static IEnumerable<string> Validate(UpdateStreamingSettingsCommand command)
    {
        if (command.MaxConcurrentSessions <= 0)
            yield return "Max concurrent sessions must be greater than 0.";
        if (command.IdleGraceSeconds <= 0)
            yield return "Idle grace must be greater than 0 seconds.";
        if (command.IdleGraceHardCapSeconds < command.IdleGraceSeconds)
            yield return "Idle grace hard cap must be greater than or equal to idle grace.";
        if (command.BufferMaxBytesPerSession <= 0)
            yield return "Buffer per session must be greater than 0 bytes.";
        if (command.BufferMaxBytesPerSession > MaxBytesPerSession)
            yield return $"Buffer per session must not exceed {MaxBytesPerSession / (1024 * 1024)} MiB.";
        if (command.BufferMaxBytesHardCap < command.BufferMaxBytesPerSession)
            yield return "Buffer hard cap must be greater than or equal to buffer per session.";
        if (command.BufferReadChunkSizeBytes <= 0)
            yield return "Read chunk size must be greater than 0 bytes.";
        if (command.BufferReadChunkSizeBytes > MaxReadChunkSizeBytes)
            yield return $"Read chunk size must not exceed {MaxReadChunkSizeBytes / (1024 * 1024)} MiB.";
        if (command.ReconnectReadStallTimeoutSeconds <= 0)
            yield return "Reconnect stall timeout must be greater than 0 seconds.";
        if (command.ReconnectOutageWindowSeconds < command.ReconnectReadStallTimeoutSeconds)
            yield return "Outage window must be greater than or equal to reconnect stall timeout.";
        if (command.ReconnectConnectTimeoutSeconds <= 0)
            yield return "Connect timeout must be greater than 0 seconds.";
    }

    private static StreamingSettingsSnapshot Map(SiteSettings settings)
        => new(
            StreamingEnabled: settings.StreamingEnabled,
            MaxConcurrentSessions: settings.StreamMaxConcurrentSessions,
            IdleGraceSeconds: settings.StreamIdleGraceSeconds,
            IdleGraceHardCapSeconds: settings.StreamIdleGraceHardCapSeconds,
            BufferMaxBytesPerSession: settings.StreamBufferMaxBytesPerSession,
            BufferMaxBytesHardCap: settings.StreamBufferMaxBytesHardCap,
            BufferReadChunkSizeBytes: settings.StreamBufferReadChunkSizeBytes,
            ReconnectReadStallTimeoutSeconds: settings.StreamReconnectReadStallTimeoutSeconds,
            ReconnectOutageWindowSeconds: settings.StreamReconnectOutageWindowSeconds,
            ReconnectConnectTimeoutSeconds: settings.StreamReconnectConnectTimeoutSeconds);
}

public sealed record StreamingSettingsSnapshot(
    bool StreamingEnabled,
    int MaxConcurrentSessions,
    int IdleGraceSeconds,
    int IdleGraceHardCapSeconds,
    int BufferMaxBytesPerSession,
    int BufferMaxBytesHardCap,
    int BufferReadChunkSizeBytes,
    int ReconnectReadStallTimeoutSeconds,
    int ReconnectOutageWindowSeconds,
    int ReconnectConnectTimeoutSeconds);

public sealed record StreamingSettingsState(
    StreamingSettingsSnapshot Saved,
    StreamingSettingsSnapshot Applied,
    bool RestartRequired);

public sealed record UpdateStreamingSettingsCommand(
    bool StreamingEnabled,
    int MaxConcurrentSessions,
    int IdleGraceSeconds,
    int IdleGraceHardCapSeconds,
    int BufferMaxBytesPerSession,
    int BufferMaxBytesHardCap,
    int BufferReadChunkSizeBytes,
    int ReconnectReadStallTimeoutSeconds,
    int ReconnectOutageWindowSeconds,
    int ReconnectConnectTimeoutSeconds);

public sealed record StreamingSettingsUpdateResult(
    bool Succeeded,
    string? Error,
    StreamingSettingsSnapshot Settings,
    bool RestartRequired,
    bool Changed);

public sealed class StreamProxyDbOptionsConfigurator(IServiceScopeFactory scopeFactory) : IConfigureOptions<StreamProxyOptions>
{
    public void Configure(StreamProxyOptions options)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var settings = db.SiteSettings.AsNoTracking().OrderBy(x => x.Id).FirstOrDefault();
        if (settings is null)
            return;

        options.StreamingEnabled = settings.StreamingEnabled;
        options.MaxConcurrentSessions = settings.StreamMaxConcurrentSessions;
        options.IdleGrace = TimeSpan.FromSeconds(settings.StreamIdleGraceSeconds);
        options.IdleGraceHardCap = TimeSpan.FromSeconds(settings.StreamIdleGraceHardCapSeconds);
    }
}

public sealed class BufferDbOptionsConfigurator(IServiceScopeFactory scopeFactory) : IConfigureOptions<BufferOptions>
{
    public void Configure(BufferOptions options)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var settings = db.SiteSettings.AsNoTracking().OrderBy(x => x.Id).FirstOrDefault();
        if (settings is null)
            return;

        options.MaxBytesPerSession = settings.StreamBufferMaxBytesPerSession;
        options.MaxBytesHardCap = settings.StreamBufferMaxBytesHardCap;
        options.ReadChunkSizeBytes = settings.StreamBufferReadChunkSizeBytes;
    }
}

public sealed class ReconnectDbOptionsConfigurator(IServiceScopeFactory scopeFactory) : IConfigureOptions<ReconnectOptions>
{
    public void Configure(ReconnectOptions options)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var settings = db.SiteSettings.AsNoTracking().OrderBy(x => x.Id).FirstOrDefault();
        if (settings is null)
            return;

        options.ReadStallTimeout = TimeSpan.FromSeconds(settings.StreamReconnectReadStallTimeoutSeconds);
        options.OutageWindow = TimeSpan.FromSeconds(settings.StreamReconnectOutageWindowSeconds);
        options.ConnectTimeout = TimeSpan.FromSeconds(settings.StreamReconnectConnectTimeoutSeconds);
    }
}
