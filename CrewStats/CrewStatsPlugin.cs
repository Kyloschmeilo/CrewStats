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
        AutoUpdate = Config.Bind(
            "Update",
            "AutoUpdate",
            true,
            "Beim Spielstart auf GitHub nach einer neuen CrewStats-Version suchen und sie installieren " +
            "(aktiv ab dem nächsten Start).");

        DataDirectory = Path.Combine(Paths.BepInExRootPath, "CrewStats");
        Directory.CreateDirectory(DataDirectory);

        Harmony.PatchAll();
        Log.LogInfo($"CrewStats {Version} geladen – Daten werden nur als Host aufgezeichnet.");

        // Runden, die beim letzten Mal nicht gesendet werden konnten, nachreichen
        Task.Run(RoundUploader.FlushPendingAsync);

        Updater.RemoveLeftovers();
        Task.Run(Updater.CheckAsync);
    }
}
