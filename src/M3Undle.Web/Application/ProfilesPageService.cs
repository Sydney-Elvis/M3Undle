using M3Undle.Web.Contracts;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

internal sealed class ProfilesPageService(IServiceScopeFactory scopeFactory)
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

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var duplicate = await db.Profiles.AsNoTracking()
            .AnyAsync(x => x.Name == name.Trim(), ct);
        if (duplicate)
            return (false, $"A profile named '{name.Trim()}' already exists.", null);

        var now = DateTime.UtcNow;
        var profile = new Profile
        {
            ProfileId = Guid.NewGuid().ToString(),
            Name = name.Trim(),
            OutputName = "m3undle",
            MergeMode = "replace",
            Enabled = true,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.Profiles.Add(profile);
        await db.SaveChangesAsync(ct);

        return (true, null, new ProfileStubDto { ProfileId = profile.ProfileId, Name = profile.Name });
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

        var activeProviderIds = await db.Providers
            .AsNoTracking()
            .Where(p => p.IsActive)
            .Select(p => p.ProviderId)
            .ToListAsync(ct);

        var activeSnapshots = await db.Snapshots
            .AsNoTracking()
            .Where(s => profileIds.Contains(s.ProfileId) && s.Status == "active")
            .OrderByDescending(s => s.CreatedUtc)
            .ToListAsync(ct);

        var groupsPendingByProfile = await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup)
            .Where(x => profileIds.Contains(x.ProfileId) && x.ProviderGroup.ContentType == "live" && x.IsNew)
            .GroupBy(x => x.ProfileId)
            .Select(g => new { ProfileId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var lastFailedRun = await db.FetchRuns
            .AsNoTracking()
            .OrderByDescending(f => f.StartedUtc)
            .Select(f => new { f.Status })
            .FirstOrDefaultAsync(ct);

        var pendingByProfile = groupsPendingByProfile.ToDictionary(g => g.ProfileId, g => g.Count);

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
                    IsActive = activeProviderIds.Contains(pp.ProviderId),
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
                CreatedUtc = profile.CreatedUtc,
                Providers = providers,
                LastPublishedUtc = snapshot?.CreatedUtc,
                LiveCount = snapshot?.LiveChannelCount ?? 0,
                MovieCount = snapshot?.VodChannelCount ?? 0,
                SeriesCount = snapshot?.SeriesChannelCount ?? 0,
                HealthStatus = health,
                GroupsPendingReview = pendingByProfile.GetValueOrDefault(profile.ProfileId, 0),
            });
        }

        return result;
    }
}
