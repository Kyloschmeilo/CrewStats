using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CrewStats;

/// <summary>
/// Ausführliche Zeitleiste für die Stats-Website (ab Rundenformat 2): Laufwege, Räume, Aufgaben, Sabotagen,
/// Türen, Vents, Disconnects, Rollenwechsel, Gestaltwandeln/Schutz/Wiederbeleben.
/// Läuft nur beim aufzeichnenden Host (dieselben Daten hat der Host ohnehin) und wird erst NACH dem Spielende
/// verschickt – im Spiel wird nichts angezeigt, niemand bekommt einen Vorteil. Jede Abfrage, die einmal scheitert
/// (z.B. nach einem Among-Us-Update), wird geloggt und bleibt danach aus; die Runde wird trotzdem gesendet.
/// </summary>
internal static partial class GameRecorder
{
    /// <summary>Sabotagen, die als „aktiv“ gelten können (IActivatable). Türen laufen extra (AllDoors).</summary>
    private static readonly SystemTypes[] SabotageSystems =
    {
        SystemTypes.Reactor, SystemTypes.Laboratory, SystemTypes.LifeSupp, SystemTypes.Electrical,
        SystemTypes.Comms, SystemTypes.HeliSabotage, SystemTypes.MushroomMixupSabotage,
    };

    private const double PollInterval = 0.2;
    private const double RolePollInterval = 2.0;
    private const double AttributionWindow = 1.0; // Sabotage/Tür → wer hat in der Sekunde davor das System benutzt

    private static readonly HashSet<string> FailedParts = new();
    private static readonly Dictionary<byte, double> _votesAt = new();
    private static readonly Dictionary<byte, string> _roles = new();
    private static readonly Dictionary<byte, double> _inVent = new(); // Spieler → seit wann
    private static readonly Dictionary<byte, int> _ventIds = new();
    private static readonly Dictionary<string, double> _sabotages = new(); // System → seit wann
    private static readonly Dictionary<string, HashSet<byte>> _repairers = new();
    private static readonly HashSet<string> _closedDoors = new();
    private static readonly HashSet<byte> _disconnected = new();
    private static readonly Dictionary<(byte Player, uint Task), double> _taskDone = new();
    private static readonly Dictionary<byte, PlayerTrack> _tracks = new();
    private static readonly List<(byte Player, SystemTypes System, double At)> _interactions = new();
    private static List<(string Name, Collider2D Area)>? _rooms;
    private static double _nextPoll, _nextTrack, _nextRolePoll;

    // ==========================================
    // START / ENDE
    // ==========================================

    private static void TimelineStart(RoundRecord round)
    {
        _votesAt.Clear();
        _roles.Clear();
        _inVent.Clear();
        _ventIds.Clear();
        _sabotages.Clear();
        _repairers.Clear();
        _closedDoors.Clear();
        _disconnected.Clear();
        _taskDone.Clear();
        _tracks.Clear();
        _interactions.Clear();
        _rooms = null;
        _nextPoll = _nextTrack = 0;
        _nextRolePoll = RolePollInterval;
        foreach (var player in round.Players) _roles.TryAdd(player.PlayerId, player.ModRole ?? player.Role);
    }

    private static void TimelineEnd(RoundRecord round, double at)
    {
        Poll(round, at); // letzte Änderungen (z.B. die Aufgabe, die das Spiel beendet hat)
        CloseVents(at);
        foreach (var (system, since) in _sabotages.ToList())
        {
            AddEvent(round, new GameEvent
            {
                AtSeconds = at, Type = RoundStats.SabotageEnd, System = system, Reason = "gameEnd",
                DurationSeconds = RoundStats.Seconds(at - since),
            });
        }
        _sabotages.Clear();
        round.Tracks = _tracks.Values.Where(t => t.Points.Count > 0).OrderBy(t => t.Player).ToList();
        _tracks.Clear();
    }

    // ==========================================
    // JEDEN FRAME (HudManager.Update)
    // ==========================================

