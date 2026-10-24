using System.Collections.Generic;
using System.Linq;

namespace CrewStats;

/// <summary>
/// Reine Regeln fürs Rundenende (ohne Spiel-Typen, damit Tests/ sie ohne Among Us prüfen kann):
/// welches Team gewinnt, welches Team ein Spieler mit The-Other-Roles-Rolle hat, wie TOR-Todesarten heißen.
/// Teams: CREW | IMPOSTOR | NEUTRAL (nur mit The Other Roles), Sieger zusätzlich UNKNOWN.
/// </summary>
internal static class RoundRules
{
    public const string Crew = "CREW";
    public const string Impostor = "IMPOSTOR";
    public const string Neutral = "NEUTRAL";
    public const string Unknown = "UNKNOWN";

    /// <summary>Ab diesem Wert sind GameOverReasons eigene Spielenden von The Other Roles (Jester-Sieg usw.).</summary>
    public const int FirstCustomReason = 10;

    /// <summary>Sieger-Team zu einem GameOverReason des Spiels (Name wie im Enum). Sonst UNKNOWN.</summary>
    public static string TeamForReason(string reason) => reason switch
    {
        "CrewmatesByVote" or "CrewmatesByTask" or "ImpostorDisconnect" => Crew,
        "ImpostorsByVote" or "ImpostorsByKill" or "ImpostorsBySabotage" or "CrewmateDisconnect" => Impostor,
        _ => Unknown,
    };

    /// <summary>
    /// Team eines Spielers mit The Other Roles: neutrale Rollen spielen für sich, Impostor (und Madmates,
    /// die mit den Impostorn gewinnen) sind Team Impostor, alle anderen Crew.
    /// </summary>
    public static string TorTeam(bool gameImpostor, bool neutralRole, bool madmate)
    {
        if (neutralRole && !gameImpostor) return Neutral;
        return gameImpostor || madmate ? Impostor : Crew;
    }

    /// <summary>
    /// Sieger-Team einer TOR-Runde. Eigene TOR-Spielenden (Jester, Jackal, Liebespaar …) sind NEUTRAL – dann
    /// verlieren alle Wetten. Bei normalen Spielenden zählt das Team aus dem Grund, außer TOR hat den Sieg
    /// jemand anderem gegeben (z.B. Akujo) – dann entscheiden die tatsächlichen Gewinner.
    /// </summary>
    public static string TorWinningTeam(string reason, bool customReason, IReadOnlyCollection<string> winnerTeams)
    {
        if (customReason) return Neutral;
        var team = TeamForReason(reason);
        if (winnerTeams.Count == 0 || winnerTeams.Contains(team)) return team;
        var distinct = winnerTeams.Distinct().ToList();
        return distinct.Count == 1 && distinct[0] != Neutral ? distinct[0] : Neutral;
    }

    /// <summary>Todesursache wie im Rundenformat (kill | ejected | disconnect), sonst der TOR-Name klein (guess, jailed …).</summary>
    public static string DeathCause(string torReason) => torReason switch
    {
        "Kill" => "kill",
        "Exile" => "ejected",
        "Disconnect" => "disconnect",
        "" => "unknown",
        _ => char.ToLowerInvariant(torReason[0]) + torReason.Substring(1),
    };

    /// <summary>Zählt ein Tod als Kill des Täters? Nicht bei Selbst-Kill (Sheriff-Fehlschuss, Liebeskummer) und Rauswurf.</summary>
    public static bool CountsAsKill(byte victim, byte? killer, string torReason) =>
        killer != null && killer != victim && torReason is not ("Exile" or "Disconnect");
}
