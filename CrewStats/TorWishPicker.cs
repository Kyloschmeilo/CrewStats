using System;
using System.Collections.Generic;
using System.Linq;

namespace CrewStats;

/// <summary>
/// Reine Auswahl-Logik für Wünsche auf Rollen von The Other Roles (ohne Spiel-Typen, getestet in Tests/).
/// TOR vergibt seine Rollen nacheinander: für jede Rolle zieht es einen zufälligen Spieler aus einer Liste
/// (Crewmates für Crew-/Neutral-Rollen, Impostor für Impostor-Rollen). Vor jeder Ziehung entscheidet <see cref="Pick"/>:
///   1. Wünscht sich ein Spieler aus der Liste genau diese Rolle → er bekommt sie (Admin-Zuweisungen zuerst,
///      dann wer zuerst gewünscht hat).
///   2. Sonst zufällig unter den Spielern, die noch auf eine Wunschrolle warten – die werden nur gezogen, wenn
///      sonst niemand mehr übrig ist (sonst wäre ihr Platz für die gewünschte Rolle schon weg).
/// Welche Rollen es in der Runde gibt, entscheidet weiter TOR (Einstellungen, Chancen) – kommt die Wunschrolle
/// nicht vor oder ist der Spieler im falschen Team, ist der Wunsch nicht erfüllt (der Bot erstattet die XP).
/// </summary>
internal sealed class TorWishPicker
{
    public sealed class Wish
    {
        public Wish(long id, byte playerId, IEnumerable<string> roles, bool admin)
        {
            Id = id;
            PlayerId = playerId;
            Roles = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase);
            Admin = admin;
        }

        public long Id { get; }
        public byte PlayerId { get; }
        public HashSet<string> Roles { get; } // TOR-RoleId-Namen, z.B. "Sheriff" oder "NiceGuesser"/"EvilGuesser"
        public bool Admin { get; }
        public bool Fulfilled { get; internal set; }
    }

    private readonly List<Wish> _wishes;
    private readonly Func<int, int> _random;

    /// <param name="random">Zufallszahl in [0, n) – im Spiel RandomNumberGenerator.</param>
    public TorWishPicker(IEnumerable<Wish> wishes, Func<int, int> random)
    {
        _wishes = wishes.OrderBy(w => w.Admin ? 0 : 1).ThenBy(w => w.Id).ToList();
        _random = random;
    }

    public IReadOnlyList<Wish> Wishes => _wishes;

    /// <summary>
    /// Wer bekommt <paramref name="role"/> aus <paramref name="candidates"/>? null = TOR soll wie immer selbst ziehen
    /// (niemand wartet, oder nur noch Wartende sind übrig).
    /// </summary>
    public byte? Pick(string role, IReadOnlyList<byte> candidates)
    {
        if (candidates.Count == 0) return null;
        var wish = _wishes.FirstOrDefault(w => !w.Fulfilled && w.Roles.Contains(role) && candidates.Contains(w.PlayerId));
        if (wish != null)
        {
            wish.Fulfilled = true;
            return wish.PlayerId;
        }

        var waiting = _wishes.Where(w => !w.Fulfilled).Select(w => w.PlayerId).ToHashSet();
        var others = candidates.Where(c => !waiting.Contains(c)).ToList();
        if (others.Count == 0 || others.Count == candidates.Count) return null;
        return others[_random(others.Count)];
    }
}
