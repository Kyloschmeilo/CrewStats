using System;
using System.Collections.Generic;
using System.Linq;

namespace CrewStats;

/// <summary>
/// Reine Tausch-Logik für Rollenwünsche (ohne Spiel-Typen, damit sie sich auch ohne Spiel prüfen lässt).
/// Ausgangspunkt sind die Rollen, die das Spiel gewürfelt hat. Die Wünsche kommen nacheinander dran
/// (Admin-Zuweisungen zuerst, dann wer zuerst gewünscht hat):
///   1. Hat der Spieler die Rolle schon (bei Crewmate/Impostor: ist er schon im Team) → bleibt so.
///   2. Sonst Tausch mit einem noch nicht festgelegten Spieler, der die Rolle hat bzw. im Team ist.
///   3. Sonst eine Spezialrolle neu vergeben, falls laut Einstellungen noch eine frei ist (Anzahl &gt; 0 und
///      Chance &gt; 0). Muss der Spieler dafür das Team wechseln, übernimmt ein nicht festgelegter Spieler aus
///      dem anderen Team seine bisherige Rolle – die Anzahl Impostor bleibt also immer wie eingestellt.
///   4. Sonst geht es nicht: der Spieler behält die gewürfelte Rolle (der Bot erstattet die XP).
/// Erfüllte Wünsche sind festgelegt und werden von späteren Wünschen nicht mehr weggetauscht.
/// </summary>
internal sealed class RoleWishPlanner
{
    public enum Outcome
    {
        Applied,
        NoSlot, // kein Platz frei (Rolle/Team schon vergeben, zu wenige Spieler)
        Disabled, // Rolle ist in den Lobby-Einstellungen aus
        Absent, // Spieler ist nicht in der Runde
    }

    private sealed class Slot
    {
        public Slot(byte playerId, int role)
        {
            PlayerId = playerId;
            Role = role;
        }

        public byte PlayerId { get; }
        public int Role { get; set; }
        public bool Fixed { get; set; }
    }

    private readonly List<Slot> _slots;
    private readonly Func<int, bool> _isImpostor;
    private readonly Func<int, int> _maxCount;
    private readonly int _crewmate;
    private readonly int _impostor;

    /// <param name="assigned">Die gewürfelten Rollen (Spieler-ID, Rolle) in Vergabe-Reihenfolge.</param>
    /// <param name="isImpostor">Gehört die Rolle zum Impostor-Team?</param>
    /// <param name="maxCount">Wie oft es eine Spezialrolle höchstens gibt (0 = aus).</param>
    public RoleWishPlanner(IEnumerable<(byte PlayerId, int Role)> assigned, Func<int, bool> isImpostor,
        Func<int, int> maxCount, int crewmate, int impostor)
    {
        _slots = assigned.Select(a => new Slot(a.PlayerId, a.Role)).ToList();
        _isImpostor = isImpostor;
        _maxCount = maxCount;
        _crewmate = crewmate;
        _impostor = impostor;
    }

    /// <summary>Die Rollen nach allen Wünschen, in derselben Reihenfolge wie übergeben.</summary>
    public IReadOnlyList<(byte PlayerId, int Role)> Result => _slots.Select(s => (s.PlayerId, s.Role)).ToList();

    public Outcome Apply(byte playerId, int role)
    {
        var me = _slots.FirstOrDefault(s => s.PlayerId == playerId);
        if (me == null) return Outcome.Absent;
        if (me.Fixed) return Outcome.NoSlot; // hat schon eine festgelegte Rolle

        var teamWish = role == _crewmate || role == _impostor;
        var wantImpostor = teamWish ? role == _impostor : _isImpostor(role);
        bool Matches(int r) => teamWish ? _isImpostor(r) == wantImpostor : r == role;

        if (Matches(me.Role)) return Fix(me);

        // Tausch – bei Crewmate/Impostor am liebsten mit jemandem, der die Grundrolle hat
        var donor = Free(me).Where(s => Matches(s.Role)).OrderBy(s => IsBase(s.Role) ? 0 : 1).FirstOrDefault();
        if (donor != null)
        {
            (me.Role, donor.Role) = (donor.Role, me.Role);
            return Fix(me);
        }

        if (teamWish) return Outcome.NoSlot; // die Teamgröße steht fest
        var max = _maxCount(role);
        if (max <= 0) return Outcome.Disabled;
        if (_slots.Count(s => s.Role == role) >= max) return Outcome.NoSlot;

        if (_isImpostor(me.Role) != wantImpostor)
        {
            var partner = Free(me).Where(s => _isImpostor(s.Role) == wantImpostor).OrderBy(s => IsBase(s.Role) ? 0 : 1).FirstOrDefault();
            if (partner == null) return Outcome.NoSlot;
            partner.Role = me.Role;
        }
        me.Role = role;
        return Fix(me);
    }

    private IEnumerable<Slot> Free(Slot me) => _slots.Where(s => s != me && !s.Fixed);

    private bool IsBase(int role) => role == _crewmate || role == _impostor;

    private static Outcome Fix(Slot slot)
    {
        slot.Fixed = true;
        return Outcome.Applied;
    }
}