    public static void Tick()
    {
        var round = _round;
        if (round == null || ShipStatus.Instance == null) return;
        var at = (DateTime.UtcNow - round.StartedAt).TotalSeconds;
        if (at < _nextPoll) return;
        _nextPoll = at + PollInterval;
        at = RoundStats.Seconds(at);

        Optional("Zeitleiste", () => Poll(round, at));
        if (at >= _nextTrack)
        {
            _nextTrack = at + RoundStats.TrackInterval;
            Optional("Laufwege", () => SampleTracks(at));
        }
        if (TorBridge.Loaded && at >= _nextRolePoll)
        {
            _nextRolePoll = at + RolePollInterval;
            Optional("Rollenwechsel", () => PollRoles(round, at));
        }
    }

    private static bool InMeeting => MeetingHud.Instance != null || ExileController.Instance != null;

    private static void Poll(RoundRecord round, double at)
    {
        PollVents(round, at);
        PollSabotages(round, at);
        PollDoors(round, at);
        PollTasks(round, at);
        PollDisconnects(round, at);
        _interactions.RemoveAll(i => at - i.At > 30);
    }

    private static void PollVents(RoundRecord round, double at)
    {
        foreach (var control in PlayerControl.AllPlayerControls.ToArray())
        {
            if (control == null) continue;
            var id = control.PlayerId;
            var inside = control.inVent;
            if (inside && !_inVent.ContainsKey(id))
            {
                var pos = control.GetTruePosition();
                var vent = NearestVent(pos);
                _inVent[id] = at;
                if (vent != null) _ventIds[id] = vent.Value;
                AddEvent(round, new GameEvent
                {
                    AtSeconds = at, Type = RoundStats.VentEnter, Player = id, Vent = vent,
                    X = RoundStats.Coord(pos.x), Y = RoundStats.Coord(pos.y), Room = RoomAt(pos),
                });
            }
            else if (!inside && _inVent.ContainsKey(id))
            {
                var pos = control.GetTruePosition();
                _inVent.Remove(id);
                AddEvent(round, new GameEvent
                {
                    AtSeconds = at, Type = RoundStats.VentExit, Player = id, Vent = NearestVent(pos),
                    X = RoundStats.Coord(pos.x), Y = RoundStats.Coord(pos.y), Room = RoomAt(pos),
                });
            }
        }
    }

    /// <summary>Meeting/Spielende holt alle aus den Vents – ohne Ausstieg zählt die Zeit bis dahin.</summary>
    private static void CloseVents(double at)
    {
        if (_round == null) return;
        foreach (var id in _inVent.Keys.ToList())
        {
            AddEvent(new GameEvent
            {
                AtSeconds = at, Type = RoundStats.VentExit, Player = id,
                Vent = _ventIds.TryGetValue(id, out var vent) ? vent : null, Reason = "meeting",
            });
        }
        _inVent.Clear();
    }

    private static void PollSabotages(RoundRecord round, double at)
    {
        var ship = ShipStatus.Instance;
        var active = new List<string>();
        foreach (var type in SabotageSystems)
        {
            if (!ship.Systems.ContainsKey(type)) continue;
            var system = ship.Systems[type]?.TryCast<IActivatable>();
            if (system != null && system.IsActive) active.Add(SabotageName(type));
        }

        var (started, ended) = RoundStats.Diff(_sabotages.Keys.ToList(), active);
        foreach (var name in started)
        {
            _sabotages[name] = at;
            _repairers[name] = new HashSet<byte>();
            AddEvent(round, new GameEvent
            {
                AtSeconds = at, Type = RoundStats.Sabotage, System = name,
                Player = LastInteraction(SystemTypes.Sabotage, at),
            });
        }
        foreach (var name in ended)
        {
            var duration = at - _sabotages[name];
            _sabotages.Remove(name);
            var fixers = _repairers.TryGetValue(name, out var set) ? set : new HashSet<byte>();
            foreach (var player in fixers)
                AddEvent(round, new GameEvent { AtSeconds = at, Type = RoundStats.Repair, Player = player, System = name });
            AddEvent(round, new GameEvent
            {
                AtSeconds = at, Type = RoundStats.SabotageEnd, System = name,
                Reason = InMeeting ? "meeting" : "fixed", DurationSeconds = RoundStats.Seconds(duration),
            });
            _repairers.Remove(name);
        }
    }

