using System.Text.Json;
using M3Undle.Core.Providers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Core.Tests.Providers;

[TestClass]
public sealed class ProviderRequestHeadersTests
{
    [TestMethod]
    public void ParseJson_Null_ReturnsEmpty()
    {
        var headers = ProviderRequestHeaders.ParseJson(null);

        Assert.IsEmpty(headers);
    }

    [TestMethod]
    public void ParseJson_EmptyString_ReturnsEmpty()
    {
        var headers = ProviderRequestHeaders.ParseJson("");

        Assert.IsEmpty(headers);
    }

    [TestMethod]
    public void ParseJson_ArrayJson_ReturnsEmpty()
    {
        var headers = ProviderRequestHeaders.ParseJson("[\"invalid\"]");

        Assert.IsEmpty(headers);
    }

    [TestMethod]
    public void ParseJson_ValidJson_ReturnsStringHeaders()
    {
        var headers = ProviderRequestHeaders.ParseJson("{\"X-Custom-Header\":\"test-value\"}");

        Assert.HasCount(1, headers);
        Assert.AreEqual("X-Custom-Header", headers[0].Name);
        Assert.AreEqual("test-value", headers[0].Value);
    }

    [TestMethod]
    public void ParseJson_MultipleHeaders_ReturnsAllStringHeaders()
    {
        var headers = ProviderRequestHeaders.ParseJson("{\"X-Api-Key\":\"key1\",\"X-Version\":\"2\"}");

        Assert.HasCount(2, headers);
        Assert.AreEqual("X-Api-Key", headers[0].Name);
        Assert.AreEqual("key1", headers[0].Value);
        Assert.AreEqual("X-Version", headers[1].Name);
        Assert.AreEqual("2", headers[1].Value);
    }

    [TestMethod]
    public void ParseJson_NonStringValues_AreSkipped()
    {
        var headers = ProviderRequestHeaders.ParseJson("{\"X-Api-Key\":\"key1\",\"X-Version\":2}");

        Assert.HasCount(1, headers);
        Assert.AreEqual("X-Api-Key", headers[0].Name);
    }

    [TestMethod]
    public void ParseJson_WhitespaceValues_AreSkipped()
    {
        var headers = ProviderRequestHeaders.ParseJson("{\"X-Api-Key\":\"   \",\"X-Version\":\"2\"}");

        Assert.HasCount(1, headers);
        Assert.AreEqual("X-Version", headers[0].Name);
    }

    [TestMethod]
    public void ParseJson_InvalidJson_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => ProviderRequestHeaders.ParseJson("{invalid"));
    }

    [TestMethod]
    public void ApplyTo_ValidJson_SetsHeader()
    {
        using var client = new HttpClient();

        ProviderRequestHeaders.ApplyTo(client, "{\"X-Custom-Header\":\"test-value\"}");

        Assert.IsTrue(client.DefaultRequestHeaders.Contains("X-Custom-Header"));
        Assert.AreEqual("test-value", client.DefaultRequestHeaders.GetValues("X-Custom-Header").Single());
    }

    [TestMethod]
    public void ApplyTo_ReplacesExistingHeader()
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Custom-Header", "old-value");

        ProviderRequestHeaders.ApplyTo(client, "{\"X-Custom-Header\":\"new-value\"}");

        Assert.AreEqual("new-value", client.DefaultRequestHeaders.GetValues("X-Custom-Header").Single());
    }
}
