using M3Undle.Web.Application;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Application;

[TestClass]
public sealed class EventChannelClassifierTests
{
    [TestMethod]
    public void Classify_WhenChannelIsNotEventLike_ReturnsNonEvent()
    {
        var result = EventChannelClassifier.Classify("Discovery Channel", "Entertainment");

        Assert.IsFalse(result.IsEvent);
        Assert.IsFalse(result.IsPlaceholder);
        Assert.IsNull(result.EventSlotKey);
        Assert.IsNull(result.EventContentKey);
    }

    [TestMethod]
    public void Classify_WhenPpvPipePlaceholder_ReturnsPlaceholderWithSlot()
    {
        var result = EventChannelClassifier.Classify("PPV 2 |", "Sports PPV");

        Assert.IsTrue(result.IsEvent);
        Assert.IsTrue(result.IsPlaceholder);
        Assert.AreEqual("Sports PPV|ppv_pipe:2", result.EventSlotKey);
        Assert.IsNull(result.EventContentKey);
        Assert.IsNull(result.EventTitle);
    }

    [TestMethod]
    public void Classify_WhenVersusMatch_ExtractsParticipantsSportAndLeague()
    {
        var result = EventChannelClassifier.Classify("PPV EVENT 7: Fighter A vs. Fighter B", "UFC PPV");

        Assert.IsTrue(result.IsEvent);
        Assert.IsFalse(result.IsPlaceholder);
        Assert.AreEqual("UFC PPV|ppv_event:7", result.EventSlotKey);
        Assert.AreEqual("UFC PPV::FIGHTER A VS. FIGHTER B", result.EventContentKey);
        Assert.AreEqual("Fighter A vs. Fighter B", result.EventTitle);
        Assert.AreEqual("mma", result.EventSport);
        Assert.AreEqual("UFC", result.EventLeague);
        Assert.AreEqual("[\"Fighter A\",\"Fighter B\"]", result.EventParticipantsJson);
    }

    [TestMethod]
    public void Classify_WhenContentContainsStartSuffix_StripsSuffixFromKeyAndTitle()
    {
        var result = EventChannelClassifier.Classify("Game 3: Team A vs Team B start: 19:00", "NFL GAMES");

        Assert.IsTrue(result.IsEvent);
        Assert.IsFalse(result.IsPlaceholder);
        Assert.AreEqual("NFL GAMES|game:3", result.EventSlotKey);
        Assert.AreEqual("NFL GAMES::TEAM A VS TEAM B", result.EventContentKey);
        Assert.AreEqual("Team A vs Team B", result.EventTitle);
        Assert.AreEqual("football", result.EventSport);
        Assert.AreEqual("NFL", result.EventLeague);
        Assert.AreEqual("[\"Team A\",\"Team B\"]", result.EventParticipantsJson);
    }
}
