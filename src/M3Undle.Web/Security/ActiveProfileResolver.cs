using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Security;

internal sealed class ActiveProfileResolver(ApplicationDbContext db) : IProfileResolver
{
    public async ValueTask<string?> ResolveActiveProfileIdAsync(string? preferredProfileId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(preferredProfileId))
        {
            var preferredExists = await db.Profiles
                .AsNoTracking()
                .AnyAsync(x => x.ProfileId == preferredProfileId && x.Enabled, cancellationToken);
            // Hard-fail when a specific profile is bound but unavailable.
            // Remove this early return when per-credential multi-profile switching is implemented.
            return preferredExists ? preferredProfileId : null;
        }

        var activeProfileId = await db.Profiles
            .AsNoTracking()
            .Where(x => x.IsActive && x.Enabled)
            .Select(x => x.ProfileId)
            .FirstOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(activeProfileId) ? null : activeProfileId;
    }
}
