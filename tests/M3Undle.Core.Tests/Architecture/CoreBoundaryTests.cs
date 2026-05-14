using M3Undle.Core.M3u;

namespace M3Undle.Core.Tests.Architecture;

[TestClass]
public sealed class CoreBoundaryTests
{
    [TestMethod]
    public void CoreAssembly_DoesNotReferenceProductAdaptersOrRuntimeFrameworks()
    {
        var references = typeof(PlaylistParser).Assembly
            .GetReferencedAssemblies()
            .Select(static name => name.Name ?? string.Empty)
            .ToArray();

        var forbiddenPrefixes = new[]
        {
            "M3Undle.Web",
            "M3Undle.Cli",
            "Microsoft.AspNetCore",
            "Microsoft.EntityFrameworkCore",
            "MudBlazor",
            "Spectre.Console",
        };

        var forbiddenReferences = references
            .Where(reference => forbiddenPrefixes.Any(prefix =>
                reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(static reference => reference, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), forbiddenReferences);
    }
}
