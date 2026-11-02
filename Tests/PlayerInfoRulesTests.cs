using System.Text.Json;
using Xunit;

namespace CrewStats.Tests;

/// <summary>Komfort-Funktionen: Anfragen an den Bot und seine Antworten (↔ Bot services/ModPlayerInfo.kt).</summary>
public class PlayerInfoRulesTests
{
    // So antwortet der Bot (PlayerInfoService.statsReply)
    private const string BotStats =
        "{\"crewstats\":\"stats-reply\",\"v\":1,\"xpPerLevel\":50,\"me\":{\"linked\":true,\"level\":7,\"games\":34,\"wins\":19," +
        "\"xp\":330,\"rank\":\"Navigator\",\"kills\":12,\"last\":{\"ago\":40,\"role\":\"Sheriff\",\"team\":\"CREW\",\"won\":true," +
        "\"xp\":15,\"kills\":1,\"tasks\":5,\"tasksTotal\":6}},\"players\":[{\"linked\":true,\"level\":7,\"games\":34,\"wins\":19}," +
        "{\"linked\":false}]}";

    [Fact]
    public void Stats_Antwort_lesen()
    {
        var reply = PlayerInfoRules.ParseStats(BotStats)!;
        Assert.Equal(new PlayerStats(true, 7, 34, 19, 330, "Navigator", 12), reply.Me);
        Assert.Equal(new LastRound(40, "Sheriff", "CREW", true, 15, 1, 5, 6), reply.Last);
        Assert.Equal(2, reply.Players.Count);
        Assert.False(reply.Players[1].Linked);
        Assert.Equal(50, reply.XpPerLevel);
        Assert.Equal("Level 7 · 30/50 XP", PlayerInfoRules.LevelLine(reply.Me!, reply.XpPerLevel));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{\"crewstats\":\"stats\",\"v\":1,\"me\":\"a#1234\",\"players\":[]}")] // noch unsere Anfrage
    [InlineData("{\"crewstats\":\"join-reply\",\"state\":\"none\"}")]
    [InlineData("kaputt")]
    public void Noch_keine_Antwort(string? content)
    {
        Assert.Null(PlayerInfoRules.ParseStats(content));
        Assert.Null(PlayerInfoRules.ParseLink(content));
    }

    [Fact]
    public void Anfragen_bauen()
    {
        using var stats = JsonDocument.Parse(PlayerInfoRules.StatsRequestContent("kilian#1234", new[] { "a#1111", "a#1111", "", "b#2222" }));
        Assert.Equal("stats", stats.RootElement.GetProperty("crewstats").GetString());
        Assert.Equal("kilian#1234", stats.RootElement.GetProperty("me").GetString());
        Assert.Equal(2, stats.RootElement.GetProperty("players").GetArrayLength()); // doppelt/leer fällt raus

        var many = new string[30];
        for (var i = 0; i < many.Length; i++) many[i] = $"p{i}#{1000 + i}";
        using var capped = JsonDocument.Parse(PlayerInfoRules.StatsRequestContent(null, many));
        Assert.Equal(PlayerInfoRules.MaxPlayers, capped.RootElement.GetProperty("players").GetArrayLength());

        Assert.Equal("{\"crewstats\":\"link\",\"v\":1,\"fc\":\"kilian#1234\"}", PlayerInfoRules.LinkRequestContent("kilian#1234"));
    }

    [Fact]
    public void Code_zum_Verknuepfen()
    {
        var reply = PlayerInfoRules.ParseLink("{\"crewstats\":\"link-reply\",\"v\":1,\"ok\":true,\"code\":\"K7P2QX\",\"minutes\":10,\"linked\":false}")!;
        Assert.True(reply.Ok);
        Assert.Equal("K7P2QX", reply.Code);
        Assert.Equal(10, reply.Minutes);
        Assert.False(reply.AlreadyLinked);
        var failed = PlayerInfoRules.ParseLink("{\"crewstats\":\"link-reply\",\"v\":1,\"ok\":false,\"linked\":false,\"error\":\"friendcode\"}")!;
        Assert.False(failed.Ok);
        Assert.Equal("friendcode", failed.Error);
    }

    [Theory]
    [InlineData(40, 60, true)] // Bot hat 20 s nach Spielende ausgewertet
    [InlineData(80, 60, true)] // Uhren gehen etwas auseinander
    [InlineData(600, 60, false)] // das war eine ältere Runde
    public void Letzte_Runde_gehoert_zu_unserer(int ago, double sinceEnd, bool expected) =>
        Assert.Equal(expected, PlayerInfoRules.IsThisRound(new LastRound(ago, "", "", false, 0, 0, 0, 0), sinceEnd));

    [Theory]
    [InlineData("GuardianAngel", "Guardian Angel")]
    [InlineData("niceGuesser", "Nice Guesser")]
    [InlineData("sheriff", "Sheriff")]
    [InlineData("", "?")]
    public void Rollennamen(string role, string expected) => Assert.Equal(expected, PlayerInfoRules.RoleName(role));

    [Fact]
    public void Texte()
    {
        Assert.Equal("–", PlayerInfoRules.WinRate(0, 0));
        Assert.Equal("56 %", PlayerInfoRules.WinRate(19, 34));
        Assert.Equal("Abcd…", PlayerInfoRules.Short("Abcdefgh", 5));
        Assert.DoesNotContain("<", PlayerInfoRules.Short("<color=red>x</color>", 30)); // keine Rich-Text-Tricks über Namen
        Assert.Equal("Crew", PlayerInfoRules.TeamName("CREW"));
    }
}