    private static void PollDoors(RoundRecord round, double at)
    {
        var closed = new HashSet<string>();
        foreach (var door in ShipStatus.Instance.AllDoors.ToArray())
        {
            if (door != null && !door.IsOpen) closed.Add(door.Room.ToString());
        }
        var (shut, opened) = RoundStats.Diff(_closedDoors.ToList(), closed);
        foreach (var room in shut)
        {
            _closedDoors.Add(room);
            AddEvent(round, new GameEvent
            {
                AtSeconds = at, Type = RoundStats.Doors, Room = room, Player = LastInteraction(SystemTypes.Doors, at),
            });
        }
        foreach (var room in opened) _closedDoors.Remove(room);
    }

    private static void PollTasks(RoundRecord round, double at)
    {
        foreach (var info in GameData.Instance.AllPlayers)
        {
            if (info == null || info.Tasks == null) continue;
            foreach (var task in info.Tasks.ToArray())
            {
                if (task == null || !task.Complete || _taskDone.ContainsKey((info.PlayerId, task.Id))) continue;
                _taskDone[(info.PlayerId, task.Id)] = at;
                var e = new GameEvent { AtSeconds = at, Type = RoundStats.Task, Player = info.PlayerId, Task = TaskName(task.TypeId) };
                var control = info.Object;
                if (control != null)
                {
                    var pos = control.GetTruePosition();
                    e.X = RoundStats.Coord(pos.x);
                    e.Y = RoundStats.Coord(pos.y);
                    e.Room = RoomAt(pos);
                }
                AddEvent(round, e);
            }
        }
    }

    private static void PollDisconnects(RoundRecord round, double at)
    {
        foreach (var info in GameData.Instance.AllPlayers)
        {
            if (info == null || !info.Disconnected || !_disconnected.Add(info.PlayerId)) continue;
            AddEvent(round, new GameEvent { AtSeconds = at, Type = RoundStats.Disconnect, Player = info.PlayerId });
            if (round.FindPlayer(info.PlayerId) is { } player) player.DisconnectedAtSeconds ??= at;
        }
    }

    private static void PollRoles(RoundRecord round, double at)
    {
        foreach (var control in PlayerControl.AllPlayerControls.ToArray())
        {
            if (control == null || control.Data == null) continue;
            var role = TorBridge.RoleOf(control)?.Role;
            if (string.IsNullOrEmpty(role)) continue;
            if (_roles.TryGetValue(control.PlayerId, out var before) && before != role)
            {
                AddEvent(round, new GameEvent
                {
                    AtSeconds = at, Type = RoundStats.RoleChange, Player = control.PlayerId, Role = role, Previous = before,
                });
            }
            _roles[control.PlayerId] = role;
        }
    }

    private static void SampleTracks(double at)
    {
        if (InMeeting) return;
        foreach (var control in PlayerControl.AllPlayerControls.ToArray())
        {
            if (control == null || control.Data == null || control.Data.IsDead || control.Data.Disconnected) continue;
            if (!_tracks.TryGetValue(control.PlayerId, out var track))
                _tracks[control.PlayerId] = track = new PlayerTrack { Player = control.PlayerId };
            var pos = control.GetTruePosition();
            track.Points.Add((float)at);
            track.Points.Add(RoundStats.Coord(pos.x));
            track.Points.Add(RoundStats.Coord(pos.y));
        }
    }

    // ==========================================
    // HOOKS (aus GamePatches)
    // ==========================================

    /// <summary>ShipStatus.UpdateSystem beim Host: wer hat welches System benutzt (Sabotage starten, reparieren, Türen).</summary>
    public static void OnSystemUsed(SystemTypes system, PlayerControl player)
    {
        if (_round == null || player == null) return;
        var at = Elapsed;
        _interactions.Add((player.PlayerId, system, at));
        // Reparatur: zählt, wenn die Sabotage dieses Systems gerade läuft
        var name = SabotageName(system);
        if (_repairers.TryGetValue(name, out var set)) set.Add(player.PlayerId);
    }

    public static void OnShapeshift(PlayerControl player, PlayerControl target)
    {
        if (_round == null || player == null || target == null) return;
        AddEvent(new GameEvent
        {
            AtSeconds = Elapsed, Type = RoundStats.Shapeshift, Player = player.PlayerId, Target = target.PlayerId,
            Reason = player.PlayerId == target.PlayerId ? "revert" : null,
        });
    }

