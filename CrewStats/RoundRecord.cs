using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace CrewStats;

/// <summary>
/// Alle Daten einer Runde – wird als JSON (camelCase) an den Bot geschickt. Vollständig beschrieben in
/// docs/rundenformat.md. Version 1 = alles bis „ModDeaths“; Version 2 hängt nur Felder an (der Bot ignoriert,
/// was er nicht kennt) – bestehende Felder nie umbenennen oder umdeuten, der Bot wertet sie aus.
/// Bleibt frei von Spiel-Typen, damit Tests/ es ohne Among Us kompilieren kann.
/// </summary>
public sealed class RoundRecord
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public string GameId { get; set; } = "";
    public string LobbyCode { get; set; } = "";
    public string Map { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public double DurationSeconds { get; set; }
    public string EndReason { get; set; } = "";
    public string WinningTeam { get; set; } = "UNKNOWN"; // CREW | IMPOSTOR | UNKNOWN, mit The Other Roles auch NEUTRAL (Sondersieg)
    public string? Mod { get; set; } // Rollen-Mod, z.B. "TheOtherRoles GMIA 1.4.1" – null ohne
    public List<PlayerRecord> Players { get; set; } = new();
    public List<KillRecord> Kills { get; set; } = new();
    public List<MeetingRecord> Meetings { get; set; } = new();
    public List<DeathRecord> ModDeaths { get; set; } = new(); // nur mit The Other Roles: alle Tode laut TOR

    // ---------- ab Version 2 ----------

    public int? MapId { get; set; }
    public string? CrewStatsVersion { get; set; } // z.B. "1.5.0"
    public string? GameVersion { get; set; } // Among-Us-Version, z.B. "2026.9.29"
    public string? Server { get; set; } // Region, z.B. "Modded NA"
    public string? GameMode { get; set; } // Normal | NormalFools
    public string? ModGameMode { get; set; } // The Other Roles: Classic | Guesser …
    public string? EndedBy { get; set; } // "game" (normales Ende) | "freeplay-exit"
    public byte? HostPlayerId { get; set; }
    public RoundSettings? Settings { get; set; } // Lobby-Einstellungen beim Start
    public Dictionary<string, string>? ModSettings { get; set; } // The Other Roles: Option → gewählter Wert
    public List<GameEvent> Events { get; set; } = new(); // Zeitleiste, nach AtSeconds sortiert
    public List<PlayerTrack> Tracks { get; set; } = new(); // Laufwege (nur Lebende, ohne Meetings)
    public double? TrackIntervalSeconds { get; set; }

    public PlayerRecord GetOrAddPlayer(byte playerId)
    {
        var player = Players.FirstOrDefault(p => p.PlayerId == playerId);
        if (player == null)
        {
            player = new PlayerRecord { PlayerId = playerId };
            Players.Add(player);
        }
        return player;
    }

    public PlayerRecord? FindPlayer(byte playerId) => Players.FirstOrDefault(p => p.PlayerId == playerId);
}

public sealed class PlayerRecord
{
    public byte PlayerId { get; set; }
    public string Name { get; set; } = "";
    public string FriendCode { get; set; } = "";
    public string Puid { get; set; } = "";
    public string Role { get; set; } = ""; // Rolle des Spiels (RoleTypes) beim Start
    public string Team { get; set; } = ""; // CREW | IMPOSTOR, mit The Other Roles auch NEUTRAL
    public string? ModRole { get; set; } // The Other Roles: Rolle am Spielende (nameKey, z.B. "sheriff")
    public string? ModStartRole { get; set; } // The Other Roles: Rolle beim Start, falls sie sich geändert hat
    public List<string>? Modifiers { get; set; } // The Other Roles: Modifikatoren (z.B. "lover", "mini", "madmate")
    public bool Won { get; set; }
    public bool IsDead { get; set; }
    public bool Disconnected { get; set; }
    public string? DeathCause { get; set; } // kill | ejected | disconnect | suicide, mit TOR auch guess, jailed …
    public byte? KilledBy { get; set; }
    public double? DiedAtSeconds { get; set; }
    public int Kills { get; set; }
    public int TasksTotal { get; set; }
    public int TasksCompleted { get; set; }

    // ---------- ab Version 2 ----------

    public string? EndRole { get; set; } // Rolle des Spiels am Ende (z.B. CrewmateGhost), falls anders als Role
    public bool IsHost { get; set; }
    public int? ColorId { get; set; }
    public string? Color { get; set; } // englischer Farbname, z.B. "Red"
    public string? Hat { get; set; }
    public string? Skin { get; set; }
    public string? Visor { get; set; }
    public string? Pet { get; set; }
    public string? NamePlate { get; set; }
    public uint? Level { get; set; } // Among-Us-Account-Level
    public string? Platform { get; set; } // StandaloneSteamPC, Android …
    public double? DisconnectedAtSeconds { get; set; }
    public List<TaskRecord>? Tasks { get; set; }
    public PlayerSummary? Summary { get; set; }
}

