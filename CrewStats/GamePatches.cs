using System;
using AmongUs.GameOptions;
using HarmonyLib;
using InnerNet;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace CrewStats;

// Alle Patches lesen nur mit (Postfix/Prefix ohne Rückgabewert) und verändern das Spiel nicht –
// Ausnahmen: die zusätzlichen Schalter im Einstellungsmenü, die optionale Hausregel
// „Kein Skip bei Notfall“ (CastVote-Prefix, nur als Host und nur wenn eingeschaltet) und die
// Rollenvergabe (nur als Host: eigene Würfel per HashRandom-Postfix während SelectRoles; Rollenwünsche aus Discord
// per RpcSetRole-Prefix, nur wenn es Wünsche gibt). Mit The Other Roles liest die Mod zusätzlich (per Reflection,
// siehe TorBridge) Rollen, Gewinner, Tode und wer im Meeting stumm ist.
// Jeder Patch fängt eigene Fehler ab, damit die Mod das Spiel niemals stören kann.

[HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.CoBegin))]
internal static class IntroCutscenePatch
{
    public static void Prefix()
    {
        Safe.Run(GameRecorder.OnGameStart);
        Safe.Run(SettingsReporter.OnGameStart);
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
internal static class MurderPlayerPatch
{
    public static void Postfix(PlayerControl __instance, PlayerControl target, MurderResultFlags resultFlags)
    {
        if (resultFlags.HasFlag(MurderResultFlags.Succeeded))
            Safe.Run(() => GameRecorder.OnKill(__instance, target));
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.StartMeeting))]
internal static class StartMeetingPatch
{
    public static void Postfix(PlayerControl __instance, NetworkedPlayerInfo target)
    {
        Safe.Run(() => NoSkipRule.OnMeetingStarted(target));
        Safe.Run(() => GameRecorder.OnMeetingStarted(__instance, target));
    }
}

// ---------- Hausregel „Kein Skip bei Notfall“ ----------

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.CastVote))]
internal static class CastVotePatch
{
    // false = Original überspringen (Stimme zählt nicht). Bei einem Fehler zählt die Stimme normal.
    public static bool Prefix(MeetingHud __instance, PlayerId srcPlayerId, PlayerId suspectPlayerId)
    {
        try
        {
            return NoSkipRule.AllowVote(__instance, srcPlayerId, suspectPlayerId);
        }
        catch (Exception exc)
        {
            CrewStatsPlugin.Logger.LogError($"Kein-Skip-Regel-Fehler (Stimme zählt normal): {exc}");
            return true;
        }
    }
}

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.VotingComplete))]
internal static class VotingCompletePatch
{
    public static void Postfix(Il2CppStructArray<MeetingHud.VoterState> states, NetworkedPlayerInfo exiled, bool tie)
        => Safe.Run(() => GameRecorder.OnVotingComplete(states, exiled, tie));
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
internal static class GameEndPatch
{
    private static int _rawReason;

    // Prefix: zu diesem Zeitpunkt existieren die Spielerdaten noch. Als Erstes, damit der Grund noch nicht von
    // The Other Roles ersetzt ist (TOR macht aus eigenen Spielenden wie „Jester gewinnt“ ImpostorsByKill).
    [HarmonyPriority(Priority.First)]
    public static void Prefix(EndGameResult endGameResult)
    {
        Safe.Run(AutoMute.Stop);
        Safe.Run(RoleWishes.Reset);
        Safe.Run(RoleShuffle.Reset);
        _rawReason = (int)endGameResult.GameOverReason;
        if (!GameRecorder.IsTorRound) Safe.Run(() => GameRecorder.OnGameEnd(endGameResult.GameOverReason));
    }

    // Mit The Other Roles erst nach TORs Postfix: dann stehen die echten Gewinner in EndGameResult.CachedWinners
    [HarmonyPriority(Priority.Last)]
    [HarmonyAfter(TorBridge.PluginId)]
    public static void Postfix()
    {
        if (GameRecorder.IsTorRound) Safe.Run(() => GameRecorder.OnGameEndTor(_rawReason));
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.ExitGame))]
internal static class ExitGamePatch
{
    public static void Prefix()
    {
        Safe.Run(AutoMute.Stop);
        Safe.Run(LobbyReporter.Close);
        Safe.Run(RoleWishes.Reset);
        Safe.Run(RoleShuffle.Reset);
        Safe.Run(GameRecorder.OnExitGame); // nur für den Freeplay-Test (Config: Debug.RecordFreeplay)
    }
}