    public static void OnProtect(PlayerControl guardian, PlayerControl target)
    {
        if (_round == null || guardian == null || target == null) return;
        AddEvent(new GameEvent { AtSeconds = Elapsed, Type = RoundStats.Protect, Player = guardian.PlayerId, Target = target.PlayerId });
    }

    public static void OnRevive(PlayerControl player)
    {
        if (_round == null || player == null) return;
        AddEvent(new GameEvent { AtSeconds = Elapsed, Type = RoundStats.Revive, Player = player.PlayerId });
    }

    // ==========================================
    // HELFER
    // ==========================================

    private static void AddEvent(GameEvent e)
    {
        if (_round != null) AddEvent(_round, e);
    }

    private static void AddEvent(RoundRecord round, GameEvent e) => round.Events.Add(e);

    /// <summary>Optionaler Teil der Aufzeichnung: ein Fehler wird einmal geloggt, danach bleibt der Teil aus.</summary>
    private static void Optional(string part, Action action)
    {
        if (FailedParts.Contains(part)) return;
        try
        {
            action();
        }
        catch (Exception exc)
        {
            FailedParts.Add(part);
            CrewStatsPlugin.Logger.LogWarning($"Rundendaten: {part} nicht lesbar – ab jetzt ohne: {exc.GetBaseException().Message}");
        }
    }

    private static byte? LastInteraction(SystemTypes system, double at)
    {
        for (var i = _interactions.Count - 1; i >= 0; i--)
        {
            var interaction = _interactions[i];
            if (at - interaction.At > AttributionWindow) break;
            if (interaction.System == system) return interaction.Player;
        }
        return null;
    }

    /// <summary>Name einer Sabotage in den Rundendaten (Reactor, Laboratory, Oxygen, Lights, Comms, Crash, MushroomMixup).</summary>
    private static string SabotageName(SystemTypes system) => system switch
    {
        SystemTypes.Electrical => "Lights",
        SystemTypes.LifeSupp => "Oxygen",
        SystemTypes.HeliSabotage => "Crash",
        SystemTypes.MushroomMixupSabotage => "MushroomMixup",
        _ => system.ToString(),
    };

    /// <summary>Raum an einer Position (Name aus SystemTypes, z.B. "Electrical"); Flure nur, wenn sonst nichts passt.</summary>
    private static string? RoomAt(Vector2 pos)
    {
        if (ShipStatus.Instance == null) return null;
        _rooms ??= ShipStatus.Instance.AllRooms.ToArray()
            .Where(r => r != null && r.roomArea != null)
            .Select(r => (r.RoomId.ToString(), r.roomArea))
            .OrderBy(r => r.Item1 == nameof(SystemTypes.Hallway) ? 1 : 0)
            .ToList();
        foreach (var (name, area) in _rooms)
        {
            if (area != null && area.OverlapPoint(pos)) return name;
        }
        return null;
    }

    private static int? NearestVent(Vector2 pos)
    {
        int? best = null;
        var bestDistance = 2.0f; // weiter weg = nicht an einem Vent
        foreach (var vent in ShipStatus.Instance.AllVents.ToArray())
        {
            if (vent == null) continue;
            var p = vent.transform.position;
            var distance = Vector2.Distance(pos, new Vector2(p.x, p.y));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = vent.Id;
            }
        }
        return best;
    }

    private static string CurrentRole(PlayerControl player)
    {
        if (TorBridge.Loaded && TorBridge.RoleOf(player)?.Role is { Length: > 0 } role) return role;
        return player.Data != null ? player.Data.RoleType.ToString() : "";
    }

    private static string TaskName(byte typeId)
    {
        var task = ShipStatus.Instance != null ? ShipStatus.Instance.GetTaskById(typeId) : null;
        return task != null ? task.TaskType.ToString() : $"Task{typeId}";
    }

    private static List<TaskRecord> TaskList(NetworkedPlayerInfo info)
    {
        var list = new List<TaskRecord>();
        if (info.Tasks == null) return list;
        foreach (var task in info.Tasks.ToArray())
        {
            if (task == null) continue;
            list.Add(new TaskRecord
            {
                Id = task.Id,
                Type = TaskName(task.TypeId),
                Completed = task.Complete,
                CompletedAtSeconds = _taskDone.TryGetValue((info.PlayerId, task.Id), out var at) ? at : null,
            });
        }
        return list;
    }
}
