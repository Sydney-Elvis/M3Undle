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
    private static readonly TimeSpan TrendRecentWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan TrendComparisonWindow = TimeSpan.FromHours(2);
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
        var rows = await LoadRowsAsync(providerId, providerChannelId, now.UtcDateTime - ObservationWindow, ct);
        var summary = BuildSummaryFromRows(rows);
        var trend = ComputeTrendFromRows(rows, now.UtcDateTime);
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
            summary.LastAdverseEventUtc,
            summary.LastCleanWatchUtc,
            trend);
    }

    public void Invalidate(string providerId, string providerChannelId)
        => _cache.TryRemove($"{providerId}:{providerChannelId}", out _);

    private async Task<List<HealthEventRow>> LoadRowsAsync(
        string providerId,
        string providerChannelId,
        DateTime cutoffUtc,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.StreamChannelHealthEvents
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
    }

    private static HealthSummary BuildSummaryFromRows(List<HealthEventRow> rows)
    {
        if (rows.Count == 0)
            return HealthSummary.Empty;

        var lastAdverseEventUtc = rows
            .Where(IsAdverse)
            .Select(e => (DateTime?)e.EventUtc)
            .Max();
        var cleanRows = rows.Where(e => e.EventKind == nameof(StreamDiagnosticEventKind.CleanWatchCompleted)
            && (lastAdverseEventUtc is null || e.EventUtc > lastAdverseEventUtc.Value))
            .ToList();
        var cleanWatchMs = cleanRows.Sum(e => Math.Max(0, e.CleanWatchDurationMs ?? 0));
        var lastCleanWatchUtc = cleanRows
            .Select(e => (DateTime?)e.EventUtc)
            .Max();

        return new HealthSummary(
            rows.Count(e => e.EventKind == nameof(StreamDiagnosticEventKind.UpstreamFailure)),
            rows.Count(e => e.EventKind == nameof(StreamDiagnosticEventKind.RecoveryOutputResumed)),
            rows.Count(e => e.EventKind == nameof(StreamDiagnosticEventKind.RecoveryOutputResumed) && e.SafeStartKind == "FallbackPacketBoundary"),
            rows.Count(e => e.EventKind == nameof(StreamDiagnosticEventKind.RecoveryOutputResumed) && e.SafeStartKind == "H264Idr"),
            rows.Count(e => e.ClientAbortAfterRecovery),
            rows.Count(e => e.ForcedRetune),
            rows.Count(e => e.TsSyncLoss),
            cleanRows.Count,
            TimeSpan.FromMilliseconds(cleanWatchMs),
            lastAdverseEventUtc,
            lastCleanWatchUtc);
    }

    private async Task<HealthSummary> LoadSummaryAsync(
        string providerId,
        string providerChannelId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var rows = await LoadRowsAsync(providerId, providerChannelId, nowUtc - ObservationWindow, ct);
        return BuildSummaryFromRows(rows);
    }

    private static StreamChannelHealthTrendResult ComputeTrendFromRows(
        List<HealthEventRow> allRows,
        DateTime nowUtc)
    {
        if (allRows.Count == 0)
            return StreamChannelHealthTrendResult.Insufficient("No health events in the observation window.");

        var recentCutoff = nowUtc - TrendRecentWindow;
        var comparisonCutoff = nowUtc - TrendComparisonWindow;

        var recentRows = allRows.Where(e => e.EventUtc >= recentCutoff).ToList();
        var comparisonRows = allRows.Where(e => e.EventUtc >= comparisonCutoff && e.EventUtc < recentCutoff).ToList();

        var recentAdverse = recentRows.Count(IsTrendAdverse);
        var comparisonAdverse = comparisonRows.Count(IsTrendAdverse);

        var recentCleanWatch = TimeSpan.FromMilliseconds(
            recentRows.Where(e => e.EventKind == nameof(StreamDiagnosticEventKind.CleanWatchCompleted))
                      .Sum(e => Math.Max(0, e.CleanWatchDurationMs ?? 0)));
        var comparisonCleanWatch = TimeSpan.FromMilliseconds(
            comparisonRows.Where(e => e.EventKind == nameof(StreamDiagnosticEventKind.CleanWatchCompleted))
                          .Sum(e => Math.Max(0, e.CleanWatchDurationMs ?? 0)));

        var lastTrendAdverseUtc = allRows.Where(IsTrendAdverse).Select(e => (DateTime?)e.EventUtc).Max();
        var cleanWatchSinceLastAdverse = TimeSpan.FromMilliseconds(
            allRows.Where(e => e.EventKind == nameof(StreamDiagnosticEventKind.CleanWatchCompleted)
                        && (lastTrendAdverseUtc is null || e.EventUtc > lastTrendAdverseUtc.Value))
                   .Sum(e => Math.Max(0, e.CleanWatchDurationMs ?? 0)));

        var recentProfile = DeriveWindowProfile(recentRows);
        var comparisonProfile = DeriveWindowProfile(comparisonRows);

        if (recentAdverse == 0 && comparisonAdverse == 0)
        {
            return new StreamChannelHealthTrendResult(
                StreamChannelHealthTrend.Stable,
                "No adverse health events detected in the last 2 hours.",
                0, 0, recentCleanWatch, comparisonCleanWatch, cleanWatchSinceLastAdverse,
                recentProfile, comparisonProfile);
        }

        var recentHasSevere = recentRows.Any(e => e.ForcedRetune || e.ClientAbortAfterRecovery);
        if (recentHasSevere)
        {
            var severeReason = recentRows.Any(e => e.ForcedRetune)
                ? "A forced retune occurred in the last hour."
                : "A client abort after recovery occurred in the last hour.";
            return new StreamChannelHealthTrendResult(
                StreamChannelHealthTrend.Worsening, severeReason,
                recentAdverse, comparisonAdverse, recentCleanWatch, comparisonCleanWatch,
                cleanWatchSinceLastAdverse, recentProfile, comparisonProfile);
        }

        if ((int)recentProfile > (int)comparisonProfile)
        {
            return new StreamChannelHealthTrendResult(
                StreamChannelHealthTrend.Worsening,
                $"Health profile escalated from {comparisonProfile} to {recentProfile} in the last hour.",
                recentAdverse, comparisonAdverse, recentCleanWatch, comparisonCleanWatch,
                cleanWatchSinceLastAdverse, recentProfile, comparisonProfile);
        }

        if ((int)recentProfile < (int)comparisonProfile)
        {
            return new StreamChannelHealthTrendResult(
                StreamChannelHealthTrend.Improving,
                $"Health profile relaxed from {comparisonProfile} to {recentProfile} in the last hour.",
                recentAdverse, comparisonAdverse, recentCleanWatch, comparisonCleanWatch,
                cleanWatchSinceLastAdverse, recentProfile, comparisonProfile);
        }

        if (recentAdverse == 0 && comparisonAdverse > 0)
        {
            var reason = cleanWatchSinceLastAdverse >= CleanWatchDecayThreshold
                ? $"No adverse events in the last hour with {cleanWatchSinceLastAdverse.TotalMinutes:F0} min of clean watch since the last issue."
                : $"No adverse events in the last hour (previously {comparisonAdverse} in the prior hour).";
            return new StreamChannelHealthTrendResult(
                StreamChannelHealthTrend.Improving, reason,
                recentAdverse, comparisonAdverse, recentCleanWatch, comparisonCleanWatch,
                cleanWatchSinceLastAdverse, recentProfile, comparisonProfile);
        }

        if (recentAdverse > comparisonAdverse)
        {
            return new StreamChannelHealthTrendResult(
                StreamChannelHealthTrend.Worsening,
                $"Adverse event count increased from {comparisonAdverse} to {recentAdverse} in the last hour.",
                recentAdverse, comparisonAdverse, recentCleanWatch, comparisonCleanWatch,
                cleanWatchSinceLastAdverse, recentProfile, comparisonProfile);
        }

        if (recentAdverse < comparisonAdverse && recentCleanWatch > TimeSpan.Zero)
        {
            return new StreamChannelHealthTrendResult(
                StreamChannelHealthTrend.Improving,
                $"Adverse events decreased from {comparisonAdverse} to {recentAdverse} in the last hour with {recentCleanWatch.TotalMinutes:F0} min of recent clean watch.",
                recentAdverse, comparisonAdverse, recentCleanWatch, comparisonCleanWatch,
                cleanWatchSinceLastAdverse, recentProfile, comparisonProfile);
        }

        return new StreamChannelHealthTrendResult(
            StreamChannelHealthTrend.Stable,
            $"Adverse event count is consistent across both hours ({recentAdverse} recent, {comparisonAdverse} prior).",
            recentAdverse, comparisonAdverse, recentCleanWatch, comparisonCleanWatch,
            cleanWatchSinceLastAdverse, recentProfile, comparisonProfile);
    }

    private static StreamChannelHealthProfile DeriveWindowProfile(List<HealthEventRow> rows)
    {
        var fallbackResumes = rows.Count(e =>
            e.EventKind == nameof(StreamDiagnosticEventKind.RecoveryOutputResumed)
            && e.SafeStartKind == "FallbackPacketBoundary");
        var forcedRetunes = rows.Count(e => e.ForcedRetune);
        var tsSyncLoss = rows.Count(e => e.TsSyncLoss);
        var upstreamFailures = rows.Count(e => e.EventKind == nameof(StreamDiagnosticEventKind.UpstreamFailure));
        var recoveryResumes = rows.Count(e => e.EventKind == nameof(StreamDiagnosticEventKind.RecoveryOutputResumed));

        if (forcedRetunes > 0 || fallbackResumes >= 2 || tsSyncLoss >= 2)
            return StreamChannelHealthProfile.Unstable;
        if (fallbackResumes > 0 || upstreamFailures >= 2 || recoveryResumes >= 2 || tsSyncLoss > 0)
            return StreamChannelHealthProfile.Cautious;
        return StreamChannelHealthProfile.Stable;
    }

    private static bool IsTrendAdverse(HealthEventRow row)
        => row.EventKind == nameof(StreamDiagnosticEventKind.UpstreamFailure)
        || row.ClientAbortAfterRecovery
        || row.ForcedRetune
        || row.TsSyncLoss
        || (row.EventKind == nameof(StreamDiagnosticEventKind.RecoveryOutputResumed)
            && row.SafeStartKind == "FallbackPacketBoundary");

    private static StreamChannelRecoveryPolicy BuildPolicy(HealthSummary summary, ReconnectOptions options)
    {
        var profile = DeriveProfile(summary);
        return profile switch
        {
            StreamChannelHealthProfile.Unstable => new StreamChannelRecoveryPolicy(
                profile,
                Max(options.RecoveryOutputHoldLimit, UnstableHoldLimit),
                Math.Max(options.RecoverySafeStartSearchLimitBytes, UnstableSearchLimitBytes),
                AllowPacketBoundaryRecoveryFallback: false,
                RequireDownstreamRetune: true,
                DownstreamRetuneReason: BuildDownstreamRetuneReason(summary),
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
            || summary.FallbackRecoveryResumes >= 2
            || summary.TsSyncLoss >= 2)
        {
            profile = StreamChannelHealthProfile.Unstable;
        }
        else if (summary.FallbackRecoveryResumes > 0
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

    private static string BuildDownstreamRetuneReason(HealthSummary summary)
        => $"Channel health is Unstable: upstreamFailures={summary.UpstreamFailures}, recoveries={summary.RecoveryResumes}, idrRecoveries={summary.IdrRecoveryResumes}, fallbackRecoveries={summary.FallbackRecoveryResumes}, abortsAfterRecovery={summary.ClientAbortAfterRecovery}, forcedRetunes={summary.ForcedRetunes}, tsSyncLoss={summary.TsSyncLoss}.";

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    private sealed record CacheEntry(DateTimeOffset CachedAt, HealthSummary Summary);

    private static bool IsAdverse(HealthEventRow row)
        => row.EventKind is not (nameof(StreamDiagnosticEventKind.CleanWatchCompleted)
            or nameof(StreamDiagnosticEventKind.SubscriberQueueFull));

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
        DateTime? LastAdverseEventUtc,
        DateTime? LastCleanWatchUtc)
    {
        public static HealthSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero, null, null);
    }
}
