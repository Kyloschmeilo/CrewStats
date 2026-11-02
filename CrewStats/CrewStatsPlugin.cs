using System.IO;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace CrewStats;

/// <summary>
/// Host-only-Mod: schreibt als Host still die Rundendaten mit und schickt sie NACH
/// Rundenende per Discord-Webhook an den Among-Us-Manager-Bot.
/// Während der Runde wird nichts angezeigt – die Mod verschafft keinen Vorteil.
/// Dazu optional Discord-Auto-Mute (Schalter im ESC-Menü), siehe <see cref="AutoMute"/>.
/// </summary>
[BepInAutoPlugin("de.amongusmanager.crewstats")]
[BepInProcess("Among Us.exe")]
public partial class CrewStatsPlugin : BasePlugin
{
    internal static ManualLogSource Logger { get; private set; } = null!;
    internal static ConfigEntry<string> WebhookUrl { get; private set; } = null!;
    internal static ConfigEntry<bool> RecordFreeplay { get; private set; } = null!;
    internal static ConfigEntry<bool> AutoMuteEnabled { get; private set; } = null!;
    internal static ConfigEntry<bool> AutoUpdate { get; private set; } = null!;
    internal static ConfigEntry<bool> AnnounceLobby { get; private set; } = null!;
    internal static ConfigEntry<bool> NoSkipOnEmergency { get; private set; } = null!;
    internal static ConfigEntry<bool> RoleWishesEnabled { get; private set; } = null!;
    internal static ConfigEntry<bool> ShuffleRoles { get; private set; } = null!;
    internal static ConfigEntry<string> AutoJoinUrl { get; private set; } = null!;
    internal static ConfigEntry<bool> AutoJoinAutomatic { get; private set; } = null!;
    internal static ConfigEntry<UnityEngine.KeyCode> AutoJoinKey { get; private set; } = null!;
    internal static ConfigEntry<UnityEngine.KeyCode> AutoJoinToggleKey { get; private set; } = null!;
    internal static ConfigEntry<bool> ShowOverlay { get; private set; } = null!;
    internal static ConfigEntry<bool> ShowLobbyInfo { get; private set; } = null!;
    internal static ConfigEntry<bool> ShowRoundSummary { get; private set; } = null!;
    internal static ConfigEntry<bool> ShowStatus { get; private set; } = null!;
    internal static ConfigEntry<bool> ShowUpdateNotice { get; private set; } = null!;
    internal static ConfigEntry<bool> RetryUploads { get; private set; } = null!;
    internal static ConfigEntry<UnityEngine.KeyCode> SettingsKey { get; private set; } = null!;
    internal static string DataDirectory { get; private set; } = null!;

    public Harmony Harmony { get; } = new(Id);

