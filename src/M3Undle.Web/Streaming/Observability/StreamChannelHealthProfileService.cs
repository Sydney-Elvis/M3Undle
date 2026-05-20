using System.Collections.Concurrent;
using M3Undle.Web.Data;
using M3Undle.Web.Streaming.Configuration;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Streaming.Observability;

public sealed class StreamChannelHealthProfileService(
    IServiceScopeFactory scopeFactory,
    ILogger<StreamChannelHealthProfileService> logger,
    TimeProvider? timeProvider = null) : IStreamChannelHealthProfileService
{
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromHours(24);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UnstableHoldLimit = TimeSpan.FromSeconds(5);
    private const int UnstableSearchLimitBytes = 2 * 1024 * 1024;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public async Task<StreamChannelRecoveryPolicy> GetRecoveryPolicyAsync(
        string providerId,
        string providerChannelId,
        ReconnectOptions reconnectOptions,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(providerChannelId))
            return StreamChannelRecoveryPolicy.FromOptions(reconnectOptions);

        var now = _timeProvider.GetUtcNow();
        var cacheKey = $"{providerId}:{providerChannelId}";
        if (_cache.TryGetValue(cacheKey, out var cached) && now - cached.CachedAt < CacheTtl)
            return BuildPolicy(cached.Summary, reconnectOptions);

        try
        {
            var cutoffUtc = now.UtcDateTime - ObservationWindow;
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var rows = await db.StreamChannelHealthEvents
                .AsNoTracking()
                .Where(e => e.ProviderId == providerId
                    && e.ProviderChannelId == providerChannelId
                    && e.EventUtc >= cutoffUtc)
                .GroupBy(_ => 1)
                .Select(g => new HealthSummary(
                    g.Count(e => e.EventKind == "UpstreamFailure"),
                    g.Count(e => e.EventKind == "RecoveryOutputResumed"),
                    g.Count(e => e.EventKind == "RecoveryOutputResumed" && e.SafeStartKind == "FallbackPacketBoundary"),
                    g.Count(e => e.EventKind == "RecoveryOutputResumed" && e.SafeStartKind == "H264Idr"),
                    g.Count(e => e.ClientAbortAfterRecovery),
                    g.Count(e => e.ForcedRetune),
                    g.Count(e => e.TsSyncLoss)))
                .SingleOrDefaultAsync(ct);

            var summary = rows ?? HealthSummary.Empty;
            _cache[cacheKey] = new CacheEntry(now, summary);
            return BuildPolicy(summary, reconnectOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to derive stream channel health profile for {ProviderId}/{ProviderChannelId}; using configured recovery policy.",
                providerId,
                providerChannelId);
            return StreamChannelRecoveryPolicy.FromOptions(reconnectOptions);
        }
    }

    private static StreamChannelRecoveryPolicy BuildPolicy(HealthSummary summary, ReconnectOptions options)
    {
        var profile = DeriveProfile(summary);
        var requireRetune = profile == StreamChannelHealthProfile.Unstable
            && summary.ClientAbortAfterRecovery >= 2
            && summary.IdrRecoveryResumes >= 1;
        return profile switch
        {
            StreamChannelHealthProfile.Unstable => new StreamChannelRecoveryPolicy(
                profile,
                Max(options.RecoveryOutputHoldLimit, UnstableHoldLimit),
                Math.Max(options.RecoverySafeStartSearchLimitBytes, UnstableSearchLimitBytes),
                AllowPacketBoundaryRecoveryFallback: false,
                RequireDownstreamRetune: requireRetune,
                DownstreamRetuneReason: requireRetune
                    ? $"Channel has {summary.ClientAbortAfterRecovery} downstream client aborts after IDR recovery in the observation window."
                    : null,
                Reason: BuildReason(summary, profile)),
            _ => new StreamChannelRecoveryPolicy(
                profile,
                options.RecoveryOutputHoldLimit,
                options.RecoverySafeStartSearchLimitBytes,
                options.AllowPacketBoundaryRecoveryFallback,
                RequireDownstreamRetune: false,
                DownstreamRetuneReason: null,
                BuildReason(summary, profile)),
        };
    }

    private static StreamChannelHealthProfile DeriveProfile(HealthSummary summary)
    {
        if (summary.ForcedRetunes > 0
            || summary.ClientAbortAfterRecovery >= 2
            || summary.FallbackRecoveryResumes >= 2
            || summary.TsSyncLoss >= 2)
            return StreamChannelHealthProfile.Unstable;

        if (summary.ClientAbortAfterRecovery > 0
            || summary.FallbackRecoveryResumes > 0
            || summary.UpstreamFailures >= 2
            || summary.RecoveryResumes >= 2
            || summary.TsSyncLoss > 0)
            return StreamChannelHealthProfile.Cautious;

        return StreamChannelHealthProfile.Fast;
    }

    private static string BuildReason(HealthSummary summary, StreamChannelHealthProfile profile)
        => profile switch
        {
            StreamChannelHealthProfile.Unstable =>
                $"Recent health events classify channel as unstable: upstreamFailures={summary.UpstreamFailures}, recoveries={summary.RecoveryResumes}, fallbackRecoveries={summary.FallbackRecoveryResumes}, abortsAfterRecovery={summary.ClientAbortAfterRecovery}, forcedRetunes={summary.ForcedRetunes}, tsSyncLoss={summary.TsSyncLoss}.",
            StreamChannelHealthProfile.Cautious =>
                $"Recent health events classify channel as cautious: upstreamFailures={summary.UpstreamFailures}, recoveries={summary.RecoveryResumes}, fallbackRecoveries={summary.FallbackRecoveryResumes}, abortsAfterRecovery={summary.ClientAbortAfterRecovery}, tsSyncLoss={summary.TsSyncLoss}.",
            _ => "No recent recovery failures or post-recovery aborts were found.",
        };

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    private sealed record CacheEntry(DateTimeOffset CachedAt, HealthSummary Summary);

    private sealed record HealthSummary(
        int UpstreamFailures,
        int RecoveryResumes,
        int FallbackRecoveryResumes,
        int IdrRecoveryResumes,
        int ClientAbortAfterRecovery,
        int ForcedRetunes,
        int TsSyncLoss)
    {
        public static HealthSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
    }
}
