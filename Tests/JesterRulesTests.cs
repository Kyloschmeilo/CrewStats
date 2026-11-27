using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace CrewStats.Tests;

public class JesterRulesTests
{
    private static List<JesterRules.Candidate> Lobby() => new()
    {
        new(0, "Imp#1", false),
        new(1, "a#1", true),
        new(2, "B#2", true),
        new(3, "c#3", true),
        new(4, "d#4", true),
    };

    [Fact]
    public void Nur_verknuepfte_Crewmates_koennen_Jester_werden()
    {
        var linked = new[] { "imp#1", "b#2", "c#3" };
        var picks = new HashSet<byte?>();
        for (var i = 0; i < 2; i++) picks.Add(JesterRules.Pick(Lobby(), linked, 100, n => i % n));
        Assert.Equal(new HashSet<byte?> { 2, 3 }, picks); // nie der Impostor, nie unverknüpfte
    }

    [Fact]
    public void Ohne_Verknuepfte_oder_mit_zu_wenig_Crew_kein_Jester()
    {
        Assert.Null(JesterRules.Pick(Lobby(), new[] { "imp#1" }, 100, _ => 0));
        var small = Lobby().Take(3).ToList(); // 2 Crewmates
        Assert.Null(JesterRules.Pick(small, new[] { "a#1", "b#2" }, 100, _ => 0));
    }

    [Fact]
    public void Chance_entscheidet_ob_es_einen_Jester_gibt()
    {
        var linked = new[] { "a#1" };
        Assert.Null(JesterRules.Pick(Lobby(), linked, 0, _ => 0));
        Assert.Null(JesterRules.Pick(Lobby(), linked, 30, n => n == 100 ? 30 : 0)); // 30 von 0–99 liegt nicht unter 30
        Assert.Equal((byte)1, JesterRules.Pick(Lobby(), linked, 30, n => n == 100 ? 29 : 0));
    }

    private static RoundRecord Round()
    {
        var round = new RoundRecord { EndReason = "HumansByVote", WinningTeam = RoundRules.Crew };
        foreach (var (id, role, team) in new[] { (0, "Impostor", "IMPOSTOR"), (1, "Crewmate", "CREW"), (2, "Engineer", "CREW") })
        {
            var player = round.GetOrAddPlayer((byte)id);
            player.Role = role;
            player.Team = team;
            player.Won = team == round.WinningTeam;
        }
        return round;
    }

    [Fact]
    public void Jester_Sieg_nur_der_Jester_gewinnt()
    {
        var round = Round();
        round.Players[0].EndRole = "CrewmateGhost"; // Trick fürs Siegerbild
        round.Players[1].EndRole = "ImpostorGhost";
        JesterRules.Apply(round, 1, won: true, new Dictionary<byte, string> { [0] = "Impostor", [1] = "CrewmateGhost" });

        Assert.Equal("JesterWin", round.EndReason);
        Assert.Equal(RoundRules.Neutral, round.WinningTeam);
        Assert.Equal(new[] { false, true, false }, round.Players.Select(p => p.Won));
        Assert.Equal(RoundRules.Neutral, round.Players[1].Team);
        Assert.Equal("jester", round.Players[1].ModRole);
        Assert.Null(round.Players[0].EndRole); // war vorher noch Impostor
        Assert.Equal("CrewmateGhost", round.Players[1].EndRole); // rausgewählt = Geist
    }

    [Fact]
    public void Ohne_Rauswurf_verliert_der_Jester_auch_beim_Crew_Sieg()
    {
        var round = Round();
        JesterRules.Apply(round, 2, won: false);
        Assert.Equal("HumansByVote", round.EndReason);
        Assert.Equal(RoundRules.Crew, round.WinningTeam);
        Assert.False(round.Players[2].Won);
        Assert.True(round.Players[1].Won);
        Assert.Equal(RoundRules.Neutral, round.Players[2].Team);
    }
}
