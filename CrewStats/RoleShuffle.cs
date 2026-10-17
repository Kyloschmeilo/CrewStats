using System.Security.Cryptography;
using NetworkedPlayerList = Il2CppSystem.Collections.Generic.List<NetworkedPlayerInfo>;

namespace CrewStats;

/// <summary>
/// Rollen selbst würfeln (Config [Rollen] SelbstWuerfeln, Standard an, Wunsch des Nutzers): als Host mischt die Mod
/// vor jeder Rollenvergabe die Spielerliste mit dem Kryptozufall von .NET. Das Spiel vergibt danach ganz normal
/// (welche Rollen es gibt, wie viele Impostor – alles nach den Lobby-Einstellungen), zieht die Spieler aber aus der
/// gemischten Liste – damit hat jeder dieselbe Chance auf jede Rolle, egal wie gut der Zufall des Spiels ist.
///
/// Bewusst KEIN Zurückhalten/Neu-Senden der Rollen (so lief es in 1.3.2 – die erste Runde damit ist beim Intro
/// eingefroren). Das passiert nur noch in Runden mit Rollenwünschen (<see cref="RoleWishes"/>).
/// </summary>
internal static class RoleShuffle
{
    public static bool Enabled => CrewStatsPlugin.ShuffleRoles.Value;

    /// <summary>LogicRoleSelectionNormal.AssignRolesForTeam (Prefix) – läuft je Runde für Impostor und Crew.</summary>
    public static void BeforeAssignTeam(NetworkedPlayerList? players, RoleTeamTypes team)
    {
        if (!Enabled || players == null || players.Count < 2) return;
        var client = AmongUsClient.Instance;
        if (client == null || !client.AmHost) return;

        for (var i = players.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            if (j == i) continue;
            var swap = players[i];
            players[i] = players[j];
            players[j] = swap;
        }
        CrewStatsPlugin.Logger.LogInfo($"Rollen selbst gewürfelt: Spielerliste gemischt ({team}, {players.Count} Spieler).");
    }
}
