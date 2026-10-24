using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using InnerNet;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace CrewStats;

/// <summary>Sammelt während einer Runde alle Ereignisse. Zeichnet nur als Host auf.</summary>
internal static class GameRecorder
{
    private static RoundRecord? _round;

    private static double Elapsed => _round == null ? 0 : (DateTime.UtcNow - _round.StartedAt).TotalSeconds;

    private static bool IsFreeplay => AmongUsClient.Instance?.NetworkMode == NetworkModes.FreePlay;

    /// <summary>Läuft gerade eine Aufzeichnung mit The Other Roles? (dann wird das Spielende nach TOR ausgewertet)</summary>
    public static bool IsTorRound => _round?.Mod != null;

    // ==========================================
    // SPIELSTART
    // ==========================================

    public static void OnGameStart()
    {
        _round = null;
        var client = AmongUsClient.Instance;
        if (client == null)
            return;
        if (IsFreeplay)
        {
            if (CrewStatsPlugin.RecordFreeplay.Value) StartRound(client);
            return;
        }
        if (client.AmHost)
            StartRound(client);
    }

    /// <summary>
    /// Freeplay-Test: startet die Aufzeichnung beim ersten Ereignis, falls der Intro-Hook
    /// in Freeplay nicht ausgelöst wurde.
    /// </summary>
    private static void EnsureFreeplayRound()
    {
        var client = AmongUsClient.Instance;
        if (_round == null && client != null && IsFreeplay && CrewStatsPlugin.RecordFreeplay.Value)
            StartRound(client);
    }

    private static void StartRound(AmongUsClient client)
    {
        var options = GameOptionsManager.Instance?.CurrentGameOptions;
        if (options == null || options.GameMode is not (GameModes.Normal or GameModes.NormalFools))
            return; // Hide & Seek wird nicht gezählt
        var tor = TorBridge.Loaded;
        if (tor && TorBridge.IsSpecialMode)
            return; // Hide & Seek / Freeplay von The Other Roles auch nicht

        _round = new RoundRecord
        {
            GameId = Guid.NewGuid().ToString("N"),
            LobbyCode = IsFreeplay ? "FREEPLAY" : GameCode.IntToGameName(client.GameId) ?? "",
            Map = MapName(options.MapId),
            StartedAt = DateTime.UtcNow,
            Mod = tor ? TorBridge.Description : null,
        };

        foreach (var info in GameData.Instance.AllPlayers)
        {
            if (info == null) continue;
            var player = _round.GetOrAddPlayer(info.PlayerId);
            player.Name = info.PlayerName ?? "";
            player.FriendCode = info.FriendCode ?? "";
            player.Puid = info.Puid ?? "";
            player.Role = info.RoleType.ToString();
            player.Team = RoleManager.IsImpostorRole(info.RoleType) ? RoundRules.Impostor : RoundRules.Crew;
        }
        if (tor) ApplyTorRoles(_round, start: true);

        CrewStatsPlugin.Logger.LogInfo($"Aufzeichnung gestartet: {_round.Map}, {_round.Players.Count} Spieler" +
            (tor ? $", {_round.Mod}" : ""));
    }

    // ==========================================
    // EREIGNISSE
    // ==========================================

    public static void OnKill(PlayerControl killer, PlayerControl victim)
    {
        EnsureFreeplayRound();
        if (_round == null || killer == null || victim == null) return;
        var at = Elapsed;
        var victimRecord = _round.FindPlayer(victim.PlayerId);
        if (killer.PlayerId == victim.PlayerId)
        {
            // Selbst-Kill (The Other Roles: Sheriff-Fehlschuss, Liebeskummer …) ist kein Kill
            if (victimRecord is { DeathCause: null })
            {
                victimRecord.DeathCause = "suicide";
                victimRecord.DiedAtSeconds = at;
            }
            return;
        }
        _round.Kills.Add(new KillRecord { AtSeconds = at, Killer = killer.PlayerId, Victim = victim.PlayerId });

        var killerRecord = _round.FindPlayer(killer.PlayerId);
        if (killerRecord != null) killerRecord.Kills++;

        if (victimRecord is { DeathCause: null })
        {
            victimRecord.DeathCause = "kill";
            victimRecord.KilledBy = killer.PlayerId;
            victimRecord.DiedAtSeconds = at;
        }
    }

