using System;
using HarmonyLib;
using InnerNet;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace CrewStats;

// Alle Patches lesen nur mit (Postfix/Prefix ohne Rückgabewert) und verändern das Spiel nicht –
// Ausnahmen: die zusätzlichen Schalter im Einstellungsmenü und die optionale Hausregel
// „Kein Skip bei Notfall“ (CastVote-Prefix, nur als Host und nur wenn eingeschaltet).
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

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
internal static class MeetingHudStartPatch
{
    public static void Postfix() => Safe.Run(NoSkipRule.OnMeetingHudShown);
}

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
        Safe.Run(GameRecorder.OnExitGame); // nur für den Freeplay-Test (Config: Debug.RecordFreeplay)
    }
}

// ---------- Auto-Mute ----------

[HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
internal static class HudUpdatePatch
{
    public static void Postfix()
    {
        AutoMute.Tick(); // beide fangen eigene Fehler ab
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
