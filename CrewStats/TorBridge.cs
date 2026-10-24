using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace CrewStats;

/// <summary>
/// Anbindung an die Rollen-Mod „The Other Roles“ (GMIA-Version, geprüft gegen 1.4.1) – nur per Reflection,
/// damit CrewStats ohne TOR genauso läuft und eine neue TOR-Version höchstens diese Zusatzinfos kostet
/// (jeder Fehler wird einmal geloggt, dann ist die Funktion aus). Gelesen wird nur, was TOR auf jedem
/// Client ohnehin weiß (Rollen erst nach Spielende bzw. als Host, Erpressung/Gefängnis sieht im Meeting jeder);
/// geändert wird an TOR nichts.
/// </summary>
internal static class TorBridge
{
    /// <summary>BepInEx-Id von The Other Roles (auch GMIA).</summary>
    public const string PluginId = "me.eisbison.theotherroles";

    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static Assembly? _assembly;
    private static DateTime _nextSearch = DateTime.MinValue;
    private static readonly HashSet<string> Failed = new();

    public sealed class PlayerRole
    {
        public string Role { get; init; } = ""; // TOR-nameKey, z.B. "sheriff", "jester", "crewmate"
        public bool Neutral { get; init; }
        public bool Madmate { get; init; }
        public List<string> Modifiers { get; init; } = new(); // z.B. "lover", "mini", "madmate"
    }

    public sealed record Death(byte Victim, byte? Killer, string Reason, DateTime At);

    /// <summary>Ist The Other Roles geladen? (sucht höchstens alle 10 s neu, solange nicht gefunden)</summary>
    public static bool Loaded => TorAssembly != null;

