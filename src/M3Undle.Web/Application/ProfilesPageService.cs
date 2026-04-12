using M3Undle.Web.Contracts;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

internal sealed class ProfilesPageService(
    IServiceScopeFactory scopeFactory,
    IRefreshTrigger refreshTrigger,
    AppEventBus eventBus)
{
    public async Task<List<ProfileStubDto>> GetProfileStubsAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Profiles
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(p => new ProfileStubDto { ProfileId = p.ProfileId, Name = p.Name })
            .ToListAsync(ct);
    }

    public async Task<(bool Success, string? Error, ProfileStubDto? Created)> CreateProfileAsync(
        string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            return (false, "Name is required.", null);

        var trimmed = name.Trim();
        if (trimmed.Length > 200)
            return (false, "Name must be 200 characters or fewer.", null);

        if (trimmed.Any(c => char.IsControl(c)))
            return (false, "Name must not contain control characters.", null);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var duplicate = await db.Profiles.AsNoTracking()
            .AnyAsync(x => x.Name == trimmed, ct);
        if (duplicate)
            return (false, $"A profile named '{trimmed}' already exists.", null);

        var now = DateTime.UtcNow;
        var profile = new Profile
        {
            ProfileId = Guid.NewGuid().ToString(),
            Name = trimmed,
            OutputName = "m3undle",
            MergeMode = "replace",
            Enabled = true,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.Profiles.Add(profile);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return (false, $"A profile named '{trimmed}' already exists.", null);
        }

        return (true, null, new ProfileStubDto { ProfileId = profile.ProfileId, Name = profile.Name });
    }

    public async Task<string?> DeleteProfileAsync(string profileId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profile = await db.Profiles.SingleOrDefaultAsync(x => x.ProfileId == profileId, ct);
        if (profile is null)
            return "Profile not found.";

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Cascade delete all profile-scoped data
        await db.EpgChannelMaps.Where(x => x.ProfileId == profileId).ExecuteDeleteAsync(ct);
        await db.ChannelMatchRules.Where(x => x.ProfileId == profileId).ExecuteDeleteAsync(ct);
        await db.CanonicalChannels.Where(x => x.ProfileId == profileId).ExecuteDeleteAsync(ct);
        await db.StreamKeys.Where(x => x.ProfileId == profileId).ExecuteDeleteAsync(ct);

        var filterIds = await db.ProfileGroupFilters
            .Where(x => x.ProfileId == profileId)
            .Select(x => x.ProfileGroupFilterId)
            .ToListAsync(ct);
        if (filterIds.Count > 0)
            await db.ProfileGroupChannelFilters.Where(x => filterIds.Contains(x.ProfileGroupFilterId)).ExecuteDeleteAsync(ct);
        await db.ProfileGroupFilters.Where(x => x.ProfileId == profileId).ExecuteDeleteAsync(ct);

        await db.Snapshots.Where(x => x.ProfileId == profileId).ExecuteDeleteAsync(ct);
        await db.ProfileProviders.Where(x => x.ProfileId == profileId).ExecuteDeleteAsync(ct);

        db.Profiles.Remove(profile);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return null;
    }

    public async Task<string?> SetProfileEnabledAsync(string profileId, bool enabled, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profile = await db.Profiles.SingleOrDefaultAsync(x => x.ProfileId == profileId, ct);
        if (profile is null)
            return "Profile not found.";

        profile.Enabled = enabled;
        profile.UpdatedUtc = DateTime.UtcNow;

        // Disabling the active profile also deactivates it so the system never
        // serves content through a disabled profile.
        if (!enabled && profile.IsActive)
            profile.IsActive = false;

        await db.SaveChangesAsync(ct);
        eventBus.Publish(AppEventKind.ProviderChanged);

        return null;
    }

    public async Task<string?> SetProfileActiveAsync(string profileId, CancellationToken ct)
    {
        if (refreshTrigger.IsRefreshing)
            return "A snapshot refresh is currently in progress. Please wait for it to finish.";

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var target = await db.Profiles
            .AsNoTracking()
            .Where(x => x.ProfileId == profileId)
            .Select(x => new { x.Enabled })
            .SingleOrDefaultAsync(ct);

        if (target is null)
            return "Profile not found.";
        if (!target.Enabled)
            return "Disabled profiles cannot be activated.";

        var now = DateTime.UtcNow;

        await db.Profiles
            .Where(x => x.IsActive && x.ProfileId != profileId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.IsActive, false)
                .SetProperty(p => p.UpdatedUtc, now), ct);

        await db.Profiles
            .Where(x => x.ProfileId == profileId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.IsActive, true)
                .SetProperty(p => p.UpdatedUtc, now), ct);

        eventBus.Publish(AppEventKind.ProviderActivated);
        refreshTrigger.TriggerRefresh();

        return null;
    }

    public async Task AutoActivateProfileIfNoneAsync(string profileId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var hasActive = await db.Profiles.AsNoTracking().AnyAsync(x => x.IsActive, ct);
        if (hasActive)
            return;

        var now = DateTime.UtcNow;
        await db.Profiles
            .Where(x => x.ProfileId == profileId && x.Enabled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.IsActive, true)
                .SetProperty(p => p.UpdatedUtc, now), ct);

        eventBus.Publish(AppEventKind.ProviderActivated);
        refreshTrigger.TriggerRefresh();
    }

    public async Task<List<ProfilePageItemDto>> GetProfilesAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await BuildProfileListAsync(db, profileId: null, ct);
    }

    public async Task<ProfileDetailDto?> GetProfileDetailAsync(string profileId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var list = await BuildProfileListAsync(db, profileId, ct);
        if (list.Count == 0) return null;

        var history = await db.Snapshots
            .AsNoTracking()
            .Where(s => s.ProfileId == profileId)
            .OrderByDescending(s => s.CreatedUtc)
            .Take(20)
            .Select(s => new ProfileSnapshotHistoryDto
            {
                SnapshotId = s.SnapshotId,
                CreatedUtc = s.CreatedUtc,
                Status = s.Status,
                ChannelCountPublished = s.ChannelCountPublished,
                LiveChannelCount = s.LiveChannelCount,
                VodChannelCount = s.VodChannelCount,
                SeriesChannelCount = s.SeriesChannelCount,
                ErrorSummary = s.ErrorSummary,
            })
            .ToListAsync(ct);

        return new ProfileDetailDto
        {
            Profile = list[0],
            History = history,
        };
    }

    private static async Task<List<ProfilePageItemDto>> BuildProfileListAsync(
        ApplicationDbContext db, string? profileId, CancellationToken ct)
    {
        var profileQuery = db.Profiles.AsNoTracking();
        if (profileId is not null)
            profileQuery = profileQuery.Where(p => p.ProfileId == profileId);

        var profiles = await profileQuery
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        if (profiles.Count == 0) return [];

        var profileIds = profiles.Select(p => p.ProfileId).ToList();

        var profileProviders = await db.ProfileProviders
            .AsNoTracking()
            .Include(pp => pp.Provider)
            .Where(pp => profileIds.Contains(pp.ProfileId))
            .OrderBy(pp => pp.Priority)
            .ToListAsync(ct);

        var activeSnapshots = await db.Snapshots
            .AsNoTracking()
            .Where(s => profileIds.Contains(s.ProfileId) && s.Status == "active")
            .OrderByDescending(s => s.CreatedUtc)
            .ToListAsync(ct);

        var groupsPendingByProfile = await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup)
            .Where(x => profileIds.Contains(x.ProfileId)
                        && x.ProviderGroup.ContentType == "live"
                        && x.IsNew)
            .GroupBy(x => x.ProfileId)
            .Select(g => new { ProfileId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var channelsPendingByProfile = await db.ProfileGroupChannelFilters
            .AsNoTracking()
            .Include(x => x.ProfileGroupFilter).ThenInclude(f => f.ProviderGroup)
            .Include(x => x.ProviderChannel)
            .Where(x => profileIds.Contains(x.ProfileGroupFilter.ProfileId)
                        && x.ProfileGroupFilter.ProviderGroup.ContentType == "live"
                        && x.ProfileGroupFilter.TrackingPolicy == LineupReviewSemantics.TrackingPolicyReview
                        && x.ProviderChannel.ContentType == "live"
                        && x.ProviderChannel.Active
                        && !x.ProviderChannel.IsPlaceholder
                        && x.State == LineupReviewSemantics.ChannelStatePending)
            .GroupBy(x => x.ProfileGroupFilter.ProfileId)
            .Select(g => new { ProfileId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var lastFailedRun = await db.FetchRuns
            .AsNoTracking()
            .OrderByDescending(f => f.StartedUtc)
            .Select(f => new { f.Status })
            .FirstOrDefaultAsync(ct);

        var pendingByProfile = groupsPendingByProfile.ToDictionary(g => g.ProfileId, g => g.Count);
        var pendingChannelsByProfileMap = channelsPendingByProfile.ToDictionary(g => g.ProfileId, g => g.Count);

        var result = new List<ProfilePageItemDto>();

        foreach (var profile in profiles)
        {
            var snapshot = activeSnapshots
                .FirstOrDefault(s => s.ProfileId == profile.ProfileId);

            var providers = profileProviders
                .Where(pp => pp.ProfileId == profile.ProfileId)
                .Select(pp => new ProfileProviderInfoDto
                {
                    ProviderId = pp.ProviderId,
                    Name = pp.Provider.Name,
                    Priority = pp.Priority,
                    Enabled = pp.Enabled,
                })
                .ToList();

            var health = ProfileHealthStatus.NoOutput;
            if (snapshot is not null)
            {
                health = lastFailedRun?.Status == "fail"
                    ? ProfileHealthStatus.Degraded
                    : ProfileHealthStatus.Ok;
            }

            result.Add(new ProfilePageItemDto
            {
                ProfileId = profile.ProfileId,
                Name = profile.Name,
                OutputName = profile.OutputName,
                MergeMode = profile.MergeMode,
                Enabled = profile.Enabled,
                IsActive = profile.IsActive,
                CreatedUtc = profile.CreatedUtc,
                Providers = providers,
                LastPublishedUtc = snapshot?.CreatedUtc,
                LiveCount = snapshot?.LiveChannelCount ?? 0,
                MovieCount = snapshot?.VodChannelCount ?? 0,
                SeriesCount = snapshot?.SeriesChannelCount ?? 0,
                HealthStatus = health,
                GroupsPendingReview = pendingByProfile.GetValueOrDefault(profile.ProfileId, 0),
                ChannelsPendingReview = pendingChannelsByProfileMap.GetValueOrDefault(profile.ProfileId, 0),
            });
        }

        return result;
    }
}