public sealed class KillRecord
{
    public double AtSeconds { get; set; }
    public byte Killer { get; set; }
    public byte Victim { get; set; }

    // ---------- ab Version 2 ----------
    public float? X { get; set; } // Ort des Opfers (= Leiche)
    public float? Y { get; set; }
    public string? Room { get; set; }
    public string? KillerRole { get; set; } // TOR-Rolle (oder Spiel-Rolle) des Täters zum Zeitpunkt
    public string? VictimRole { get; set; }
}

public sealed class MeetingRecord
{
    public double AtSeconds { get; set; }
    public byte Caller { get; set; }
    public byte? Body { get; set; } // null = Notfall-Knopf
    public List<VoteRecord> Votes { get; set; } = new();
    public byte? Exiled { get; set; }
    public bool Tie { get; set; }

    // ---------- ab Version 2 ----------
    public double? EndedAtSeconds { get; set; } // Abstimmung fertig
    public float? CallerX { get; set; }
    public float? CallerY { get; set; }
    public string? CallerRoom { get; set; }
    public string? BodyRoom { get; set; } // wo die gemeldete Leiche lag
    public List<byte>? Alive { get; set; } // lebende Spieler zu Beginn
}

public sealed class VoteRecord
{
    public byte Voter { get; set; }
    public byte? Target { get; set; } // null = übersprungen / nicht abgestimmt

    // ---------- ab Version 2 ----------
    public bool? Skipped { get; set; } // true = „Überspringen“ gedrückt, false = gar nicht abgestimmt (nur wenn Target null)
    public double? AtSeconds { get; set; } // wann die Stimme abgegeben wurde
}

/// <summary>Ein Tod laut The Other Roles (Reason = CustomDeathReason: Kill, Exile, Guess, Jailed, Suicide …).</summary>
public sealed class DeathRecord
{
    public double AtSeconds { get; set; }
    public byte Victim { get; set; }
    public byte? Killer { get; set; }
    public string Reason { get; set; } = "";
}

// ==========================================
// AB VERSION 2
// ==========================================

/// <summary>Lobby-Einstellungen beim Rundenstart (gleiche Namen wie crewstats-settings.json).</summary>
public sealed class RoundSettings
{
    public int MaxPlayers { get; set; }
    public int NumImpostors { get; set; }
    public Dictionary<string, int> Ints { get; set; } = new();
    public Dictionary<string, float> Floats { get; set; } = new();
    public Dictionary<string, bool> Bools { get; set; } = new();
    public Dictionary<string, RoleSetting> Roles { get; set; } = new();
}

public sealed class RoleSetting
{
    public int Count { get; set; }
    public int Chance { get; set; }
}

/// <summary>
/// Ein Ereignis der Zeitleiste. Welche Felder gesetzt sind, hängt von <see cref="Type"/> ab (siehe
/// docs/rundenformat.md); leere Felder fehlen im JSON.
/// </summary>
public sealed class GameEvent
{
    public double AtSeconds { get; set; }
    public string Type { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public byte? Player { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public byte? Target { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public float? X { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public float? Y { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Room { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? System { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Task { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Vent { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Role { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Previous { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Reason { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Skipped { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Tie { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? DurationSeconds { get; set; }
}

/// <summary>Eine Aufgabe eines Spielers.</summary>
public sealed class TaskRecord
{
    public uint Id { get; set; }
    public string Type { get; set; } = ""; // TaskTypes, z.B. "FixWiring"
    public bool Completed { get; set; }
    public double? CompletedAtSeconds { get; set; }
}

/// <summary>
/// Laufweg eines Spielers: <see cref="Points"/> ist flach [t, x, y, t, x, y, …] (Sekunden seit Start, Spielkoordinaten),
/// damit 15 Minuten × 15 Spieler klein bleiben. Nur solange der Spieler lebt und kein Meeting läuft.
/// </summary>
public sealed class PlayerTrack
{
    public byte Player { get; set; }
    public List<float> Points { get; set; } = new();
}

/// <summary>Aus Ereignissen und Laufweg berechnete Kennzahlen (siehe RoundStats).</summary>
public sealed class PlayerSummary
{
    public double Distance { get; set; } // gelaufene Strecke in Spieleinheiten
    public double? AliveSeconds { get; set; } // bis zum Tod bzw. Rundenende
    public int MeetingsCalled { get; set; } // Notfall-Knopf
    public int BodiesReported { get; set; }
    public int VotesCast { get; set; } // auf einen Spieler
    public int Skips { get; set; }
    public int CorrectVotes { get; set; } // gegen jemanden aus einem anderen Team (Impostor ↔ Crew), ohne Neutrale
    public int VentUses { get; set; }
    public double VentSeconds { get; set; }
    public int Sabotages { get; set; } // gestartete Sabotagen (soweit bekannt)
    public int SabotagesFixed { get; set; } // beendete Sabotagen, an denen der Spieler repariert hat
    public double? FirstTaskSeconds { get; set; }
    public double? AllTasksSeconds { get; set; } // alle Aufgaben fertig
}
