using System.Text.Json;
using CrewStats;
using Xunit;

namespace CrewStats.Tests;

public class VoiceRulesTests
{
    [Fact]
    public void BuildsTheReportTheWebsiteExpects()
    {
        var json = VoiceRules.Build("Kilian#1234", "ABCDEF", "tasks", new[]
        {
            new VoiceRules.Player("Kilian#1234", "Kilian", 3, 1.23456f, -2f, false, false, false),
            new VoiceRules.Player("", "Ohne", 1, 0, 0, false, false, false),
            new VoiceRules.Player("mia#5678", "Mia", 2, 10f, 4f, true, true, true),
        });
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("voice", root.GetProperty("crewstats").GetString());
        Assert.Equal("kilian#1234", root.GetProperty("fc").GetString());
        Assert.Equal("tasks", root.GetProperty("phase").GetString());
        var players = root.GetProperty("players");
        Assert.Equal(2, players.GetArrayLength());
        Assert.Equal("kilian#1234", players[0].GetProperty("fc").GetString());
        Assert.Equal(1.23, players[0].GetProperty("x").GetDouble(), 3);
        Assert.True(players[1].GetProperty("dead").GetBoolean());
        Assert.True(players[1].GetProperty("vent").GetBoolean());
        Assert.True(players[1].GetProperty("mute").GetBoolean());
    }

    [Fact]
    public void PhaseAndInterval()
    {
        Assert.Equal("lobby", VoiceRules.Phase(started: false, normalMode: true, meeting: false));
        Assert.Equal("lobby", VoiceRules.Phase(started: true, normalMode: false, meeting: false));
        Assert.Equal("tasks", VoiceRules.Phase(started: true, normalMode: true, meeting: false));
        Assert.Equal("meeting", VoiceRules.Phase(started: true, normalMode: true, meeting: true));
        Assert.Equal(VoiceRules.IdleInterval, VoiceRules.Interval(0));
        Assert.Equal(VoiceRules.ActiveInterval, VoiceRules.Interval(3));
    }

    [Fact]
    public void ReadsTheListenerCount()
    {
        Assert.Equal(2, VoiceRules.Listeners("{\"crewstats\":\"voice-reply\",\"listeners\":2}"));
        Assert.Equal(-1, VoiceRules.Listeners("{\"crewstats\":\"voice-reply\",\"error\":\"busy\"}"));
        Assert.Equal(-1, VoiceRules.Listeners("{\"crewstats\":\"join-reply\"}"));
        Assert.Equal(-1, VoiceRules.Listeners("kaputt"));
        Assert.Equal(-1, VoiceRules.Listeners(null));
    }
}
