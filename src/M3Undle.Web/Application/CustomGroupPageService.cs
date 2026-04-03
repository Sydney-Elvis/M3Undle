using M3Undle.Web.Contracts;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

public sealed class CustomGroupPageService(IServiceScopeFactory scopeFactory, AppEventBus eventBus)
{
    public async Task<List<CustomGroupDto>> ListAsync(string profileId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var groups = await db.ProfileCustomGroups
            .AsNoTracking()
            .Include(x => x.Channels)
            .Include(x => x.ProviderLinks)
            .Where(x => x.ProfileId == profileId)
            .OrderBy(x => x.SortOverride == null ? 1 : 0)
            .ThenBy(x => x.SortOverride)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);

        return groups.Select(ToDto).ToList();
    }

    public async Task<CustomGroupDto?> CreateAsync(
        string profileId,
        string name,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        var profileExists = await db.Profiles
            .AsNoTracking()
            .AnyAsync(x => x.ProfileId == profileId, cancellationToken);

        if (!profileExists)
            return null;

        var now = DateTime.UtcNow;
        var group = new ProfileCustomGroup
        {
            CustomGroupId = Guid.NewGuid().ToString(),
            ProfileId = profileId,
            Name = trimmed,
            Decision = LineupReviewSemantics.GroupDecisionInclude,
            ChannelMode = LineupReviewSemantics.GroupModeManualReview,
            TrackingPolicy = LineupReviewSemantics.TrackingPolicyReview,
            TrackNewChannels = false,
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        db.ProfileCustomGroups.Add(group);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return null;
        }

        return ToDto(group);
    }

    public async Task<bool> UpdateAsync(
        string profileId,
        string customGroupId,
        UpdateCustomGroupRequest request,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var group = await db.ProfileCustomGroups
            .FirstOrDefaultAsync(x => x.CustomGroupId == customGroupId && x.ProfileId == profileId, cancellationToken);

        if (group is null)
            return false;

        if (request.Name is not null)
        {
            var trimmed = request.Name.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                group.Name = trimmed;
        }

        if (request.Decision is not null)
        {
            var d = request.Decision.Trim().ToLowerInvariant();
            if (d is "include" or "exclude" or "pending")
                group.Decision = d;
        }

        if (request.ChannelMode is not null)
            group.ChannelMode = LineupReviewSemantics.NormalizeGroupMode(request.ChannelMode);

        if (request.TrackingPolicy is not null)
            group.TrackingPolicy = LineupReviewSemantics.NormalizeTrackingPolicy(request.TrackingPolicy);

        if (request.ClearTrackingKeywords)
            group.TrackingKeywords = null;
        else if (request.TrackingKeywords is not null)
            group.TrackingKeywords = string.IsNullOrWhiteSpace(request.TrackingKeywords) ? null : request.TrackingKeywords.Trim();

        if (request.ClearAutoNum)
        {
            group.AutoNumStart = null;
            group.AutoNumEnd = null;
        }
        else
        {
            if (request.AutoNumStart is not null)
                group.AutoNumStart = request.AutoNumStart;
            if (request.ClearAutoNumEnd)
                group.AutoNumEnd = null;
            else if (request.AutoNumEnd is not null)
                group.AutoNumEnd = request.AutoNumEnd;
        }

        if (request.TrackNewChannels is not null)
            group.TrackNewChannels = request.TrackNewChannels.Value;

        if (request.SortOverride is not null)
            group.SortOverride = request.SortOverride;

        group.UpdatedUtc = DateTime.UtcNow;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return false;
        }

        eventBus.Publish(AppEventKind.GroupFiltersChanged);
        return true;
    }

    public async Task<bool> DeleteAsync(string profileId, string customGroupId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var count = await db.ProfileCustomGroups
            .Where(x => x.CustomGroupId == customGroupId && x.ProfileId == profileId)
            .ExecuteDeleteAsync(cancellationToken);

        if (count > 0)
            eventBus.Publish(AppEventKind.GroupFiltersChanged);

        return count > 0;
    }

    // -------------------------------------------------------------------------
    // Channels
    // -------------------------------------------------------------------------

    public async Task<List<CustomGroupChannelDto>> ListChannelsAsync(
        string profileId,
        string customGroupId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var group = await db.ProfileCustomGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.CustomGroupId == customGroupId && x.ProfileId == profileId, cancellationToken);

        if (group is null)
            return [];

        var rows = await db.ProfileCustomGroupChannels
            .AsNoTracking()
            .Include(x => x.ProviderChannel).ThenInclude(c => c.Provider)
            .Where(x => x.CustomGroupId == customGroupId)
            .OrderBy(x => x.ChannelNumber == null ? 1 : 0)
            .ThenBy(x => x.ChannelNumber)
            .ThenBy(x => x.ProviderChannel.DisplayName)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new CustomGroupChannelDto
        {
            CustomGroupChannelId = r.CustomGroupChannelId,
            ProviderChannelId = r.ProviderChannelId,
            DisplayName = r.ProviderChannel.DisplayName,
            TvgId = r.ProviderChannel.TvgId,
            GroupTitle = r.ProviderChannel.GroupTitle,
            ProviderName = r.ProviderChannel.Provider.Name,
            Active = r.ProviderChannel.Active,
            State = r.State,
            ChannelNumber = r.ChannelNumber,
            DisplayNameOverride = r.DisplayNameOverride,
            TvgIdOverride = r.TvgIdOverride,
        }).ToList();
    }

    public async Task<int> AddChannelsAsync(
        string profileId,
        string customGroupId,
        List<string> providerChannelIds,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var group = await db.ProfileCustomGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.CustomGroupId == customGroupId && x.ProfileId == profileId, cancellationToken);

        if (group is null)
            return 0;

        var existingIds = await db.ProfileCustomGroupChannels
            .AsNoTracking()
            .Where(x => x.CustomGroupId == customGroupId)
            .Select(x => x.ProviderChannelId)
            .ToHashSetAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var toAdd = providerChannelIds
            .Where(id => !existingIds.Contains(id))
            .Distinct()
            .Select(id => new ProfileCustomGroupChannel
            {
                CustomGroupChannelId = Guid.NewGuid().ToString(),
                CustomGroupId = customGroupId,
                ProviderChannelId = id,
                State = LineupReviewSemantics.ChannelStateIncluded,
                CreatedUtc = now,
                UpdatedUtc = now,
            })
            .ToList();

        if (toAdd.Count == 0)
            return 0;

        db.ProfileCustomGroupChannels.AddRange(toAdd);
        await db.SaveChangesAsync(cancellationToken);
        return toAdd.Count;
    }

    public async Task<bool> RemoveChannelAsync(
        string profileId,
        string customGroupId,
        string providerChannelId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var group = await db.ProfileCustomGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.CustomGroupId == customGroupId && x.ProfileId == profileId, cancellationToken);

        if (group is null)
            return false;

        var count = await db.ProfileCustomGroupChannels
            .Where(x => x.CustomGroupId == customGroupId && x.ProviderChannelId == providerChannelId)
            .ExecuteDeleteAsync(cancellationToken);

        return count > 0;
    }

    public async Task<bool> UpdateChannelAsync(
        string profileId,
        string customGroupId,
        string providerChannelId,
        UpdateCustomGroupChannelRequest request,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var group = await db.ProfileCustomGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.CustomGroupId == customGroupId && x.ProfileId == profileId, cancellationToken);

        if (group is null)
            return false;

        var row = await db.ProfileCustomGroupChannels
            .FirstOrDefaultAsync(x => x.CustomGroupId == customGroupId && x.ProviderChannelId == providerChannelId, cancellationToken);

        if (row is null)
            return false;

        if (request.State is not null)
            row.State = LineupReviewSemantics.NormalizeChannelState(request.State);

        if (request.ClearChannelNumber)
            row.ChannelNumber = null;
        else if (request.ChannelNumber is not null)
            row.ChannelNumber = request.ChannelNumber;

        if (request.ClearDisplayNameOverride)
            row.DisplayNameOverride = null;
        else if (request.DisplayNameOverride is not null)
            row.DisplayNameOverride = string.IsNullOrWhiteSpace(request.DisplayNameOverride) ? null : request.DisplayNameOverride.Trim();

        if (request.ClearTvgIdOverride)
            row.TvgIdOverride = null;
        else if (request.TvgIdOverride is not null)
            row.TvgIdOverride = string.IsNullOrWhiteSpace(request.TvgIdOverride) ? null : request.TvgIdOverride.Trim();

        row.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // -------------------------------------------------------------------------
    // Provider links
    // -------------------------------------------------------------------------

    public async Task<List<CustomGroupProviderLinkDto>> ListProviderLinksAsync(
        string profileId,
        string customGroupId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var group = await db.ProfileCustomGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.CustomGroupId == customGroupId && x.ProfileId == profileId, cancellationToken);

        if (group is null)
            return [];

        var links = await db.ProfileCustomGroupProviderLinks
            .AsNoTracking()
            .Include(x => x.ProviderGroup).ThenInclude(g => g.Provider)
            .Where(x => x.CustomGroupId == customGroupId)
            .OrderBy(x => x.ProviderGroup.RawName)
            .ToListAsync(cancellationToken);

        return links.Select(l => new CustomGroupProviderLinkDto
        {
            LinkId = l.LinkId,
            ProviderGroupId = l.ProviderGroupId,
            ProviderGroupRawName = l.ProviderGroup.RawName,
            ProviderName = l.ProviderGroup.Provider.Name,
            ChannelCount = l.ProviderGroup.ChannelCount,
            CreatedUtc = l.CreatedUtc,
        }).ToList();
    }

    public async Task<bool> AddProviderLinkAsync(
        string profileId,
        string customGroupId,
        string providerGroupId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var group = await db.ProfileCustomGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.CustomGroupId == customGroupId && x.ProfileId == profileId, cancellationToken);

        if (group is null)
            return false;

        var alreadyLinked = await db.ProfileCustomGroupProviderLinks
            .AsNoTracking()
            .AnyAsync(x => x.CustomGroupId == customGroupId && x.ProviderGroupId == providerGroupId, cancellationToken);

        if (alreadyLinked)
            return true;

        var providerGroupExists = await db.ProviderGroups
            .AsNoTracking()
            .AnyAsync(x => x.ProviderGroupId == providerGroupId, cancellationToken);

        if (!providerGroupExists)
            return false;

        db.ProfileCustomGroupProviderLinks.Add(new ProfileCustomGroupProviderLink
        {
            LinkId = Guid.NewGuid().ToString(),
            CustomGroupId = customGroupId,
            ProviderGroupId = providerGroupId,
            CreatedUtc = DateTime.UtcNow,
        });

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RemoveProviderLinkAsync(
        string profileId,
        string customGroupId,
        string providerGroupId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var group = await db.ProfileCustomGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.CustomGroupId == customGroupId && x.ProfileId == profileId, cancellationToken);

        if (group is null)
            return false;

        var count = await db.ProfileCustomGroupProviderLinks
            .Where(x => x.CustomGroupId == customGroupId && x.ProviderGroupId == providerGroupId)
            .ExecuteDeleteAsync(cancellationToken);

        return count > 0;
    }

    // -------------------------------------------------------------------------
    // Pending review sync (called from SnapshotBuilder after provider sync)
    // -------------------------------------------------------------------------

    public async Task SyncPendingChannelReviewsAsync(
        string profileId,
        string providerId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Only custom groups that are included and have at least one provider link for this provider
        var candidateGroups = await db.ProfileCustomGroups
            .AsNoTracking()
            .Include(x => x.ProviderLinks).ThenInclude(l => l.ProviderGroup)
            .Where(x => x.ProfileId == profileId
                        && x.Decision == LineupReviewSemantics.GroupDecisionInclude)
            .ToListAsync(cancellationToken);

        foreach (var group in candidateGroups)
        {
            var linkedGroupIds = group.ProviderLinks
                .Where(l => l.ProviderGroup.ProviderId == providerId)
                .Select(l => l.ProviderGroupId)
                .ToList();

            if (linkedGroupIds.Count == 0)
                continue;

            var activeChannels = await db.ProviderChannels
                .AsNoTracking()
                .Where(x => x.ProviderId == providerId
                            && x.Active
                            && x.ContentType == "live"
                            && !x.IsPlaceholder
                            && x.ProviderGroupId != null
                            && linkedGroupIds.Contains(x.ProviderGroupId!))
                .ToListAsync(cancellationToken);

            if (activeChannels.Count == 0)
                continue;

            var activeIds = activeChannels.Select(x => x.ProviderChannelId).ToList();

            var existing = await db.ProfileCustomGroupChannels
                .AsNoTracking()
                .Where(x => x.CustomGroupId == group.CustomGroupId
                            && activeIds.Contains(x.ProviderChannelId))
                .Select(x => x.ProviderChannelId)
                .ToHashSetAsync(cancellationToken);

            var newChannels = activeChannels.Where(ch => !existing.Contains(ch.ProviderChannelId)).ToList();
            if (newChannels.Count == 0)
                continue;

            var isManualReview = LineupReviewSemantics.NormalizeGroupMode(group.ChannelMode) == LineupReviewSemantics.GroupModeManualReview;

            var newRows = newChannels.Select(ch => new ProfileCustomGroupChannel
            {
                CustomGroupChannelId = Guid.NewGuid().ToString(),
                CustomGroupId = group.CustomGroupId,
                ProviderChannelId = ch.ProviderChannelId,
                State = isManualReview
                    ? LineupReviewSemantics.ChannelStatePending
                    : EvaluateTrackingPolicy(group, ch),
                CreatedUtc = now,
                UpdatedUtc = now,
            }).ToList();

            db.ProfileCustomGroupChannels.AddRange(newRows);
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string EvaluateTrackingPolicy(ProfileCustomGroup group, ProviderChannel ch)
    {
        if (!ch.IsEvent && string.IsNullOrWhiteSpace(ch.EventContentKey))
            return LineupReviewSemantics.ChannelStateIncluded;

        if (LineupReviewSemantics.ShouldAutoAddAll(group.TrackingPolicy))
            return LineupReviewSemantics.ChannelStateIncluded;

        if (LineupReviewSemantics.ShouldAutoAddPopulated(group.TrackingPolicy))
            return !string.IsNullOrWhiteSpace(ch.EventContentKey)
                ? LineupReviewSemantics.ChannelStateIncluded
                : LineupReviewSemantics.ChannelStatePending;

        if (LineupReviewSemantics.ShouldAutoAddMatching(group.TrackingPolicy))
            return LineupReviewSemantics.MatchesTrackingKeywords(group.TrackingKeywords, ch.DisplayName, ch.GroupTitle, ch.EventContentKey)
                ? LineupReviewSemantics.ChannelStateIncluded
                : LineupReviewSemantics.ChannelStatePending;

        return LineupReviewSemantics.ChannelStatePending;
    }

    private static CustomGroupDto ToDto(ProfileCustomGroup g) => new()
    {
        CustomGroupId = g.CustomGroupId,
        ProfileId = g.ProfileId,
        Name = g.Name,
        Decision = g.Decision,
        ChannelMode = g.ChannelMode,
        TrackingPolicy = g.TrackingPolicy,
        TrackingKeywords = g.TrackingKeywords,
        AutoNumStart = g.AutoNumStart,
        AutoNumEnd = g.AutoNumEnd,
        TrackNewChannels = g.TrackNewChannels,
        SortOverride = g.SortOverride,
        ChannelCount = g.Channels.Count,
        SelectedChannelCount = g.Channels.Count(c => c.State == LineupReviewSemantics.ChannelStateIncluded),
        ProviderLinkCount = g.ProviderLinks.Count,
        CreatedUtc = g.CreatedUtc,
        UpdatedUtc = g.UpdatedUtc,
    };
}
