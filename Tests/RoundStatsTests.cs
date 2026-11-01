using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace CrewStats.Tests;

public class RoundStatsTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static RoundRecord Example() =>
        JsonSerializer.Deserialize<RoundRecord>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "beispiel-runde.json")), Json)!;

    [Fact]
    public void Diff_findet_neue_und_beendete()
    {
        var (added, removed) = RoundStats.Diff(new[] { "Lights", "Comms" }, new[] { "Comms", "Reactor" });
        Assert.Equal(new[] { "Reactor" }, added);
        Assert.Equal(new[] { "Lights" }, removed);
    }

    [Fact]
    public void Distance_ueberspringt_Spruenge_und_Pausen()
    {
        // 1 s → 1 Einheit, dann Vent-Sprung (30 Einheiten in 1 s), dann Pause durch Meeting (10 s), dann 1 Einheit
        var points = new List<float> { 0, 0, 0, 1, 1, 0, 2, 31, 0, 12, 31, 5, 13, 32, 5 };
        Assert.Equal(2.0, RoundStats.Distance(points));
        Assert.Equal(0, RoundStats.Distance(new List<float>()));
    }

    [Fact]
    public void SortEvents_ist_stabil()
    {
        var round = new RoundRecord
        {
            Events =
            {
                new GameEvent { AtSeconds = 5, Type = "b" },
                new GameEvent { AtSeconds = 1, Type = "a" },
                new GameEvent { AtSeconds = 5, Type = "c" },
            },
        };
        RoundStats.SortEvents(round);
        Assert.Equal("abc", string.Concat(round.Events.Select(e => e.Type)));
    }

    [Fact]
    public void Summarize_rechnet_Kennzahlen_aus_dem_Beispiel()
    {
        var round = Example();
        RoundStats.Summarize(round);
        var kilian = round.FindPlayer(0)!.Summary!;
        Assert.Equal(1, kilian.MeetingsCalled);
        Assert.Equal(1, kilian.BodiesReported);
        Assert.Equal(1, kilian.VotesCast);
        Assert.Equal(1, kilian.CorrectVotes); // gegen den Impostor
        Assert.Equal(1, kilian.Skips);
        Assert.Equal(1, kilian.SabotagesFixed);
        Assert.Equal(30.4, kilian.FirstTaskSeconds);
        Assert.Equal(95.0, kilian.AllTasksSeconds);
        Assert.Equal(400, kilian.AliveSeconds);
        Assert.Equal(3.0, kilian.Distance);

        var mia = round.FindPlayer(1)!.Summary!;
        Assert.Equal(1, mia.VentUses);
        Assert.Equal(3.0, mia.VentSeconds);
        Assert.Equal(1, mia.Sabotages);
        Assert.Equal(0, mia.CorrectVotes); // Impostor stimmt nie „richtig“
        Assert.Equal(1, mia.Skips + mia.VotesCast); // nicht abgestimmt zählt nicht als Skip
        Assert.Equal(260, mia.AliveSeconds);
        Assert.Equal(1.0, mia.Distance); // Sprung auf (20,20) zählt nicht

        var tom = round.FindPlayer(2)!.Summary!;
        Assert.Equal(120.5, tom.AliveSeconds);
        Assert.Equal(50.2, tom.FirstTaskSeconds);
        Assert.Null(tom.AllTasksSeconds); // nicht alle fertig
    }

    [Fact]
    public void Vent_ohne_Ausstieg_zaehlt_bis_zum_Meeting()
    {
        var round = new RoundRecord
        {
            DurationSeconds = 100,
            Players = { new PlayerRecord { PlayerId = 1, Team = RoundRules.Impostor } },
            Meetings = { new MeetingRecord { AtSeconds = 50 } },
            Events = { new GameEvent { AtSeconds = 40, Type = RoundStats.VentEnter, Player = 1 } },
        };
        RoundStats.Summarize(round);
        Assert.Equal(10, round.Players[0].Summary!.VentSeconds);
    }

    [Theory]
    [InlineData("<color=#FF1919FF>Sheriff</color>", "Sheriff")]
    [InlineData("  30s ", "30s")]
    [InlineData("a<b>c", "ac")]
    public void StripTags(string input, string expected) => Assert.Equal(expected, RoundStats.StripTags(input));

    [Fact]
    public void JSON_behaelt_die_Felder_fuer_den_Bot_und_laesst_leere_Ereignisfelder_weg()
    {
        var round = Example();
        var json = JsonSerializer.Serialize(round, Json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(RoundRecord.CurrentVersion, root.GetProperty("version").GetInt32());
        // Felder aus Version 1, die der Bot (ModRoundModel.kt) liest
        foreach (var key in new[] { "gameId", "lobbyCode", "map", "durationSeconds", "endReason", "winningTeam", "players", "meetings", "kills", "mod", "modDeaths" })
            Assert.True(root.TryGetProperty(key, out _), key);
        var player = root.GetProperty("players")[0];
        foreach (var key in new[] { "playerId", "name", "friendCode", "role", "team", "won", "isDead", "disconnected", "deathCause", "killedBy", "kills", "tasksTotal", "tasksCompleted", "diedAtSeconds", "modRole", "modifiers" })
            Assert.True(player.TryGetProperty(key, out _), key);

        var vote = root.GetProperty("events").EnumerateArray().First(e => e.GetProperty("type").GetString() == "vote");
        Assert.False(vote.TryGetProperty("room", out _));
        Assert.False(vote.TryGetProperty("x", out _));
    }

    [Fact]
    public void Beispiel_kennt_nur_dokumentierte_Ereignisse()
    {
        var known = typeof(RoundStats).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();
        foreach (var e in Example().Events) Assert.Contains(e.Type, known);
        var doc = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docs", "rundenformat.md"));
        foreach (var type in known) Assert.Contains($"`{type}`", doc);
    }
}
