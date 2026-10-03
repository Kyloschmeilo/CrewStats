using System;
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

        _round = new RoundRecord
        {
            GameId = Guid.NewGuid().ToString("N"),
            LobbyCode = IsFreeplay ? "FREEPLAY" : GameCode.IntToGameName(client.GameId) ?? "",
            Map = MapName(options.MapId),
            StartedAt = DateTime.UtcNow,
        };

        foreach (var info in GameData.Instance.AllPlayers)
        {
            if (info == null) continue;
            var player = _round.GetOrAddPlayer(info.PlayerId);
            player.Name = info.PlayerName ?? "";
            player.FriendCode = info.FriendCode ?? "";
            player.Puid = info.Puid ?? "";
            player.Role = info.RoleType.ToString();
            player.Team = RoleManager.IsImpostorRole(info.RoleType) ? "IMPOSTOR" : "CREW";
        }

        CrewStatsPlugin.Logger.LogInfo($"Aufzeichnung gestartet: {_round.Map}, {_round.Players.Count} Spieler");
    }

    // ==========================================
    // EREIGNISSE
    // ==========================================

    public static void OnKill(PlayerControl killer, PlayerControl victim)
    {
        EnsureFreeplayRound();
        if (_round == null || killer == null || victim == null) return;
        var at = Elapsed;
        _round.Kills.Add(new KillRecord { AtSeconds = at, Killer = killer.PlayerId, Victim = victim.PlayerId });

        var killerRecord = _round.FindPlayer(killer.PlayerId);
        if (killerRecord != null) killerRecord.Kills++;

        var victimRecord = _round.FindPlayer(victim.PlayerId);
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
        Finish(reason.ToString(), reason switch
        {
            GameOverReason.CrewmatesByVote or GameOverReason.CrewmatesByTask or GameOverReason.ImpostorDisconnect => "CREW",
            GameOverReason.ImpostorsByVote or GameOverReason.ImpostorsByKill
                or GameOverReason.ImpostorsBySabotage or GameOverReason.CrewmateDisconnect => "IMPOSTOR",
            _ => "UNKNOWN",
        });
    }

    /// <summary>Freeplay hat kein Rundenende – beim Verlassen wird die Test-Runde ohne Sieger gesendet.</summary>
    public static void OnExitGame()
    {
        if (_round != null && IsFreeplay)
            Finish("FreeplayTest", "UNKNOWN");
    }

    private static void Finish(string endReason, string winningTeam)
    {
        var round = _round;
        _round = null;
        if (round == null) return;

        round.EndedAt = DateTime.UtcNow;
        round.DurationSeconds = Math.Round((round.EndedAt - round.StartedAt).TotalSeconds);
        round.EndReason = endReason;
        round.WinningTeam = winningTeam;

        // Endstand: Tasks, Tode, Disconnects
        foreach (var info in GameData.Instance.AllPlayers)
        {
            if (info == null) continue;
            var player = round.GetOrAddPlayer(info.PlayerId);
            if (string.IsNullOrEmpty(player.Name)) player.Name = info.PlayerName ?? "";
            player.IsDead = info.IsDead;
            player.Disconnected = info.Disconnected;
            if (info.Disconnected && player.DeathCause == null) player.DeathCause = "disconnect";

            if (player.Team == "CREW" && info.Tasks != null)
            {
                player.TasksTotal = info.Tasks.Count;
                player.TasksCompleted = info.Tasks.ToArray().Count(t => t != null && t.Complete);
            }
        }

        foreach (var player in round.Players)
            player.Won = player.Team == round.WinningTeam;

        CrewStatsPlugin.Logger.LogInfo($"Runde beendet: {round.WinningTeam} ({round.EndReason}) – wird gesendet");
        RoundUploader.SaveAndSend(round);
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
