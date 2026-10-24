using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;

namespace CrewStats;

/// <summary>
/// Behebt Kicks als Host mit The Other Roles (GMIA 1.4.1): Tritt jemand der Lobby bei, schickt TOR alle Einstellungen
/// (<c>CustomOption.ShareOptionSelections</c>) – bis zu 200 Optionen und alle Rollen-Filter (Guesser-Rollenlisten) in
/// je EINEM Paket. Das Filter-Paket ist ~1635 Bytes groß, also größer als ein UDP-Paket (~1500) übers Internet; kommt
/// es nicht an, trennt der Server den Host nach 7,5 s („Reliable packet … was not ack'd“, gesehen am 24.10.2026).
/// Hier werden dieselben Daten in kleinen Paketen geschickt (40 Optionen bzw. ein Filter je Paket) – TORs Empfänger
/// liest die Anzahl aus jedem Paket, das Format bleibt gleich, andere Spieler brauchen den Fix nicht.
/// Fehlt etwas (andere TOR-Version), läuft TORs eigenes Senden wie bisher.
/// </summary>
internal static class TorPacketFix
{
    private const int OptionsPerPacket = 40;
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static bool _applied;
    private static bool _failed;

    private static FieldInfo _options = null!, _id = null!, _selection = null!, _filterSelection = null!, _nameKey = null!;
    private static Type _filterType = null!;
    private static byte _rpcOptions, _rpcFilters;

    /// <summary>Patcht TOR, sobald es geladen ist (TOR lädt nach CrewStats, daher erst aus dem Hauptmenü).</summary>
    public static void TryApply()
    {
        if (_applied) return;
        var tor = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "TheOtherRoles");
        if (tor == null) return;
        _applied = true;
        try
        {
            var option = tor.GetType("TheOtherRoles.CustomOption", true)!;
            _filterType = tor.GetType("TheOtherRoles.CustomFilterOption", true)!;
            _options = option.GetField("options", Static) ?? throw new MissingFieldException("CustomOption.options");
            _id = option.GetField("id", Instance) ?? throw new MissingFieldException("CustomOption.id");
            _selection = option.GetField("selection", Instance) ?? throw new MissingFieldException("CustomOption.selection");
            _filterSelection = _filterType.GetField("filterSelection", Instance) ?? throw new MissingFieldException("filterSelection");
            _nameKey = tor.GetType("TheOtherRoles.RoleInfo", true)!.GetField("nameKey", Instance) ?? throw new MissingFieldException("RoleInfo.nameKey");
            var rpc = tor.GetType("TheOtherRoles.CustomRPC", true)!;
            _rpcOptions = Convert.ToByte(Enum.Parse(rpc, "ShareOptions"));
            _rpcFilters = Convert.ToByte(Enum.Parse(rpc, "ShareFilterOptions"));
            var share = option.GetMethod("ShareOptionSelections", Static, null, Type.EmptyTypes, null)
                        ?? throw new MissingMethodException("CustomOption.ShareOptionSelections");
            new Harmony("de.amongusmanager.crewstats.torfix").Patch(share, prefix: new HarmonyMethod(typeof(TorPacketFix), nameof(Prefix)));
            CrewStatsPlugin.Logger.LogInfo("The Other Roles: Einstellungen werden in kleinen Paketen geteilt (Fix gegen Kicks beim Beitreten).");
        }
        catch (Exception exc)
        {
            CrewStatsPlugin.Logger.LogWarning($"The Other Roles: Paket-Fix nicht möglich (andere TOR-Version?): {exc.GetBaseException().Message}");
        }
    }

    // false = TORs eigenes Senden überspringen (nur wenn wir selbst gesendet haben)
    private static bool Prefix()
    {
        if (_failed) return true;
        try
        {
            // dieselbe Bedingung wie in TOR
            var client = AmongUsClient.Instance;
            if (PlayerControl.AllPlayerControls.Count <= 1 || client == null || (!client.AmHost && PlayerControl.LocalPlayer == null)) return false;
            var local = PlayerControl.LocalPlayer;
            if (local == null) return true;

            // erst alles lesen, dann senden – ein Lesefehler fällt so sauber auf TORs Senden zurück
            var all = ((IEnumerable)_options.GetValue(null)!).Cast<object>().ToList();
            var values = all.Select(o => ((uint)(int)_id.GetValue(o)!, (uint)(int)_selection.GetValue(o)!)).ToList();
            var filters = all.Where(_filterType.IsInstanceOfType)
                .Select(o => ((uint)(int)_id.GetValue(o)!, string.Join(",",
                    ((IEnumerable)_filterSelection.GetValue(o)!).Cast<object>().Select(r => (string)_nameKey.GetValue(r)!))))
                .ToList();

            for (var start = 0; start < values.Count; start += OptionsPerPacket)
            {
                var chunk = values.Skip(start).Take(OptionsPerPacket).ToList();
                var writer = client.StartRpcImmediately(local.NetId, _rpcOptions, SendOption.Reliable, -1);
                writer.Write((byte)chunk.Count);
                foreach (var (id, selection) in chunk)
                {
                    writer.WritePacked(id);
                    writer.WritePacked(selection);
                }
                client.FinishRpcImmediately(writer);
            }

            foreach (var (id, text) in filters)
            {
                var writer = client.StartRpcImmediately(local.NetId, _rpcFilters, SendOption.Reliable, -1);
                writer.Write((byte)1);
                writer.WritePacked(id);
                writer.Write(text);
                client.FinishRpcImmediately(writer);
            }
            return false;
        }
        catch (Exception exc)
        {
            _failed = true;
            CrewStatsPlugin.Logger.LogWarning($"The Other Roles: Paket-Fix fehlgeschlagen, ab jetzt TORs eigenes Senden: {exc.GetBaseException().Message}");
            return true;
        }
    }
}
