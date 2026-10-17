using System;
using AmongUs.GameOptions;
using HarmonyLib;
using InnerNet;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace CrewStats;

// Alle Patches lesen nur mit (Postfix/Prefix ohne Rückgabewert) und verändern das Spiel nicht –
// Ausnahmen: die zusätzlichen Schalter im Einstellungsmenü, die optionale Hausregel
// „Kein Skip bei Notfall“ (CastVote-Prefix, nur als Host und nur wenn eingeschaltet) und die
// Rollenvergabe (RpcSetRole-Prefix, nur als Host: Rollen selbst würfeln und Rollenwünsche aus Discord).
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
    // Prefix: zu diesem Zeitpunkt existieren die Spielerdaten noch
    public static void Prefix(EndGameResult endGameResult)
    {
        Safe.Run(AutoMute.Stop);
        Safe.Run(RoleWishes.Reset);
        Safe.Run(() => GameRecorder.OnGameEnd(endGameResult.GameOverReason));
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
    public static void Prefix() => Safe.Run(RoleWishes.BeforeSelectRoles);

    public static void Postfix() => Safe.Run(RoleWishes.AfterSelectRoles);
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