    public static void OnMeetingStarted(PlayerControl caller, NetworkedPlayerInfo? body)
    {
        EnsureFreeplayRound();
        if (_round == null || caller == null) return;
        _round.Meetings.Add(new MeetingRecord
        {
            AtSeconds = Elapsed,
            Caller = caller.PlayerId,
            Body = body?.PlayerId,
        });
    }

    public static void OnVotingComplete(Il2CppStructArray<MeetingHud.VoterState> states, NetworkedPlayerInfo? exiled, bool tie)
    {
        if (_round == null) return;
        var meeting = _round.Meetings.LastOrDefault();
        if (meeting == null)
        {
            meeting = new MeetingRecord { AtSeconds = Elapsed };
            _round.Meetings.Add(meeting);
        }

        meeting.Votes.Clear();
        foreach (var state in states)
        {
            // 252+ sind Sonderwerte (übersprungen, nicht abgestimmt, tot)
            var target = state.SkippedVote || state.VotedForId >= 252 ? (byte?)null : state.VotedForId;
            meeting.Votes.Add(new VoteRecord { Voter = state.VoterId, Target = target });
        }
        meeting.Tie = tie;
        meeting.Exiled = exiled?.PlayerId;

        if (exiled != null && _round.FindPlayer(exiled.PlayerId) is { DeathCause: null } ejected)
        {
            ejected.DeathCause = "ejected";
            ejected.DiedAtSeconds = Elapsed;
        }
    }

    // ==========================================
    // SPIELENDE
    // ==========================================

    public static void OnGameEnd(GameOverReason reason)
    {
        var round = TakeRound();
        if (round == null) return;
        Complete(round, reason.ToString());
        round.WinningTeam = RoundRules.TeamForReason(round.EndReason);
        foreach (var player in round.Players)
            player.Won = player.Team == round.WinningTeam;
        Send(round);
    }

    /// <summary>
    /// Spielende mit The Other Roles (Postfix nach TOR): Sieger sind die, die TOR in EndGameResult.CachedWinners
    /// stehen lässt (Jester, Liebespaar, Jackal … gewinnen dort selbst), Rollen/Team zum Spielende, Tode aus TORs
    /// Verlauf (Guesser, Jailor, Selbstmord …). <paramref name="rawReason"/> = Grund vor TORs Prefix (Fallback).
    /// </summary>
    public static void OnGameEndTor(int rawReason)
    {
        var round = TakeRound();
        if (round == null) return;

        var reason = TorBridge.GameOverReason() ?? rawReason;
        var custom = reason >= RoundRules.FirstCustomReason;
        var reasonName = custom ? TorBridge.CustomReasonName(reason) ?? $"TheOtherRoles{reason}" : ((GameOverReason)reason).ToString();

        ApplyTorRoles(round, start: false);
        Complete(round, reasonName);
        ApplyTorDeaths(round);

        HashSet<string>? winners = null;
        try
        {
            winners = EndGameResult.CachedWinners.ToArray()
                .Where(w => w != null)
                .Select(w => w.PlayerName ?? "")
                .ToHashSet();
        }
        catch (Exception exc)
        {
            CrewStatsPlugin.Logger.LogWarning($"Gewinner nicht lesbar – Sieg nach Team: {exc.GetBaseException().Message}");
        }

        if (winners != null)
        {
            foreach (var player in round.Players)
                player.Won = winners.Contains(player.Name);
            round.WinningTeam = RoundRules.TorWinningTeam(reasonName, custom,
                round.Players.Where(p => p.Won).Select(p => p.Team).ToList());
        }
        else
        {
            round.WinningTeam = custom ? RoundRules.Unknown : RoundRules.TeamForReason(reasonName);
            foreach (var player in round.Players)
                player.Won = player.Team == round.WinningTeam;
        }
        Send(round);
    }

    /// <summary>Freeplay hat kein Rundenende – beim Verlassen wird die Test-Runde ohne Sieger gesendet.</summary>
    public static void OnExitGame()
    {
        if (_round == null || !IsFreeplay) return;
        var round = TakeRound()!;
        Complete(round, "FreeplayTest");
        round.WinningTeam = RoundRules.Unknown;
        Send(round);
    }

