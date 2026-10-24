using System;
using System.Collections.Generic;
using System.Linq;

namespace CrewStats;

/// <summary>Alle Daten einer Runde – wird als JSON an den Bot geschickt.</summary>
public sealed class RoundRecord
{
    public int Version { get; set; } = 1;
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
    public string Role { get; set; } = ""; // Rolle des Spiels (RoleTypes)
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
}

public sealed class KillRecord
{
    public double AtSeconds { get; set; }
    public byte Killer { get; set; }
    public byte Victim { get; set; }
}

public sealed class MeetingRecord
{
    public double AtSeconds { get; set; }
    public byte Caller { get; set; }
    public byte? Body { get; set; } // null = Notfall-Knopf
    public List<VoteRecord> Votes { get; set; } = new();
    public byte? Exiled { get; set; }
    public bool Tie { get; set; }
}

public sealed class VoteRecord
{
    public byte Voter { get; set; }
    public byte? Target { get; set; } // null = übersprungen / nicht abgestimmt
}

/// <summary>Ein Tod laut The Other Roles (Reason = CustomDeathReason: Kill, Exile, Guess, Jailed, Suicide …).</summary>
public sealed class DeathRecord
{
    public double AtSeconds { get; set; }
    public byte Victim { get; set; }
    public byte? Killer { get; set; }
    public string Reason { get; set; } = "";
}
