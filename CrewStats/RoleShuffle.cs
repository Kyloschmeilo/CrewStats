using System.Security.Cryptography;
using UnityEngine;

namespace CrewStats;

/// <summary>
/// Rollen selbst würfeln (Config [Rollen] SelbstWuerfeln, Standard an, Wunsch des Nutzers): als Host ersetzt die Mod
/// während RoleManager.SelectRoles jedes Würfelergebnis des Spiels (HashRandom) durch den Kryptozufall von .NET.
/// Die Rollenvergabe selbst bleibt komplett die des Spiels (welche Rollen es gibt, wie viele Impostor, wer gezogen
/// wird) – nur die Würfel sind unsere. Außerhalb der Rollenvergabe bleibt HashRandom unverändert.
///
/// Bewusst so simpel – zwei frühere Wege sind im echten Spiel gescheitert:
///   1.3.2: alle RpcSetRole zurückhalten und neu senden → erste Runde fror beim Intro ein.
///   1.3.3: Prefix auf LogicRoleSelectionNormal.AssignRolesForTeam (virtuell, Nullable- und Il2Cpp-Listen-Parameter)
///          → Absturz in coreclr.dll, sobald das Spiel die Methode aufrief. Solche Signaturen nicht patchen!
/// </summary>
internal static class RoleShuffle
{
    private static bool _active;
    private static int _frame;
    private static int _rolls;
    private static int _thread; // nur der Spiel-Thread würfelt neu (Time.frameCount geht nur dort)

    public static bool Enabled => CrewStatsPlugin.ShuffleRoles.Value;

    /// <summary>RoleManager.SelectRoles (Prefix).</summary>
    public static void BeforeSelectRoles()
    {
        var client = AmongUsClient.Instance;
        _active = Enabled && client != null && client.AmHost;
        _frame = Time.frameCount;
        _thread = System.Environment.CurrentManagedThreadId;
        _rolls = 0;
    }

    /// <summary>RoleManager.SelectRoles (Postfix).</summary>
    public static void AfterSelectRoles()
    {
        if (!_active) return;
        _active = false;
        CrewStatsPlugin.Logger.LogInfo(_rolls > 0
            ? $"Rollen selbst gewürfelt ({_rolls} Würfe mit echtem Zufall)."
            : "Rollen selbst würfeln: das Spiel hat bei der Rollenvergabe nicht gewürfelt – Rollen wie vom Spiel.");
    }

    /// <summary>HashRandom.FastNext/Next(max) (Postfix): Ergebnis in [0, max) ersetzen, nur während der Rollenvergabe.</summary>
    public static void Replace(int maxExclusive, ref int result) => Replace(0, maxExclusive, ref result);

    /// <summary>HashRandom.Next(min, max) (Postfix): Ergebnis in [min, max) ersetzen, nur während der Rollenvergabe.</summary>
    public static void Replace(int minInclusive, int maxExclusive, ref int result)
    {
        if (!_active || System.Environment.CurrentManagedThreadId != _thread) return;
        if (Time.frameCount != _frame)
        {
            _active = false; // SelectRoles ist ohne Postfix zu Ende gegangen – ab jetzt wieder der Zufall des Spiels
            return;
        }
        if (maxExclusive <= minInclusive) return;
        result = RandomNumberGenerator.GetInt32(minInclusive, maxExclusive);
        _rolls++;
    }

    /// <summary>Spielende/Verlassen.</summary>
    public static void Reset() => _active = false;
}
