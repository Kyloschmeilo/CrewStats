using System.Collections.Generic;
using Xunit;

namespace CrewStats.Tests;

public class AutoJoinRulesTests
{
    private static LobbyInfo Lobby(string state = "open", string code = "ABCDEF", int players = 5, int max = 15) =>
        new(state, code, "Skeld", players, max, "Kilian");

    [Fact]
    public void Antwort_des_Bots_lesen()
    {
        var reply = AutoJoinRules.ParseReply(
            "{\"crewstats\":\"join-reply\",\"v\":1,\"state\":\"open\",\"code\":\"qwerty\",\"map\":\"Polus\",\"players\":4,\"max\":10,\"host\":\"Kilian\",\"age\":12}");
        Assert.Equal(new LobbyInfo("open", "QWERTY", "Polus", 4, 10, "Kilian"), reply);
        Assert.Equal("none", AutoJoinRules.ParseReply("{\"crewstats\":\"join-reply\",\"v\":1,\"state\":\"none\"}")!.State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{\"crewstats\":\"join\",\"v\":1}")] // noch unsere Anfrage – der Bot hat noch nicht geantwortet
    [InlineData("kein json")]
    [InlineData("[1,2]")]
    public void Keine_Antwort(string? content) => Assert.Null(AutoJoinRules.ParseReply(content));

    [Fact]
    public void Anfrage_ist_eine_join_Nachricht() =>
        Assert.Equal("{\"crewstats\":\"join\",\"v\":1}", AutoJoinRules.RequestContent());

    [Fact]
    public void Entscheidung()
    {
        var none = new HashSet<string>();
        Assert.Equal(AutoJoinRules.Decision.Join, AutoJoinRules.Decide(Lobby(), none, manual: false));
        Assert.Equal(AutoJoinRules.Decision.Started, AutoJoinRules.Decide(Lobby("started"), none, false));
        Assert.Equal(AutoJoinRules.Decision.NoLobby, AutoJoinRules.Decide(Lobby("none", ""), none, false));
        Assert.Equal(AutoJoinRules.Decision.NoLobby, AutoJoinRules.Decide(Lobby("closed"), none, false));
        Assert.Equal(AutoJoinRules.Decision.Full, AutoJoinRules.Decide(Lobby(players: 15), none, true));
        Assert.Equal(AutoJoinRules.Decision.InvalidCode, AutoJoinRules.Decide(Lobby(code: "AB1DEF"), none, true));
        Assert.Equal(AutoJoinRules.Decision.Join, AutoJoinRules.Decide(Lobby(max: 0), none, false)); // unbekannte Größe
    }

    [Fact]
    public void Automatisch_nur_einmal_pro_Lobby_per_Taste_immer()
    {
        var joined = new HashSet<string> { "ABCDEF" };
        Assert.Equal(AutoJoinRules.Decision.AlreadyJoined, AutoJoinRules.Decide(Lobby(), joined, manual: false));
        Assert.Equal(AutoJoinRules.Decision.Join, AutoJoinRules.Decide(Lobby(), joined, manual: true));
        Assert.Equal(AutoJoinRules.Decision.Join, AutoJoinRules.Decide(Lobby(code: "GHIJKL"), joined, manual: false));
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/123/abc", "https://discord.com/api/webhooks/123/abc")]
    [InlineData("  https://discord.com/api/webhooks/123/abc/?wait=true ", "https://discord.com/api/webhooks/123/abc")]
    [InlineData("https://discord.com/api/webhooks/123", null)]
    [InlineData("http://discord.com/api/webhooks/123/abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Url_pruefen(string? url, string? expected) => Assert.Equal(expected, AutoJoinRules.NormalizeUrl(url));
}
