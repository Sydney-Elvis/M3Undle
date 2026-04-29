using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using M3Undle.Core;
using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Streaming.Observability;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Observability;

public sealed class M3UndleMetrics : IDisposable
{
    public const string MeterName = "M3Undle";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StreamingRegistry _streamingRegistry;
    private readonly HdHomeRunTunerCountResolver _tunerCountResolver;
    private readonly TimeProvider _timeProvider;
    private readonly AppBuildInfo _buildInfo;
    private readonly DateTimeOffset _startedUtc;
    private readonly Meter _meter;
    private readonly Counter<long> _providerRefreshFailures;
    private readonly Histogram<double> _providerRefreshDuration;
    private readonly Counter<long> _providerStreamLimitReached;
    private readonly Counter<long> _streamSessionsStarted;
    private readonly Counter<long> _streamSessionsEnded;
    private readonly Counter<long> _streamReconnects;
    private readonly Counter<long> _streamFailures;
    private readonly Counter<long> _lineupAddedChannels;
    private readonly Counter<long> _lineupRemovedChannels;
    private readonly Counter<long> _lineupRenamedChannels;
    private readonly Counter<long> _lineupMappingChanges;
    private readonly Counter<long> _epgRefreshFailures;
    private readonly Counter<long> _hdhrTuneRequests;
    private readonly Counter<long> _hdhrTuneFailures;
    private readonly Counter<long> _hdhrDiscoveryRequests;
    private readonly Counter<long> _httpRequests;
    private readonly Histogram<double> _httpRequestDuration;
    private readonly ConcurrentDictionary<string, double> _lastProviderRefreshTimestamps = new(StringComparer.Ordinal);
    private readonly object _epgGate = new();
    private EpgMetricSnapshot _epgSnapshot = new(0, 0, 0, null);

    public M3UndleMetrics(
        IServiceScopeFactory scopeFactory,
        StreamingRegistry streamingRegistry,
        HdHomeRunTunerCountResolver tunerCountResolver,
        TimeProvider timeProvider,
        AppBuildInfo buildInfo)
    {
        _scopeFactory = scopeFactory;
        _streamingRegistry = streamingRegistry;
        _tunerCountResolver = tunerCountResolver;
        _timeProvider = timeProvider;
        _buildInfo = buildInfo;
        _startedUtc = _timeProvider.GetUtcNow();
        _meter = new Meter(MeterName, _buildInfo.Version);

        _providerRefreshFailures = _meter.CreateCounter<long>("m3undle_provider_refresh_failures_total");
        _providerRefreshDuration = _meter.CreateHistogram<double>("m3undle_provider_refresh_duration_seconds", unit: "s");
        _providerStreamLimitReached = _meter.CreateCounter<long>("m3undle_provider_stream_limit_reached_total");
        _streamSessionsStarted = _meter.CreateCounter<long>("m3undle_stream_sessions_started_total");
        _streamSessionsEnded = _meter.CreateCounter<long>("m3undle_stream_sessions_ended_total");
        _streamReconnects = _meter.CreateCounter<long>("m3undle_stream_reconnects_total");
        _streamFailures = _meter.CreateCounter<long>("m3undle_stream_failures_total");
        _lineupAddedChannels = _meter.CreateCounter<long>("m3undle_lineup_added_channels_total");
        _lineupRemovedChannels = _meter.CreateCounter<long>("m3undle_lineup_removed_channels_total");
        _lineupRenamedChannels = _meter.CreateCounter<long>("m3undle_lineup_renamed_channels_total");
        _lineupMappingChanges = _meter.CreateCounter<long>("m3undle_lineup_mapping_changes_total");
        _epgRefreshFailures = _meter.CreateCounter<long>("m3undle_epg_refresh_failures_total");
        _hdhrTuneRequests = _meter.CreateCounter<long>("m3undle_hdhr_tune_requests_total");
        _hdhrTuneFailures = _meter.CreateCounter<long>("m3undle_hdhr_tune_failures_total");
        _hdhrDiscoveryRequests = _meter.CreateCounter<long>("m3undle_hdhr_discovery_requests_total");
        _httpRequests = _meter.CreateCounter<long>("http_requests_total");
        _httpRequestDuration = _meter.CreateHistogram<double>("http_request_duration_seconds", unit: "s");

        CreateObservableInstruments();
    }

    public void RecordProviderRefresh(string providerId, bool success, TimeSpan duration)
    {
        var tags = ProviderTags(providerId);
        _providerRefreshDuration.Record(duration.TotalSeconds, tags);
        _lastProviderRefreshTimestamps[providerId] = ToUnixSeconds(_timeProvider.GetUtcNow());
        if (!success)
            _providerRefreshFailures.Add(1, tags);
    }