    public override void Load()
    {
        Logger = Log;
        WebhookUrl = Config.Bind(
            "Discord",
            "WebhookUrl",
            "",
            "Webhook-URL, die der Bot bei /mod-setup anzeigt. Leer = Runden nur lokal speichern.");
        RecordFreeplay = Config.Bind(
            "Debug",
            "RecordFreeplay",
            false,
            "Nur zum Testen: zeichnet auch Freeplay auf. Die Runde wird beim Verlassen von Freeplay " +
            "ohne Sieger (UNKNOWN) gesendet, es gibt also keine XP. Auto-Mute funktioniert dann auch in Freeplay.");
        AutoMuteEnabled = Config.Bind(
            "AutoMute",
            "Enabled",
            false,
            "Discord-Auto-Mute: Aufgaben = alle stumm, Meeting = Lebende reden, Spielende = alle entmutet. " +
            "Wird im Spiel im Einstellungsmenü (ESC) an- und ausgeschaltet.");
        NoSkipOnEmergency = Config.Bind(
            "Regeln",
            "NoSkipOnEmergency",
            false,
            "Hausregel als Host: Bei Meetings über den Notfall-Knopf werden Skip-Stimmen abgelehnt, " +
            "die Spieler müssen jemanden wählen. Schalter im Spiel: ESC -> Allgemein -> \"Kein Skip bei Notfall\".");
        RoleWishesEnabled = Config.Bind(
            "Rollenwuensche",
            "Enabled",
            true,
            "Als Host die Rollenwünsche aus Discord (/rollenwunsch, Admins: /rolle-zuweisen) bei der Rollenvergabe " +
            "berücksichtigen. " +
            "Anzahl Impostor und Spezialrollen bleiben wie eingestellt.");
        ShuffleRoles = Config.Bind(
            "Rollen",
            "SelbstWuerfeln",
            true,
            "Als Host würfelt die Mod selbst (echter Zufall), wer welche Rolle bekommt – jeder hat dieselbe Chance. " +
            "Welche Rollen es gibt und wie viele Impostor, bestimmen weiter die Lobby-Einstellungen. " +
            "false = das Spiel verteilt die Rollen wie ohne Mod.");
        AnnounceLobby = Config.Bind(
            "Lobby",
            "Announce",
            true,
            "Als Host die eigene Lobby (Code, Map, Spielerzahl) automatisch im Discord ankündigen. " +
            "Während der Runde ist das Wettbüro dann gesperrt.");
        AutoJoinUrl = Config.Bind(
            "AutoJoin",
            "Url",
            "",
            "Auto-Join-URL, die der Bot bei /autojoin anzeigt (nicht die geheime WebhookUrl). " +
            "Leer = kein Auto-Join.");
        AutoJoinAutomatic = Config.Bind(
            "AutoJoin",
            "Automatisch",
            true,
            "Im Hauptmenü automatisch der neuesten Lobby aus dem Discord beitreten (jede Lobby nur einmal). " +
            "Im Spiel mit der Taste AnAus umschaltbar.");
        AutoJoinKey = Config.Bind(
            "AutoJoin",
            "TasteJetzt",
            UnityEngine.KeyCode.F5,
            "Im Hauptmenü: sofort der neuesten Lobby beitreten (auch wenn du sie schon einmal verlassen hast).");
        AutoJoinToggleKey = Config.Bind(
            "AutoJoin",
            "TasteAnAus",
            UnityEngine.KeyCode.F6,
            "Im Hauptmenü: automatisches Beitreten an-/ausschalten.");
        AutoUpdate = Config.Bind(
            "Update",
            "AutoUpdate",
            true,
            "Beim Spielstart auf GitHub nach einer neuen CrewStats-Version suchen und sie installieren " +
            "(aktiv ab dem nächsten Start).");

        ShowOverlay = Config.Bind(
            "Komfort",
            "Overlay",
            true,
            "In der Lobby links deine Stats aus dem Discord zeigen (Level, XP, Siege, letzte Rolle). " +
            "Braucht die Auto-Join-URL (oder als Host die WebhookUrl).");
        ShowLobbyInfo = Config.Bind(
            "Komfort",
            "LobbyInfo",
            true,
            "In der Lobby zeigen, wer mit Discord verknüpft ist (mit Level und Siegen).");
        ShowRoundSummary = Config.Bind(
            "Komfort",
            "Rundenzusammenfassung",
            true,
            "Nach der Runde deine Rolle, Kills, Aufgaben und die XP aus dem Discord zeigen (nichts während der Runde).");
        ShowStatus = Config.Bind(
            "Komfort",
            "Verbindungsstatus",
            true,
            "Unten links im Hauptmenü und in der Lobby: Punkt für die Verbindung zum Bot (grün/gelb/rot) mit Fehlerhinweis.");
        ShowUpdateNotice = Config.Bind(
            "Komfort",
            "UpdateHinweis",
            true,
            "Beim Start nach einer neuen CrewStats-Version suchen und im Spiel darauf hinweisen " +
            "(auch wenn [Update] AutoUpdate aus ist).");
        RetryUploads = Config.Bind(
            "Komfort",
            "Nachsenden",
            true,
            "Runden, die nicht gesendet werden konnten (z.B. ohne Internet), liegen in BepInEx/CrewStats/pending " +
            "und werden alle 2 Minuten erneut gesendet. false = nur beim Spielstart und nach der nächsten Runde.");
        SettingsKey = Config.Bind(
            "Komfort",
            "TasteMenue",
            UnityEngine.KeyCode.F7,
            "Öffnet/schließt das CrewStats-Einstellungsmenü (alle Funktionen einzeln an/aus, Discord verknüpfen).");

        DataDirectory = Path.Combine(Paths.BepInExRootPath, "CrewStats");
        Directory.CreateDirectory(DataDirectory);

        Harmony.PatchAll();
        Log.LogInfo($"CrewStats {Version} geladen – Daten werden nur als Host aufgezeichnet.");

        // Runden, die beim letzten Mal nicht gesendet werden konnten, nachreichen
        Task.Run(RoundUploader.FlushPendingAsync);

        Updater.RemoveLeftovers();
        Task.Run(Updater.CheckAsync);

        // Anzeige: Verbindungsstatus, Stats, Rundenzusammenfassung, Einstellungsmenü (F7)
        try
        {
            AddComponent<ComfortUi>();
        }
        catch (System.Exception exc)
        {
            Log.LogError($"Komfort-Anzeige konnte nicht starten (Rest der Mod läuft normal): {exc}");
        }
    }
}