    private static RoundRecord? TakeRound()
    {
        var round = _round;
        _round = null;
        return round;
    }

    /// <summary>Endstand: Dauer, Grund, Tasks, Tode, Disconnects.</summary>
    private static void Complete(RoundRecord round, string endReason)
    {
        round.EndedAt = DateTime.UtcNow;
        round.DurationSeconds = Math.Round((round.EndedAt - round.StartedAt).TotalSeconds);
        round.EndReason = endReason;

        foreach (var info in GameData.Instance.AllPlayers)
        {
            if (info == null) continue;
            var player = round.GetOrAddPlayer(info.PlayerId);
            if (string.IsNullOrEmpty(player.Name)) player.Name = info.PlayerName ?? "";
            player.IsDead = info.IsDead;
            player.Disconnected = info.Disconnected;
            if (info.Disconnected && player.DeathCause == null) player.DeathCause = "disconnect";

            if (player.Team == RoundRules.Crew && info.Tasks != null)
            {
                player.TasksTotal = info.Tasks.Count;
                player.TasksCompleted = info.Tasks.ToArray().Count(t => t != null && t.Complete);
            }
        }
    }

    private static void Send(RoundRecord round)
    {
        CrewStatsPlugin.Logger.LogInfo($"Runde beendet: {round.WinningTeam} ({round.EndReason}) – wird gesendet");
        RoundUploader.SaveAndSend(round);
    }

    // ==========================================
    // THE OTHER ROLES
    // ==========================================

    /// <summary>
    /// TOR-Rolle und Team jedes Spielers. Beim Start gemerkt, am Ende neu gelesen (Rollen können wechseln:
    /// Shifter, Sidekick, Thief …) – dann steht die Startrolle zusätzlich in ModStartRole.
    /// </summary>
    private static void ApplyTorRoles(RoundRecord round, bool start)
    {
        foreach (var control in PlayerControl.AllPlayerControls.ToArray())
        {
            if (control == null || control.Data == null) continue;
            var player = round.FindPlayer(control.PlayerId);
            if (player == null) continue;
            var role = TorBridge.RoleOf(control);
            if (role == null) return; // TOR nicht lesbar: Rundendaten wie ohne TOR

            if (!start && player.ModRole != null && player.ModRole != role.Role)
                player.ModStartRole ??= player.ModRole;
            player.ModRole = role.Role;
            player.Modifiers = role.Modifiers;
            var gameImpostor = control.Data.Role != null ? control.Data.Role.IsImpostor : player.Team == RoundRules.Impostor;
            player.Team = RoundRules.TorTeam(gameImpostor, role.Neutral, role.Madmate);
        }
    }

    /// <summary>Tode aus TORs Verlauf: Todesart, Täter und Kills (auch Guesser-Treffer und Jailor-Hinrichtungen).</summary>
    private static void ApplyTorDeaths(RoundRecord round)
    {
        var deaths = TorBridge.Deaths();
        if (deaths.Count == 0) return;
        round.ModDeaths = deaths
            .Select(d => new DeathRecord
            {
                AtSeconds = Math.Max(0, Math.Round((d.At - round.StartedAt).TotalSeconds, 1)),
                Victim = d.Victim,
                Killer = d.Killer,
                Reason = d.Reason,
            })
            .ToList();

        foreach (var player in round.Players)
        {
            player.Kills = deaths.Count(d => d.Killer == player.PlayerId && RoundRules.CountsAsKill(d.Victim, d.Killer, d.Reason));
            var death = round.ModDeaths.LastOrDefault(d => d.Victim == player.PlayerId);
            if (death == null || !player.IsDead) continue;
            player.DeathCause = player.Disconnected ? "disconnect" : RoundRules.DeathCause(death.Reason);
            player.KilledBy = RoundRules.CountsAsKill(death.Victim, death.Killer, death.Reason) ? death.Killer : null;
            player.DiedAtSeconds ??= death.AtSeconds;
        }
    }

    internal static string MapName(byte mapId) => mapId switch
    {
        0 => "The Skeld",
        1 => "MIRA HQ",
        2 => "Polus",
        3 => "dlekS ehT",
        4 => "The Airship",
        5 => "The Fungle",
        _ => $"Map {mapId}",
    };
}
