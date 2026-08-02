namespace M3Undle.Web.Observability.Resources;

public interface IResourceFactsProvider
{
    Task<ResourceFacts> GetSnapshotAsync(CancellationToken ct = default);
}
