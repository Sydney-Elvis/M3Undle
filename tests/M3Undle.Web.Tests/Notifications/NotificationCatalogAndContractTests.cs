using System.Reflection;
using M3Undle.Web.Application;
using M3Undle.Web.Application.Notifications;
using M3Undle.Web.Data.Entities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Notifications;

[TestClass]
public sealed class NotificationCatalogAndContractTests
{
    [TestMethod]
    public void Catalog_KeysAreUniqueAndStable()
    {
        var keys = NotificationCatalog.Definitions.Select(d => d.Key).ToList();
        Assert.HasCount(12, keys);
        Assert.AreEqual(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(keys.All(k => k == k.ToLowerInvariant() && k.Contains('.')));
    }

    [TestMethod]
    public void Catalog_AccountsForEveryExistingSystemEventTypeExactlyOnce()
    {
        var eventTypes = typeof(SystemEventTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        var mapped = NotificationCatalog.Definitions.SelectMany(d => d.LegacyEventTypes).ToList();

        Assert.HasCount(15, eventTypes);
        CollectionAssert.AreEquivalent(eventTypes, mapped, "Every existing event type must map to exactly one catalog row.");
    }

    [TestMethod]
    public void Catalog_IncidentRowsHaveRecoveryAndOneTimeRowsDoNot()
    {
        foreach (var d in NotificationCatalog.Definitions)
        {
            Assert.AreEqual(d.Lifecycle == NotificationLifecycle.Incident, d.SupportsRecovery, d.Key);
            Assert.AreEqual(d.Lifecycle == NotificationLifecycle.Incident, d.SupportsReminders, d.Key);
        }
    }

    [TestMethod]
    public void Catalog_EveryRowIsAvailable_NowThatEveryProducerIsImplementedAndTested()
    {
        // A row flips to available only in the package that implements and tests its producer. Add a new row as unavailable first.
        Assert.IsTrue(NotificationCatalog.Definitions.All(d => d.ProducerAvailable));
    }

    [TestMethod]
    public void Catalog_StreamInstabilityThresholdsAreCatalogPolicy_NotTransportLogic()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(2), NotificationCatalog.Find(NotificationKeys.StreamUnstable)!.FixedSustainedDelay);
        Assert.IsTrue(NotificationCatalog.Definitions.Where(d => d.Key != NotificationKeys.StreamUnstable).All(d => d.FixedSustainedDelay is null));
    }

    [TestMethod]
    public void Catalog_UnknownKeysAreRejected()
    {
        Assert.IsFalse(NotificationCatalog.IsKnown("matrix.message"));
        Assert.IsNull(NotificationCatalog.Find(null));
        Assert.IsTrue(NotificationCatalog.IsKnown(NotificationKeys.EpgFetchFailed));
    }

    [TestMethod]
    public void Registry_ResolvesProvidersByKind_ThirdProviderNeedsOnlyRegistration()
    {
        var registry = new NotificationProviderRegistry(
            [new FakeProvider(NotificationProviderKinds.Matrix), new FakeProvider(NotificationProviderKinds.Smtp), new FakeProvider("pushover")]);

        Assert.IsNotNull(registry.Find("pushover"));
        Assert.IsNull(registry.Find("sms"));
        Assert.HasCount(3, registry.Kinds);
    }

    [TestMethod]
    public void Registry_RejectsDuplicateKinds()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new NotificationProviderRegistry([new FakeProvider("smtp"), new FakeProvider("smtp")]));
    }

    [TestMethod]
    public void Secret_NeverPrintsOrSerializes()
    {
        var secret = new NotificationSecret("hunter2");
        Assert.AreEqual("[redacted]", secret.ToString());
        Assert.DoesNotContain("hunter2", System.Text.Json.JsonSerializer.Serialize(secret));
        var config = new SmtpProviderConfiguration("h", 587, "starttls", "password", "u", secret, "a@b.c", null);
        Assert.DoesNotContain("hunter2", config.ToString());
    }

    private sealed class FakeProvider(string kind) : INotificationProvider
    {
        public string Kind => kind;
        public Task<NotificationSendResult> SendAsync(NotificationSendRequest request, CancellationToken cancellationToken)
            => Task.FromResult(NotificationSendResult.Accepted());
    }
}