    public void RecordProviderStreamLimitReached(string providerId)
        => _providerStreamLimitReached.Add(1, ProviderTags(providerId));

    public void RecordStreamStarted(string providerId, string? streamType = null, string? protocol = null)
        => _streamSessionsStarted.Add(1, StreamTags(providerId, streamType, protocol));

    public void RecordStreamEnded(string providerId, string? streamType = null, string? protocol = null)
        => _streamSessionsEnded.Add(1, StreamTags(providerId, streamType, protocol));

    public void RecordStreamReconnect(string providerId)
        => _streamReconnects.Add(1, ProviderTags(providerId));

    public void RecordStreamFailure(string providerId, string result = "failure")
        => _streamFailures.Add(1, ResultTags(providerId, result));

    public void RecordLineupPublish(string? profileId, int added, int removed, int renamed, int mappingChanges)
    {
        var tags = ProfileTags(profileId);
        if (added > 0) _lineupAddedChannels.Add(added, tags);
        if (removed > 0) _lineupRemovedChannels.Add(removed, tags);
        if (renamed > 0) _lineupRenamedChannels.Add(renamed, tags);
        if (mappingChanges > 0) _lineupMappingChanges.Add(mappingChanges, tags);
    }

    public void RecordEpgRefresh(bool success, int channels, int programs, int unmatchedChannels)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_epgGate)
        {
            _epgSnapshot = new EpgMetricSnapshot(channels, programs, unmatchedChannels, now);
        }

        if (!success)
            _epgRefreshFailures.Add(1);
    }

    public void RecordHdhrTuneRequest(bool success)
    {
        _hdhrTuneRequests.Add(1);
        if (!success)
            _hdhrTuneFailures.Add(1);
    }

    public void RecordHdhrDiscoveryRequest()
        => _hdhrDiscoveryRequests.Add(1);

    public void RecordHttpRequest(string route, string method, int statusCode, TimeSpan duration)
    {
        var tags = new TagList
        {
            { "route", string.IsNullOrWhiteSpace(route) ? "unknown" : route },
            { "method", method },
            { "status_code", statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        };
        _httpRequests.Add(1, tags);
        _httpRequestDuration.Record(duration.TotalSeconds, tags);
    }

    public void Dispose() => _meter.Dispose();

    private void CreateObservableInstruments()
    {
        _meter.CreateObservableGauge("m3undle_build_info", ObserveBuildInfo);
        _meter.CreateObservableGauge("m3undle_uptime_seconds", () => (_timeProvider.GetUtcNow() - _startedUtc).TotalSeconds, unit: "s");
        _meter.CreateObservableGauge("m3undle_provider_up", ObserveProviderUp);
        _meter.CreateObservableGauge("m3undle_provider_last_refresh_timestamp_seconds", ObserveProviderLastRefreshTimestamps, unit: "s");
        _meter.CreateObservableGauge("m3undle_provider_stream_limit", ObserveProviderStreamLimits);
        _meter.CreateObservableGauge("m3undle_stream_sessions_active", () => _streamingRegistry.GetActiveSessions().Count);
        _meter.CreateObservableGauge("m3undle_upstream_connections_active", () => _streamingRegistry.GetActiveProviderStreams().Count);
        _meter.CreateObservableGauge("m3undle_downstream_clients_active", () => _streamingRegistry.GetActiveClients().Count);
        _meter.CreateObservableGauge("m3undle_stream_share_ratio", ObserveStreamShareRatio);
        _meter.CreateObservableGauge("m3undle_lineup_channels_total", ObserveLineupChannelsTotal);
        _meter.CreateObservableGauge("m3undle_lineup_channels_enabled_total", ObserveEnabledChannelsTotal);
        _meter.CreateObservableGauge("m3undle_lineup_last_publish_timestamp_seconds", ObserveLineupLastPublishTimestamp, unit: "s");
        _meter.CreateObservableGauge("m3undle_epg_channels_total", () => SnapshotEpg().Channels);
        _meter.CreateObservableGauge("m3undle_epg_programs_total", () => SnapshotEpg().Programs);
        _meter.CreateObservableGauge("m3undle_epg_last_refresh_timestamp_seconds", () => SnapshotEpg().LastRefreshUtc is { } last ? ToUnixSeconds(last) : 0, unit: "s");
        _meter.CreateObservableGauge("m3undle_epg_age_seconds", ObserveEpgAgeSeconds, unit: "s");
        _meter.CreateObservableGauge("m3undle_epg_unmatched_channels_total", () => SnapshotEpg().UnmatchedChannels);
        _meter.CreateObservableGauge("m3undle_hdhr_tuners_total", () => _tunerCountResolver.ResolveTunerCount());
        _meter.CreateObservableGauge("m3undle_hdhr_tuners_in_use", ObserveHdhrTunersInUse);
    }

    private Measurement<int> ObserveBuildInfo()
        => new(1, new KeyValuePair<string, object?>("version", _buildInfo.Version),
            new KeyValuePair<string, object?>("build_number", _buildInfo.BuildNumber ?? "unknown"));

    private IEnumerable<Measurement<int>> ObserveProviderUp()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.Providers
            .AsNoTracking()
            .Select(p => new { p.ProviderId, p.Enabled })
            .ToArray()
            .Select(p => new Measurement<int>(p.Enabled ? 1 : 0, ProviderTags(p.ProviderId)))
            .ToArray();
    }

    private IEnumerable<Measurement<double>> ObserveProviderLastRefreshTimestamps()
        => _lastProviderRefreshTimestamps
            .Select(x => new Measurement<double>(x.Value, ProviderTags(x.Key)))
            .ToArray();

    private IEnumerable<Measurement<int>> ObserveProviderStreamLimits()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.Providers
            .AsNoTracking()
            .Where(p => p.MaxConcurrentStreams.HasValue)
            .Select(p => new { p.ProviderId, StreamLimit = p.MaxConcurrentStreams!.Value })
            .ToArray()
            .Select(p => new Measurement<int>(p.StreamLimit, ProviderTags(p.ProviderId)))
            .ToArray();
    }

    private double ObserveStreamShareRatio()
    {
        var upstreams = _streamingRegistry.GetActiveProviderStreams().Count;
        if (upstreams == 0)
            return 0;

        var clients = _streamingRegistry.GetActiveClients().Count;
        return clients / (double)upstreams;
    }

    private int ObserveLineupChannelsTotal()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.Snapshots
            .AsNoTracking()
            .Where(x => x.Status == "active")
            .OrderByDescending(x => x.CreatedUtc)
            .Select(x => x.ChannelCountPublished)
            .FirstOrDefault();
    }

    private int ObserveEnabledChannelsTotal()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.CanonicalChannels.AsNoTracking().Count(x => x.Enabled);
    }

    private double ObserveLineupLastPublishTimestamp()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var createdUtc = db.Snapshots
            .AsNoTracking()
            .Where(x => x.Status == "active")
            .OrderByDescending(x => x.CreatedUtc)
            .Select(x => (DateTime?)x.CreatedUtc)
            .FirstOrDefault();

        return createdUtc.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(createdUtc.Value, DateTimeKind.Utc)).ToUnixTimeSeconds()
            : 0;
    }

    private double ObserveEpgAgeSeconds()
    {
        var snapshot = SnapshotEpg();
        return snapshot.LastRefreshUtc is { } last
            ? Math.Max(0, (_timeProvider.GetUtcNow() - last).TotalSeconds)
            : 0;
    }

    private int ObserveHdhrTunersInUse()
    {
        using var scope = _scopeFactory.CreateScope();
        var tunerManager = scope.ServiceProvider.GetRequiredService<HdHomeRunTunerManager>();
        return tunerManager.GetActiveLeases().Count;
    }

    private EpgMetricSnapshot SnapshotEpg()
    {
        lock (_epgGate)
            return _epgSnapshot;
    }

    private static TagList ProviderTags(string? providerId)
    {
        var tags = new TagList();
        if (!string.IsNullOrWhiteSpace(providerId))
            tags.Add("provider_id", providerId);
        return tags;
    }

    private static TagList ProfileTags(string? profileId)
    {
        var tags = new TagList();
        if (!string.IsNullOrWhiteSpace(profileId))
            tags.Add("profile_id", profileId);
        return tags;
    }

    private static TagList ResultTags(string? providerId, string result)
    {
        var tags = ProviderTags(providerId);
        tags.Add("result", result);
        return tags;
    }

    private static TagList StreamTags(string? providerId, string? streamType, string? protocol)
    {
        var tags = ProviderTags(providerId);
        if (!string.IsNullOrWhiteSpace(streamType))
            tags.Add("stream_type", streamType);
        if (!string.IsNullOrWhiteSpace(protocol))
            tags.Add("protocol", protocol);
        return tags;
    }

    private static double ToUnixSeconds(DateTimeOffset value) => value.ToUnixTimeSeconds();

    private sealed record EpgMetricSnapshot(int Channels, int Programs, int UnmatchedChannels, DateTimeOffset? LastRefreshUtc);
}