// ---------- Rollenvergabe: selbst würfeln + Rollenwünsche aus Discord (nur als Host) ----------

[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.BeginGame))]
internal static class BeginGamePatch
{
    public static void Postfix() => Safe.Run(RoleWishes.OnStartPressed);
}

[HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
internal static class SelectRolesPatch
{
    public static void Prefix()
    {
        Safe.Run(RoleShuffle.BeforeSelectRoles);
        Safe.Run(RoleWishes.BeforeSelectRoles);
    }

    // Als Erstes: die zurückgehaltenen Spiel-Rollen (Rollenwünsche) müssen verteilt sein, bevor The Other Roles
    // in seinem SelectRoles-Postfix anhand von Impostor/Crewmate seine Rollen vergibt.
    [HarmonyPriority(Priority.First)]
    [HarmonyBefore(TorBridge.PluginId)]
    public static void Postfix()
    {
        Safe.Run(RoleShuffle.AfterSelectRoles); // erst die eigenen Würfel abschalten
        Safe.Run(RoleWishes.AfterSelectRoles);
    }
}

// Rollen selbst würfeln: während SelectRoles die Würfelergebnisse des Spiels ersetzen (RoleShuffle).
// Nur einfache statische int-Methoden patchen – Patches auf Methoden mit Nullable-/Il2Cpp-Listen-Parametern
// haben in 1.3.3 die .NET-Laufzeit abstürzen lassen.

[HarmonyPatch(typeof(HashRandom), nameof(HashRandom.FastNext), typeof(int))]
internal static class HashRandomFastNextPatch
{
    public static void Postfix(int maxInt, ref int __result)
    {
        try
        {
            RoleShuffle.Replace(maxInt, ref __result);
        }
        catch
        {
            // Ergebnis des Spiels bleibt
        }
    }
}

[HarmonyPatch(typeof(HashRandom), nameof(HashRandom.Next), typeof(int))]
internal static class HashRandomNextPatch
{
    public static void Postfix(int maxInt, ref int __result)
    {
        try
        {
            RoleShuffle.Replace(maxInt, ref __result);
        }
        catch
        {
            // Ergebnis des Spiels bleibt
        }
    }
}

[HarmonyPatch(typeof(HashRandom), nameof(HashRandom.Next), typeof(int), typeof(int))]
internal static class HashRandomNextRangePatch
{
    public static void Postfix(int minInt, int maxInt, ref int __result)
    {
        try
        {
            RoleShuffle.Replace(minInt, maxInt, ref __result);
        }
        catch
        {
            // Ergebnis des Spiels bleibt
        }
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSetRole))]
internal static class RpcSetRolePatch
{
    // false = Rolle noch nicht senden (wird erst gesammelt, siehe RoleWishes). Bei einem Fehler normal weiter.
    public static bool Prefix(PlayerControl __instance, RoleTypes roleType, bool canOverrideRole)
    {
        try
        {
            return RoleWishes.OnRpcSetRole(__instance, roleType, canOverrideRole);
        }
        catch (Exception exc)
        {
            CrewStatsPlugin.Logger.LogError($"Rollenwunsch-Fehler (Rolle wird normal gesetzt): {exc}");
            return true;
        }
    }
}

// ---------- Auto-Mute ----------

[HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
internal static class HudUpdatePatch
{
    public static void Postfix()
    {
        AutoMute.Tick(); // alle fangen eigene Fehler ab
        LobbyReporter.Tick();
    }
}

[HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Start))]
internal static class OptionsMenuPatch
{
    public static void Postfix(OptionsMenuBehaviour __instance) => Safe.Run(() => MenuToggles.Create(__instance));
}

// ---------- Auto-Update ----------

[HarmonyPatch(typeof(VersionShower), nameof(VersionShower.Start))]
internal static class VersionShowerPatch
{
    // Hinweis unter der Versionsnummer im Hauptmenü, wenn ein Update installiert wurde
    public static void Postfix(VersionShower __instance) => Safe.Run(() =>
    {
        TorPacketFix.TryApply();
        if (Updater.Notice != null && __instance.text != null)
            __instance.text.text += $"\n<color=#FFD54F>{Updater.Notice}</color>";
    });
}

internal static class Safe
{
    public static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exc)
        {
            CrewStatsPlugin.Logger.LogError($"CrewStats-Fehler (Spiel läuft normal weiter): {exc}");
        }
    }
}
