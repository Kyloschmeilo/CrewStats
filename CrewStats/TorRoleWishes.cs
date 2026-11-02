using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;

namespace CrewStats;

/// <summary>
/// Rollenwünsche für Rollen von The Other Roles (/special-rollenwunsch, /special-rolle-zuweisen im Bot).
/// Ablauf als Host (Teil von <see cref="RoleWishes"/>):
///   1. Bei der Spiel-Rollenvergabe sorgt <see cref="RoleWishPlanner"/> dafür, dass der Spieler im richtigen Team ist
///      (Crew-/Neutral-Rolle → Crewmate, Impostor-Rolle → Impostor).
///   2. Danach vergibt TOR in seinem SelectRoles-Postfix seine Rollen über
///      RoleManagerSelectRolesPatch.setRoleToRandomPlayer(roleId, playerList, removePlayer). Ein Prefix darauf lässt
///      in der Liste kurz nur den Spieler stehen, den <see cref="TorWishPicker"/> auswählt – TORs eigene Ziehung
///      (Index 0 von 1) trifft dann ihn, Senden/Setzen der Rolle macht TOR selbst. Ein Finalizer stellt die Liste
///      wieder her.
///   3. SelectRoles-Postfix ganz am Ende: Rückmeldung an den Bot (erfüllt / nicht möglich).
/// TOR wird nur gepatcht, wenn es geladen ist; geht das nicht (andere TOR-Version), schlagen TOR-Wünsche mit
/// „disabled“ fehl und alles andere läuft normal.
/// </summary>
internal static class TorRoleWishes
{
    private const string AssignmentType = "TheOtherRoles.Patches.RoleManagerSelectRolesPatch";
    private const string AssignMethod = "setRoleToRandomPlayer";

    private static bool _patched;
    private static bool _patchFailed;
    private static TorWishPicker? _picker;
    private static int _frame;

    // Zwischen Prefix und Finalizer einer Ziehung
    private static List<PlayerControl>? _saved;
    private static PlayerControl? _chosen;

    /// <summary>Kann die Mod TOR-Wünsche anwenden? (patcht beim ersten Aufruf)</summary>
    public static bool Available
    {
        get
        {
            if (!TorBridge.Loaded) return false;
            if (!_patched && !_patchFailed) Patch();
            return _patched;
        }
    }

    /// <summary>Nach der Spiel-Rollenvergabe, vor TORs Postfix: diese Wünsche gelten für TORs Ziehungen.</summary>
    public static void Begin(List<TorWishPicker.Wish> wishes)
    {
        _picker = wishes.Count > 0 ? new TorWishPicker(wishes, max => RandomNumberGenerator.GetInt32(max)) : null;
        _frame = Time.frameCount;
    }

    /// <summary>SelectRoles-Postfix nach TOR: Ergebnis (Wunsch-Id, erfüllt?) und Ende.</summary>
    public static List<(long Id, bool Applied)> Finish()
    {
        var picker = _picker;
        _picker = null;
        if (picker == null) return new List<(long, bool)>();
        var results = picker.Wishes.Select(w => (w.Id, w.Fulfilled)).ToList();
        CrewStatsPlugin.Logger.LogInfo(
            $"Rollenwünsche (The Other Roles): {results.Count(r => r.Fulfilled)} erfüllt, {results.Count(r => !r.Fulfilled)} nicht möglich.");
        return results;
    }

    public static void Reset()
    {
        _picker = null;
        _saved = null;
        _chosen = null;
    }

    // ---------- Patch auf TOR ----------

    private static void Patch()
    {
        try
        {
            var type = TorBridge.FindType(AssignmentType) ?? throw new MissingMemberException(AssignmentType);
            var original = AccessTools.Method(type, AssignMethod, new[] { typeof(byte), typeof(List<PlayerControl>), typeof(bool) })
                ?? throw new MissingMethodException(AssignmentType, AssignMethod);
            var flags = BindingFlags.NonPublic | BindingFlags.Static;
            new Harmony(CrewStatsPlugin.Id + ".tor-wishes").Patch(original,
                prefix: new HarmonyMethod(typeof(TorRoleWishes).GetMethod(nameof(Prefix), flags)),
                finalizer: new HarmonyMethod(typeof(TorRoleWishes).GetMethod(nameof(Finalizer), flags)));
            _patched = true;
        }
        catch (Exception exc)
        {
            _patchFailed = true;
            CrewStatsPlugin.Logger.LogWarning(
                $"Rollenwünsche: Rollenvergabe von The Other Roles nicht gefunden (andere TOR-Version?) – TOR-Wünsche gehen nicht: {exc.GetBaseException().Message}");
        }
    }

    private static void Prefix(byte __0, List<PlayerControl> __1)
    {
        _saved = null;
        _chosen = null;
        var picker = _picker;
        if (picker == null || __1 == null || __1.Count == 0) return;
        if (Time.frameCount != _frame) return; // nicht mehr dieselbe Rollenvergabe

        try
        {
            var role = TorBridge.RoleIdName(__0);
            if (role == null) return;
            var candidates = __1.Where(p => p != null).Select(p => p.PlayerId).ToList();
            var chosenId = picker.Pick(role, candidates);
            if (chosenId == null) return;
            var chosen = __1.First(p => p != null && p.PlayerId == chosenId.Value);
            _saved = __1.ToList();
            _chosen = chosen;
            __1.Clear();
            __1.Add(chosen); // TOR zieht Index 0 von 1
        }
        catch (Exception exc)
        {
            CrewStatsPlugin.Logger.LogError($"Rollenwünsche (The Other Roles): Auswahl fehlgeschlagen – TOR zieht selbst: {exc}");
        }
    }

    private static Exception? Finalizer(Exception? __exception, List<PlayerControl> __1, bool __2)
    {
        var saved = _saved;
        var chosen = _chosen;
        _saved = null;
        _chosen = null;
        if (saved == null || __1 == null) return __exception;
        __1.Clear();
        foreach (var player in saved)
        {
            if (__2 && player == chosen) continue; // TOR hat den Gezogenen aus der Liste genommen
            __1.Add(player);
        }
        return __exception;
    }
}