    private static Assembly? TorAssembly
    {
        get
        {
            if (_assembly != null || DateTime.UtcNow < _nextSearch) return _assembly;
            _nextSearch = DateTime.UtcNow.AddSeconds(10);
            _assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "TheOtherRoles" && a.GetType("TheOtherRoles.RoleInfo") != null);
            if (_assembly != null) CrewStatsPlugin.Logger.LogInfo($"The Other Roles erkannt ({Description}) – Rollen werden mit aufgezeichnet.");
            return _assembly;
        }
    }

    /// <summary>„TheOtherRoles GMIA 1.4.1“ (+ Spielmodus, wenn nicht Classic) – für die Rundendaten.</summary>
    public static string Description
    {
        get
        {
            var version = Try("Version", () => TorType("TheOtherRoles.TheOtherRolesPlugin")?.GetField("VersionString", Static)?.GetValue(null) as string);
            var text = "TheOtherRoles GMIA" + (string.IsNullOrEmpty(version) ? "" : " " + version);
            var mode = GameMode;
            return mode is null or "Classic" ? text : $"{text} ({mode})";
        }
    }

    /// <summary>TOR-Spielmodus: Classic, Guesser, HideNSeek oder FreePlay (null = unbekannt).</summary>
    public static string? GameMode => Try("GameMode", () =>
        TorType("TheOtherRoles.TORMapOptions")?.GetField("gameMode", Static)?.GetValue(null)?.ToString());

    /// <summary>TOR-Modi ohne normale Runde (eigenes Hide & Seek, Freeplay) – werden nicht gezählt, kein Auto-Mute.</summary>
    public static bool IsSpecialMode => GameMode is "HideNSeek" or "FreePlay";

    /// <summary>Rolle eines Spielers (inkl. versteckter Rollen und Modifikatoren), null wenn nicht lesbar.</summary>
    public static PlayerRole? RoleOf(PlayerControl player) => Try("Rolle", () =>
    {
        var roleInfo = TorType("TheOtherRoles.RoleInfo");
        var roleId = TorType("TheOtherRoles.RoleId");
        if (roleInfo == null || roleId == null || player == null) return null;
        var get = roleInfo.GetMethod("getRoleInfoForPlayer", Static)!;
        var nameKey = roleInfo.GetField("nameKey", Instance)!;
        var isNeutral = roleInfo.GetField("isNeutral", Instance)!;
        var isModifier = roleInfo.GetField("isModifier", Instance)!;

        // getRoleInfoForPlayer(p, showModifier: true, includeHidden: true, excludeRoles: null)
        if (get.Invoke(null, new object?[] { player, true, true, null }) is not IEnumerable infos) return null;
        string? role = null;
        var neutral = false;
        var modifiers = new List<string>();
        foreach (var info in infos)
        {
            if (info == null) continue;
            var key = nameKey.GetValue(info) as string ?? "";
            if ((bool)isModifier.GetValue(info)!)
            {
                if (key.Length > 0 && !modifiers.Contains(key)) modifiers.Add(key);
            }
            else if (role == null)
            {
                role = key;
                neutral = (bool)isNeutral.GetValue(info)!;
            }
        }

        var hasModifier = TorType("TheOtherRoles.Helpers")?.GetMethod("hasModifier", Static, null,
            new[] { typeof(PlayerControl), roleId }, null);
        bool Has(string id) => hasModifier != null && (bool)hasModifier.Invoke(null, new[] { player, Enum.Parse(roleId, id) })!;
        var madmate = Has("Madmate") || Has("CreatedMadmate");
        if (madmate) modifiers.Add("madmate");

        return new PlayerRole { Role = role ?? "", Neutral = neutral, Madmate = madmate, Modifiers = modifiers };
    });

    /// <summary>
    /// Der echte Grund des Spielendes: TOR ersetzt eigene Gründe (≥ 10, z.B. Jester-Sieg) für das Spiel durch
    /// ImpostorsByKill und merkt sich den echten in OnGameEndPatch.gameOverReason.
    /// </summary>
    public static int? GameOverReason() => Try("Spielende", () =>
        TorType("TheOtherRoles.Patches.OnGameEndPatch")?.GetField("gameOverReason", Static)?.GetValue(null) is { } value
            ? Convert.ToInt32(value)
            : (int?)null);

    /// <summary>Name eines eigenen TOR-Spielendes (z.B. 13 → "JesterWin").</summary>
    public static string? CustomReasonName(int value) => Try("Spielende-Name", () =>
    {
        var type = TorType("TheOtherRoles.Patches.CustomGameOverReason");
        return type == null ? null : Enum.GetName(type, Enum.ToObject(type, value));
    });

    /// <summary>Alle Tode der Runde laut TOR (mit Täter und Art: Kill, Exile, Guess, Jailed, Suicide …).</summary>
    public static List<Death> Deaths() => Try("Tode", () =>
    {
        var history = TorType("TheOtherRoles.GameHistory");
        var deadPlayer = TorType("TheOtherRoles.DeadPlayer");
        if (history == null || deadPlayer == null) return null;
        if (history.GetField("deadPlayers", Static)?.GetValue(null) is not IEnumerable list) return null;
        var player = deadPlayer.GetField("player", Instance)!;
        var killer = deadPlayer.GetField("killerIfExisting", Instance)!;
        var reason = deadPlayer.GetField("deathReason", Instance)!;
        var time = deadPlayer.GetField("timeOfDeath", Instance)!;

        var deaths = new List<Death>();
        foreach (var entry in list)
        {
            if (entry == null) continue;
            try
            {
                var victim = player.GetValue(entry) as PlayerControl;
                if (victim == null) continue; // Spieler weg (Unity-Objekt zerstört)
                var by = killer.GetValue(entry) as PlayerControl;
                deaths.Add(new Death(victim.PlayerId, by != null ? by.PlayerId : (byte?)null,
                    reason.GetValue(entry)?.ToString() ?? "", (DateTime)time.GetValue(entry)!));
            }
            catch
            {
                // einzelner kaputter Eintrag (z.B. Spieler hat das Spiel verlassen)
            }
        }
        return deaths;
    }) ?? new List<Death>();

    /// <summary>
    /// Wer im Meeting nicht reden darf: vom Blackmailer erpresst oder vom Jailor eingesperrt.
    /// Beides zeigt TOR im Meeting allen Spielern an (Symbol am Spieler), verrät also nichts.
    /// </summary>
    public static HashSet<byte> Silenced() => Try("Stumm", () =>
    {
        var result = new HashSet<byte>();
        var blackmailer = TorType("TheOtherRoles.Roles.Blackmailer");
        if (blackmailer != null && Players(blackmailer) is { } blackmailers)
        {
            var target = blackmailer.GetField("blackmailed", Instance)!;
            var owner = blackmailer.GetField("player", Instance)!;
            foreach (var role in blackmailers)
            {
                if (role == null || (owner.GetValue(role) as PlayerControl) == null) continue; // Blackmailer weg
                var victim = target.GetValue(role) as PlayerControl;
                if (victim != null) result.Add(victim.PlayerId);
            }
        }

        var isJailed = TorType("TheOtherRoles.Roles.Jailor")?.GetMethod("isJailed", Static, null, new[] { typeof(byte) }, null);
        if (isJailed != null && GameData.Instance != null)
        {
            foreach (var info in GameData.Instance.AllPlayers)
            {
                if (info != null && (bool)isJailed.Invoke(null, new object[] { info.PlayerId })!)
                    result.Add(info.PlayerId);
            }
        }
        return result;
    }) ?? new HashSet<byte>();

    // ---------- intern ----------

    private static Type? TorType(string name) => TorAssembly?.GetType(name);

    /// <summary>RoleBase&lt;T&gt;.players – die Liste aller Spieler mit dieser Rolle.</summary>
    private static IEnumerable? Players(Type role)
    {
        for (var type = role; type != null; type = type.BaseType)
        {
            var field = type.GetField("players", Static | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(null) as IEnumerable;
        }
        return null;
    }

    /// <summary>Führt einen Lesezugriff aus; nach dem ersten Fehler bleibt diese Funktion aus (TOR geändert?).</summary>
    private static T? Try<T>(string what, Func<T?> read) where T : class => TryCore(what, read);

    private static int? Try(string what, Func<int?> read) => TryCore(what, () => read() is { } v ? (object)v : null) as int?;

    private static T? TryCore<T>(string what, Func<T?> read) where T : class
    {
        if (TorAssembly == null || Failed.Contains(what)) return null;
        try
        {
            return read();
        }
        catch (Exception exc)
        {
            Failed.Add(what);
            CrewStatsPlugin.Logger.LogWarning($"The Other Roles: {what} nicht lesbar (andere TOR-Version?) – ab jetzt ohne: {exc.GetBaseException().Message}");
            return null;
        }
    }
}
