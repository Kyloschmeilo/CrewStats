using System;
using Xunit;

namespace CrewStats.Tests;

public class RoundRulesTests
{
    [Theory]
    [InlineData("CrewmatesByVote", "CREW")]
    [InlineData("CrewmatesByTask", "CREW")]
    [InlineData("ImpostorDisconnect", "CREW")]
    [InlineData("ImpostorsByVote", "IMPOSTOR")]
    [InlineData("ImpostorsByKill", "IMPOSTOR")]
    [InlineData("ImpostorsBySabotage", "IMPOSTOR")]
    [InlineData("CrewmateDisconnect", "IMPOSTOR")]
    [InlineData("HideAndSeek_ImpostorsByKills", "UNKNOWN")]
    [InlineData("FreeplayTest", "UNKNOWN")]
    public void TeamForReason_wie_bisher(string reason, string team) => Assert.Equal(team, RoundRules.TeamForReason(reason));

    [Theory]
    [InlineData(false, false, false, "CREW")]
    [InlineData(true, false, false, "IMPOSTOR")]
    [InlineData(false, true, false, "NEUTRAL")] // Jester, Jackal …
    [InlineData(false, false, true, "IMPOSTOR")] // Madmate gewinnt mit den Impostorn
    [InlineData(true, true, false, "IMPOSTOR")] // Impostor bleibt Impostor
    public void TorTeam(bool gameImpostor, bool neutral, bool madmate, string team) =>
        Assert.Equal(team, RoundRules.TorTeam(gameImpostor, neutral, madmate));

    [Fact]
    public void Eigenes_TOR_Spielende_ist_Sondersieg() =>
        Assert.Equal("NEUTRAL", RoundRules.TorWinningTeam("JesterWin", customReason: true, new[] { "NEUTRAL" }));

    [Fact]
    public void Normales_Spielende_bleibt_beim_Team_auch_mit_Liebespaar() =>
        Assert.Equal("CREW", RoundRules.TorWinningTeam("CrewmatesByTask", false, new[] { "CREW", "CREW", "IMPOSTOR" }));

    [Fact]
    public void Ohne_Gewinner_zaehlt_der_Grund() =>
        Assert.Equal("IMPOSTOR", RoundRules.TorWinningTeam("ImpostorsByKill", false, Array.Empty<string>()));

    [Fact]
    public void Hat_TOR_den_Sieg_umverteilt_entscheiden_die_Gewinner()
    {
        // Akujo + Partner gewinnen statt der Impostor
        Assert.Equal("NEUTRAL", RoundRules.TorWinningTeam("ImpostorsByKill", false, new[] { "NEUTRAL", "CREW" }));
        // Spielende "ImpostorDisconnect", gewonnen haben aber nur Impostor
        Assert.Equal("IMPOSTOR", RoundRules.TorWinningTeam("ImpostorDisconnect", false, new[] { "IMPOSTOR" }));
        Assert.Equal("NEUTRAL", RoundRules.TorWinningTeam("CrewmatesByVote", false, new[] { "NEUTRAL" }));
    }

    [Theory]
    [InlineData("Kill", "kill")]
    [InlineData("Exile", "ejected")]
    [InlineData("Disconnect", "disconnect")]
    [InlineData("Guess", "guess")]
    [InlineData("Jailed", "jailed")]
    [InlineData("LoverSuicide", "loverSuicide")]
    [InlineData("", "unknown")]
    public void DeathCause(string reason, string cause) => Assert.Equal(cause, RoundRules.DeathCause(reason));

    [Fact]
    public void Kills_ohne_Selbst_Kill_und_Rauswurf()
    {
        Assert.True(RoundRules.CountsAsKill(1, 2, "Kill"));
        Assert.True(RoundRules.CountsAsKill(1, 2, "Guess"));
        Assert.True(RoundRules.CountsAsKill(1, 2, "Jailed"));
        Assert.False(RoundRules.CountsAsKill(2, 2, "Kill")); // Sheriff-Fehlschuss
        Assert.False(RoundRules.CountsAsKill(1, null, "Suicide"));
        Assert.False(RoundRules.CountsAsKill(1, 2, "Exile"));
    }
}
