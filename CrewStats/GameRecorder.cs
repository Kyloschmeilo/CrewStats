using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using InnerNet;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace CrewStats;

/// <summary>
/// Sammelt während einer Runde alle Ereignisse. Zeichnet nur als Host auf. Die ausführliche Zeitleiste
/// (Laufwege, Aufgaben, Sabotagen, Vents …) steht in GameTimeline.cs. Format: docs/rundenformat.md.
/// </summary>
internal static partial class GameRecorder
{
    private static RoundRecord? _round;

    private static double Elapsed => _round == null ? 0 : RoundStats.Seconds((DateTime.UtcNow - _round.StartedAt).TotalSeconds);

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
            MapId = options.MapId,
            StartedAt = DateTime.UtcNow,
            Mod = tor ? TorBridge.Description : null,
            CrewStatsVersion = CrewStatsPlugin.Version,
            GameMode = options.GameMode.ToString(),
            TrackIntervalSeconds = RoundStats.TrackInterval,
        };
        var round = _round;
        Optional("Spielversion", () => round.GameVersion = UnityEngine.Application.version);
        Optional("Server", () => round.Server = IsFreeplay ? "Freeplay" : ServerManager.Instance?.CurrentRegion?.Name);
        Optional("Host", () => round.HostPlayerId = PlayerControl.LocalPlayer != null ? PlayerControl.LocalPlayer.PlayerId : null);
        Optional("Einstellungen", () => round.Settings = SettingsReporter.Capture(options));
        if (tor)
        {
            round.ModGameMode = TorBridge.GameMode;
            round.ModSettings = TorBridge.Options();
        }

        foreach (var info in GameData.Instance.AllPlayers)
        {
            if (info == null) continue;
            var player = round.GetOrAddPlayer(info.PlayerId);
            player.Name = info.PlayerName ?? "";
            player.FriendCode = info.FriendCode ?? "";
            player.Puid = info.Puid ?? "";
            player.Role = info.RoleType.ToString();
            player.Team = RoleManager.IsImpostorRole(info.RoleType) ? RoundRules.Impostor : RoundRules.Crew;
            player.IsHost = player.PlayerId == round.HostPlayerId;
            Optional("Aussehen", () => ApplyLooks(player, info));
        }
        if (tor) ApplyTorRoles(round, start: true);
        TimelineStart(round);

        CrewStatsPlugin.Logger.LogInfo($"Aufzeichnung gestartet: {round.Map}, {round.Players.Count} Spieler" +
            (tor ? $", {round.Mod}" : ""));
    }

    /// <summary>Farbe, Kosmetik, Level und Plattform – alles, was jeder in der Lobby sieht.</summary>
    private static void ApplyLooks(PlayerRecord player, NetworkedPlayerInfo info)
    {
        var outfit = info.DefaultOutfit;
        if (outfit != null)
        {
            player.ColorId = outfit.ColorId;
            player.Color = ColorName(outfit.ColorId);
            player.Hat = Cosmetic(outfit.HatId);
            player.Skin = Cosmetic(outfit.SkinId);
            player.Visor = Cosmetic(outfit.VisorId);
            player.Pet = Cosmetic(outfit.PetId);
            player.NamePlate = Cosmetic(outfit.NamePlateId);
        }
        player.Level = info.PlayerLevel;
        var client = AmongUsClient.Instance?.GetClientFromPlayerInfo(info);
        if (client?.PlatformData != null) player.Platform = client.PlatformData.Platform.ToString();
    }

    private static string? Cosmetic(string? id) => string.IsNullOrEmpty(id) || id.EndsWith("_None") || id == "missing" ? null : id;

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
        var kill = new KillRecord { AtSeconds = at, Killer = killer.PlayerId, Victim = victim.PlayerId };
        Optional("Kill-Ort", () =>
        {
            var pos = victim.GetTruePosition();
            kill.X = RoundStats.Coord(pos.x);
            kill.Y = RoundStats.Coord(pos.y);
            kill.Room = RoomAt(pos);
            kill.KillerRole = CurrentRole(killer);
            kill.VictimRole = CurrentRole(victim);
        });
        _round.Kills.Add(kill);
        AddEvent(new GameEvent
        {
            AtSeconds = at, Type = RoundStats.Kill, Player = killer.PlayerId, Target = victim.PlayerId,
            X = kill.X, Y = kill.Y, Room = kill.Room, Role = kill.KillerRole,
        });

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
        var meeting = new MeetingRecord
        {
            AtSeconds = Elapsed,
            Caller = caller.PlayerId,
            Body = body?.PlayerId,
        };
        Optional("Meeting-Ort", () =>
        {
            var pos = caller.GetTruePosition();
            meeting.CallerX = RoundStats.Coord(pos.x);
            meeting.CallerY = RoundStats.Coord(pos.y);
            meeting.CallerRoom = RoomAt(pos);
            if (body != null) meeting.BodyRoom = _round.Kills.LastOrDefault(k => k.Victim == body.PlayerId)?.Room;
            meeting.Alive = GameData.Instance.AllPlayers.ToArray()
                .Where(p => p != null && !p.IsDead && !p.Disconnected)
                .Select(p => p.PlayerId)
                .ToList();
        });
        _round.Meetings.Add(meeting);
        _votesAt.Clear();
        AddEvent(new GameEvent
        {
            AtSeconds = meeting.AtSeconds, Type = RoundStats.Meeting, Player = caller.PlayerId, Target = body?.PlayerId,
            X = meeting.CallerX, Y = meeting.CallerY, Room = meeting.CallerRoom,
            Reason = body == null ? "emergency" : "report",
        });
        CloseVents(meeting.AtSeconds);
    }

    /// <summary>MeetingHud.CastVote (beim Host, für jede gezählte Stimme): merkt sich, wann abgestimmt wurde.</summary>
    public static void OnVoteCast(byte voter, byte suspect)
    {
        if (_round == null) return;
        _votesAt[voter] = Elapsed;
    }

    public static void OnVotingComplete(Il2CppStructArray<MeetingHud.VoterState> states, NetworkedPlayerInfo? exiled, bool tie)
    {
        if (_round == null) return;
        var at = Elapsed;
        var meeting = _round.Meetings.LastOrDefault();
        if (meeting == null)
        {
            meeting = new MeetingRecord { AtSeconds = at };
            _round.Meetings.Add(meeting);
        }

        meeting.Votes.Clear();
        foreach (var state in states)
        {
            // 252+ sind Sonderwerte (übersprungen, nicht abgestimmt, tot)
            var target = state.SkippedVote || state.VotedForId >= 252 ? (byte?)null : state.VotedForId;
            var vote = new VoteRecord
            {
                Voter = state.VoterId,
                Target = target,
                Skipped = target == null ? state.SkippedVote : null,
                AtSeconds = _votesAt.TryGetValue(state.VoterId, out var votedAt) ? votedAt : null,
            };
            meeting.Votes.Add(vote);
            if (target != null || vote.Skipped == true)
            {
                AddEvent(new GameEvent
                {
                    AtSeconds = vote.AtSeconds ?? at, Type = RoundStats.Vote, Player = vote.Voter, Target = target,
                    Skipped = target == null ? true : null,
                });
            }
        }
        meeting.Tie = tie;
        meeting.Exiled = exiled?.PlayerId;
        meeting.EndedAtSeconds = at;
        AddEvent(new GameEvent { AtSeconds = at, Type = RoundStats.Exile, Target = exiled?.PlayerId, Tie = tie });

        if (exiled != null && _round.FindPlayer(exiled.PlayerId) is { DeathCause: null } ejected)
        {
            ejected.DeathCause = "ejected";
            ejected.DiedAtSeconds = at;
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
        round.EndedBy = "freeplay-exit";
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
        round.EndedBy = "game";
        var at = RoundStats.Seconds((round.EndedAt - round.StartedAt).TotalSeconds);
        Optional("Zeitleiste-Ende", () => TimelineEnd(round, at));

        foreach (var info in GameData.Instance.AllPlayers)
        {
            if (info == null) continue;
            var player = round.GetOrAddPlayer(info.PlayerId);
            if (string.IsNullOrEmpty(player.Name)) player.Name = info.PlayerName ?? "";
            player.IsDead = info.IsDead;
            player.Disconnected = info.Disconnected;
            if (info.Disconnected && player.DeathCause == null) player.DeathCause = "disconnect";
            var endRole = info.RoleType.ToString();
            if (endRole != player.Role) player.EndRole = endRole;

            if (player.Team == RoundRules.Crew && info.Tasks != null)
            {
                player.TasksTotal = info.Tasks.Count;
                player.TasksCompleted = info.Tasks.ToArray().Count(t => t != null && t.Complete);
            }
            if (player.Team == RoundRules.Crew) Optional("Aufgaben", () => player.Tasks = TaskList(info));
        }
    }

    private static void Send(RoundRecord round)
    {
        Optional("Kennzahlen", () =>
        {
            RoundStats.SortEvents(round);
            RoundStats.Summarize(round);
        });
        CrewStatsPlugin.Logger.LogInfo($"Runde beendet: {round.WinningTeam} ({round.EndReason}), {round.Events.Count} Ereignisse – wird gesendet");
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
            if (start) _roles[control.PlayerId] = role.Role;
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

        // In der Zeitleiste fehlen bisher nur die Tode, die kein normaler Kill/Rauswurf waren (Guess, Jailed, Suicide …)
        foreach (var death in round.ModDeaths.Where(d => d.Reason is not ("Kill" or "Exile" or "Disconnect")))
        {
            AddEvent(round, new GameEvent
            {
                AtSeconds = death.AtSeconds, Type = RoundStats.Death, Player = death.Killer, Target = death.Victim,
                Reason = RoundRules.DeathCause(death.Reason),
            });
        }

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

    /// <summary>Englische Farbnamen des Spiels (ColorId 0–17), unabhängig von der Spielsprache.</summary>
    private static readonly string[] ColorNames =
    {
        "Red", "Blue", "Green", "Pink", "Orange", "Yellow", "Black", "White", "Purple",
        "Brown", "Cyan", "Lime", "Maroon", "Rose", "Banana", "Gray", "Tan", "Coral",
    };

    internal static string ColorName(int colorId) =>
        colorId >= 0 && colorId < ColorNames.Length ? ColorNames[colorId] : $"Color{colorId}";
}
