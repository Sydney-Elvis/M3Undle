using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using M3Undle.Web.Data.Entities;

namespace M3Undle.Web.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<ProfileProvider> ProfileProviders => Set<ProfileProvider>();
    public DbSet<FetchRun> FetchRuns => Set<FetchRun>();
    public DbSet<ProviderGroup> ProviderGroups => Set<ProviderGroup>();
    public DbSet<ProviderChannel> ProviderChannels => Set<ProviderChannel>();
    public DbSet<CanonicalChannel> CanonicalChannels => Set<CanonicalChannel>();
    public DbSet<ChannelSource> ChannelSources => Set<ChannelSource>();
    public DbSet<ChannelMatchRule> ChannelMatchRules => Set<ChannelMatchRule>();
    public DbSet<EpgChannelMap> EpgChannelMaps => Set<EpgChannelMap>();
    public DbSet<Snapshot> Snapshots => Set<Snapshot>();
    public DbSet<StreamKey> StreamKeys => Set<StreamKey>();
    public DbSet<SiteSettings> SiteSettings => Set<SiteSettings>();
    public DbSet<EndpointCredential> EndpointCredentials => Set<EndpointCredential>();
    public DbSet<EndpointAccessBinding> EndpointAccessBindings => Set<EndpointAccessBinding>();
    public DbSet<ProfileGroupFilter> ProfileGroupFilters => Set<ProfileGroupFilter>();
    public DbSet<ProfileGroupChannelFilter> ProfileGroupChannelFilters => Set<ProfileGroupChannelFilter>();
    public DbSet<EpgSource> EpgSources => Set<EpgSource>();
    public DbSet<EpgSourceChannel> EpgSourceChannels => Set<EpgSourceChannel>();
    public DbSet<EpgChannelMapping> EpgChannelMappings => Set<EpgChannelMapping>();
    public DbSet<EpgFetchRun> EpgFetchRuns => Set<EpgFetchRun>();
    public DbSet<ProfileCustomGroup> ProfileCustomGroups => Set<ProfileCustomGroup>();
    public DbSet<ProfileCustomGroupChannel> ProfileCustomGroupChannels => Set<ProfileCustomGroupChannel>();
    public DbSet<ProfileCustomGroupProviderLink> ProfileCustomGroupProviderLinks => Set<ProfileCustomGroupProviderLink>();
    public DbSet<DownstreamIntegration> DownstreamIntegrations => Set<DownstreamIntegration>();
    public DbSet<ProfileEventInterestRule> ProfileEventInterestRules => Set<ProfileEventInterestRule>();
    public DbSet<SystemEvent> SystemEvents => Set<SystemEvent>();
    public DbSet<MetricsToken> MetricsTokens => Set<MetricsToken>();
    public DbSet<StreamChannelHealthEvent> StreamChannelHealthEvents => Set<StreamChannelHealthEvent>();
    public DbSet<XtreamSeriesCache> XtreamSeriesCache => Set<XtreamSeriesCache>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        NormalizeProviderChannelKeys();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        NormalizeProviderChannelKeys();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void NormalizeProviderChannelKeys()
    {
        foreach (var entry in ChangeTracker.Entries<ProviderChannel>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Entity.ProviderChannelKey))
            {
                entry.Entity.ProviderChannelKey = null;
            }
        }
    }
}

