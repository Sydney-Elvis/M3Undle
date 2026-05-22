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
    private static readonly TimeSpan CleanWatchDecayThreshold = TimeSpan.FromMinutes(30);
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
            var summary = await LoadSummaryAsync(providerId, providerChannelId, now.UtcDateTime, ct);
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

    public StreamRelayPolicyDecision GetRelayPolicyDecision(
        string providerRelayPolicy,
        StreamChannelRecoveryPolicy recoveryPolicy)
    {
        var normalizedPolicy = CleanRelayModes.Normalize(providerRelayPolicy);
        return normalizedPolicy switch
        {
            CleanRelayModes.On => StreamRelayPolicyDecision.CleanRemux(
                normalizedPolicy,
                "Provider relay policy is On; clean remux is forced for this provider."),
            CleanRelayModes.Auto when recoveryPolicy.Profile == StreamChannelHealthProfile.Unstable =>
                StreamRelayPolicyDecision.CleanRemux(
                    normalizedPolicy,
                    $"Provider relay policy is Auto and channel health is {recoveryPolicy.Profile}; clean remux selected. {recoveryPolicy.Reason}"),
            CleanRelayModes.Auto => StreamRelayPolicyDecision.Direct(
                normalizedPolicy,
                $"Provider relay policy is Auto and channel health is {recoveryPolicy.Profile}; direct relay selected. {recoveryPolicy.Reason}"),
            _ => StreamRelayPolicyDecision.Direct(
                normalizedPolicy,
                "Provider relay policy is Off; direct relay is forced for this provider."),
        };
    }

    public async Task<StreamChannelHealthEvidence> GetEvidenceAsync(
        string providerId,
        string providerChannelId,
        ReconnectOptions reconnectOptions,
        CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        var summary = await LoadSummaryAsync(providerId, providerChannelId, now.UtcDateTime, ct);
        _cache[$"{providerId}:{providerChannelId}"] = new CacheEntry(now, summary);
        var policy = BuildPolicy(summary, reconnectOptions);
        return new StreamChannelHealthEvidence(
            providerId,
            providerChannelId,
            policy,
            GetRelayPolicyDecision(CleanRelayModes.Auto, policy),
            summary.UpstreamFailures,
            summary.RecoveryResumes,
            summary.FallbackRecoveryResumes,
            summary.IdrRecoveryResumes,
            summary.ClientAbortAfterRecovery,
            summary.ForcedRetunes,
            summary.TsSyncLoss,
            summary.CleanWatchEvents,
            summary.CleanWatchDuration,
            summary.LastAdverseEventUtc);
    }

    public void Invalidate(string providerId, string providerChannelId)
        => _cache.TryRemove($"{providerId}:{providerChannelId}", out _);

    private async Task<HealthSummary> LoadSummaryAsync(
        string providerId,
        string providerChannelId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var cutoffUtc = nowUtc - ObservationWindow;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.StreamChannelHealthEvents
            .AsNoTracking()
            .Where(e => e.ProviderId == providerId
                && e.ProviderChannelId == providerChannelId
                && e.EventUtc >= cutoffUtc)
            .Select(e => new HealthEventRow(
                e.EventKind,
                e.EventUtc,
                e.SafeStartKind,
                e.ClientAbortAfterRecovery,
                e.ForcedRetune,
                e.TsSyncLoss,
                e.CleanWatchDurationMs))
            .ToListAsync(ct);

        if (rows.Count == 0)
            return HealthSummary.Empty;

        var lastAdverseEventUtc = rows
            .Where(IsAdverse)
            .Select(e => (DateTime?)e.EventUtc)
            .Max();
        var cleanRows = rows.Where(e => e.EventKind == nameof(StreamDiagnosticEventKind.CleanWatchCompleted)
            && (lastAdverseEventUtc is null || e.EventUtc > lastAdverseEventUtc.Value));
        var cleanWatchMs = cleanRows.Sum(e => Math.Max(0, e.CleanWatchDurationMs ?? 0));

        return new HealthSummary(
            rows.Count(e => e.EventKind == nameof(StreamDiagnosticEventKind.UpstreamFailure)),
            rows.Count(e => e.EventKind == nameof(StreamDiagnosticEventKind.RecoveryOutputResumed)),
            rows.Count(e => e.EventKind == nameof(StreamDiagnosticEventKind.RecoveryOutputResumed) && e.SafeStartKind == "FallbackPacketBoundary"),
            rows.Count(e => e.EventKind == nameof(StreamDiagnosticEventKind.RecoveryOutputResumed) && e.SafeStartKind == "H264Idr"),
            rows.Count(e => e.ClientAbortAfterRecovery),
            rows.Count(e => e.ForcedRetune),
            rows.Count(e => e.TsSyncLoss),
            rows.Count(e => e.EventKind == nameof(StreamDiagnosticEventKind.CleanWatchCompleted)
                && (lastAdverseEventUtc is null || e.EventUtc > lastAdverseEventUtc.Value)),
            TimeSpan.FromMilliseconds(cleanWatchMs),
            lastAdverseEventUtc);
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
        StreamChannelHealthProfile profile;
        if (summary.ForcedRetunes > 0
            || summary.ClientAbortAfterRecovery >= 2
            || summary.FallbackRecoveryResumes >= 2
            || summary.TsSyncLoss >= 2)
        {
            profile = StreamChannelHealthProfile.Unstable;
        }
        else if (summary.ClientAbortAfterRecovery > 0
            || summary.FallbackRecoveryResumes > 0
            || summary.UpstreamFailures >= 2
            || summary.RecoveryResumes >= 2
            || summary.TsSyncLoss > 0)
        {
            profile = StreamChannelHealthProfile.Cautious;
        }
        else
        {
            profile = StreamChannelHealthProfile.Stable;
        }

        if (profile == StreamChannelHealthProfile.Stable || summary.CleanWatchDuration < CleanWatchDecayThreshold)
            return profile;

        return profile == StreamChannelHealthProfile.Unstable
            ? StreamChannelHealthProfile.Cautious
            : StreamChannelHealthProfile.Stable;
    }

    private static string BuildReason(HealthSummary summary, StreamChannelHealthProfile profile)
        => profile switch
        {
            StreamChannelHealthProfile.Unstable =>
                $"Recent health events classify channel as unstable: upstreamFailures={summary.UpstreamFailures}, recoveries={summary.RecoveryResumes}, fallbackRecoveries={summary.FallbackRecoveryResumes}, abortsAfterRecovery={summary.ClientAbortAfterRecovery}, forcedRetunes={summary.ForcedRetunes}, tsSyncLoss={summary.TsSyncLoss}, cleanWatchSeconds={summary.CleanWatchDuration.TotalSeconds:F0}.",
            StreamChannelHealthProfile.Cautious =>
                $"Recent health events classify channel as cautious: upstreamFailures={summary.UpstreamFailures}, recoveries={summary.RecoveryResumes}, fallbackRecoveries={summary.FallbackRecoveryResumes}, abortsAfterRecovery={summary.ClientAbortAfterRecovery}, tsSyncLoss={summary.TsSyncLoss}, cleanWatchSeconds={summary.CleanWatchDuration.TotalSeconds:F0}.",
            _ => summary.CleanWatchDuration > TimeSpan.Zero
                ? $"Recent clean watch evidence relaxed channel health: cleanWatchSeconds={summary.CleanWatchDuration.TotalSeconds:F0}."
                : "No recent recovery failures or post-recovery aborts were found.",
        };

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    private sealed record CacheEntry(DateTimeOffset CachedAt, HealthSummary Summary);

    private static bool IsAdverse(HealthEventRow row)
        => !string.Equals(row.EventKind, nameof(StreamDiagnosticEventKind.CleanWatchCompleted), StringComparison.Ordinal);

    private sealed record HealthEventRow(
        string EventKind,
        DateTime EventUtc,
        string? SafeStartKind,
        bool ClientAbortAfterRecovery,
        bool ForcedRetune,
        bool TsSyncLoss,
        double? CleanWatchDurationMs);

    private sealed record HealthSummary(
        int UpstreamFailures,
        int RecoveryResumes,
        int FallbackRecoveryResumes,
        int IdrRecoveryResumes,
        int ClientAbortAfterRecovery,
        int ForcedRetunes,
        int TsSyncLoss,
        int CleanWatchEvents,
        TimeSpan CleanWatchDuration,
        DateTime? LastAdverseEventUtc)
    {
        public static HealthSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero, null);
    }
}
