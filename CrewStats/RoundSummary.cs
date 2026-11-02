using System;
using System.Linq;
using AmongUs.GameOptions;

namespace CrewStats;

/// <summary>Was die Mod über die eigene Runde weiß (für die Rundenzusammenfassung nach dem Spielende).</summary>
internal sealed class OwnRound
{
    public string Role = "";
    public string Team = ""; // CREW | IMPOSTOR | NEUTRAL
    public int Kills;
    public int Tasks;
    public int TasksTotal;
    public bool Dead;
    public bool? Won;
    public DateTime EndedAt;

    // vom Bot (falls verknüpft und der Host CrewStats hat)
    public LastRound? Bot;
    public bool BotChecked; // alle Nachfragen beim Bot erledigt
    public bool NotLinked;
}

/// <summary>
/// Rundenzusammenfassung (Config [Komfort] Rundenzusammenfassung, Standard an): merkt sich bei JEDEM Spieler (nicht nur
/// beim Host) die eigene Rolle, eigene Kills und Aufgaben und zeigt sie nach dem Spielende in der Lobby an – dazu die
/// XP, sobald der Bot die Runde des Hosts ausgewertet hat. Während der Runde wird nichts angezeigt (kein Vorteil).
/// Alles wird nur gelesen, das Spiel bleibt unverändert.
/// </summary>
internal static class RoundSummary
{
    private static OwnRound? _running;

    /// <summary>Zuletzt beendete Runde (bis sie weggeklickt wird oder die nächste beginnt).</summary>
    public static OwnRound? Last { get; private set; }

    public static void Dismiss() => Last = null;

    private static bool CountsAsRound
    {
        get
        {
            var client = AmongUsClient.Instance;
            if (client == null || client.NetworkMode == NetworkModes.FreePlay) return false;
            var options = GameOptionsManager.Instance?.CurrentGameOptions;
            if (options == null || options.GameMode is not (GameModes.Normal or GameModes.NormalFools)) return false;
            return !(TorBridge.Loaded && TorBridge.IsSpecialMode);
        }
    }

    public static void OnGameStart()
    {
        _running = null;
        Last = null;
        if (!CrewStatsPlugin.ShowRoundSummary.Value || !CountsAsRound) return;
        var me = PlayerControl.LocalPlayer;
        var info = me != null ? me.Data : null;
        if (info == null) return;
        var role = info.RoleType;
        _running = new OwnRound
        {
            Role = role.ToString(),
            Team = RoleManager.IsImpostorRole(role) ? RoundRules.Impostor : RoundRules.Crew,
        };
    }

    public static void OnKill(PlayerControl killer, PlayerControl victim)
    {
        if (_running == null || killer == null || victim == null || killer.PlayerId == victim.PlayerId) return;
        if (killer == PlayerControl.LocalPlayer) _running.Kills++;
    }

    /// <summary>Spielende-Prefix: die Spielerdaten gibt es noch.</summary>
    public static void OnGameEnd()
    {
        var round = _running;
        if (round == null) return;
        round.EndedAt = DateTime.UtcNow;
        var info = PlayerControl.LocalPlayer != null ? PlayerControl.LocalPlayer.Data : null;
        if (info == null) return;
        round.Dead = info.IsDead;
        if (round.Team == RoundRules.Crew && info.Tasks != null)
        {
            round.TasksTotal = info.Tasks.Count;
            round.Tasks = info.Tasks.ToArray().Count(t => t != null && t.Complete);
        }
    }

    /// <summary>Spielende-Postfix (nach The Other Roles): Gewinner und TOR-Rolle.</summary>
    public static void OnGameEndPostfix()
    {
        var round = _running;
        _running = null;
        if (round == null) return;
        try
        {
            round.Won = EndGameResult.CachedWinners.ToArray().Any(w => w != null && w.IsYou);
        }
        catch
        {
            round.Won = null; // dann zeigt es der Bot (falls verknüpft)
        }
        if (TorBridge.Loaded && PlayerControl.LocalPlayer != null)
        {
            var tor = TorBridge.RoleOf(PlayerControl.LocalPlayer);
            if (tor != null && tor.Role.Length > 0)
            {
                round.Role = tor.Role;
                round.Team = tor.Madmate ? RoundRules.Impostor : tor.Neutral ? "NEUTRAL" : round.Team;
            }
        }
        if (round.EndedAt == default) round.EndedAt = DateTime.UtcNow;
        Last = round;
        Comfort.OnRoundEnded();
    }

    /// <summary>Antwort des Bots auf die Nachfrage nach der Runde.</summary>
    public static void ApplyBot(StatsReply reply)
    {
        var round = Last;
        if (round == null) return;
        if (reply.Me is { Linked: false })
        {
            round.NotLinked = true;
            round.BotChecked = true;
            return;
        }
        if (reply.Last != null && PlayerInfoRules.IsThisRound(reply.Last, (DateTime.UtcNow - round.EndedAt).TotalSeconds))
        {
            round.Bot = reply.Last;
            round.BotChecked = true;
        }
    }
}
