using System;
using System.Collections.Generic;
using System.Linq;

namespace CrewStats;

/// <summary>
/// Reine Regeln des Jester-Modus (ohne Spiel, getestet in Tests/): wer Jester werden kann und wie die Runde für den
/// Bot aussieht. Spiel-Anbindung: <see cref="Jester"/>.
/// </summary>
internal static class JesterRules
{
    /// <summary>Rolle in den Rundendaten (wie der nameKey von The Other Roles, damit Bot und Website ihn kennen).</summary>
    public const string RoleName = "jester";

    /// <summary>Spielende-Grund in den Rundendaten (wie bei The Other Roles: Bot zeigt „Jester Sieg“).</summary>
    public const string WinReason = "JesterWin";

    /// <summary>Mindestens so viele Crewmates (inkl. Jester) – sonst wäre der Jester zu leicht zu erraten.</summary>
    public const int MinCrewmates = 3;

    public sealed record Candidate(byte PlayerId, string FriendCode, bool Crewmate);

    /// <summary>
    /// Jester der Runde oder null: nur Crewmates, nur mit Discord verknüpft (sonst erführe er es nie),
    /// Chance in Prozent. <paramref name="random"/>(n) liefert gleichverteilt 0 … n-1 (echter Zufall).
    /// </summary>
    public static byte? Pick(IReadOnlyList<Candidate> players, IReadOnlyCollection<string> linked, int chancePercent, Func<int, int> random)
    {
        var crew = players.Where(p => p.Crewmate).ToList();
        if (crew.Count < MinCrewmates) return null;
        var linkedSet = new HashSet<string>(linked.Select(Normalize));
        var candidates = crew.Where(p => linkedSet.Contains(Normalize(p.FriendCode))).ToList();
        if (candidates.Count == 0) return null;
        var chance = Math.Clamp(chancePercent, 0, 100);
        if (chance < 100 && random(100) >= chance) return null;
        return candidates[random(candidates.Count)].PlayerId;
    }

    /// <summary>
    /// Jester in den Rundendaten: neutral; hat er gewonnen, gewinnt nur er (wie der Jester von The Other Roles).
    /// <paramref name="rolesBeforeWin"/> = Spiel-Rollen der Spieler, die fürs Siegerbild eine Geister-Rolle bekamen.
    /// </summary>
    public static void Apply(RoundRecord round, byte jesterId, bool won, IReadOnlyDictionary<byte, string>? rolesBeforeWin = null)
    {
        var jester = round.FindPlayer(jesterId);
        if (jester == null) return;
        jester.Team = RoundRules.Neutral;
        jester.ModRole = RoleName;
        if (!won)
        {
            jester.Won = false; // ein Crew-Sieg ist kein Jester-Sieg
            return;
        }
        round.EndReason = WinReason;
        round.WinningTeam = RoundRules.Neutral;
        foreach (var player in round.Players)
        {
            player.Won = player.PlayerId == jesterId;
            // Die Geister-Rollen fürs Siegerbild sind nur ein Trick (siehe Jester.Win) – echte Rolle davor eintragen
            if (rolesBeforeWin != null && rolesBeforeWin.TryGetValue(player.PlayerId, out var before))
                player.EndRole = before == player.Role ? null : before;
        }
    }

    public static string Normalize(string? friendCode) => (friendCode ?? "").Trim().ToLowerInvariant();
}
