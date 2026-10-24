using System;
using System.Collections.Generic;
using System.Text.Json;
using AmongUs.GameOptions;
using InnerNet;

namespace CrewStats;

/// <summary>
/// Discord-Auto-Mute: meldet dem Bot über den Webhook, in welcher Phase das Spiel ist und wer tot ist.
/// Aufgaben → Lebende stumm + taub, Tote reden miteinander; Meeting (inkl. Rauswurf-Animation) →
/// Lebende reden, Tote stumm; Spielende/Lobby → alle frei. Gilt im Voice-Channel des Spielers mit der Mod.
/// Funktioniert auch ohne Host zu sein – Meeting und Tote sieht jeder Spieler ohnehin.
/// Mit The Other Roles bleiben im Meeting außerdem Erpresste (Blackmailer) und Eingesperrte (Jailor) stumm – das
/// zeigt TOR im Meeting ohnehin allen an. Ändert sich das während des Meetings, wird neu gemeldet.
/// JSON-Format ↔ Bot: services/AutoMute.kt (beide Seiten gemeinsam ändern!).
/// </summary>
internal static class AutoMute
{
    private enum Phase { Off, Tasks, Meeting }

    private static readonly LatestWebhookSender Sender = new("Auto-Mute");

    private static Phase _sent = Phase.Off; // was der Bot zuletzt bekommen hat
    private static List<string> _sentDead = new(); // Tote beim letzten Phasenwechsel
    private static string _sentSilenced = ""; // Stumme (sortiert, kommagetrennt) der letzten Meldung
    private static DateTime _nextSilenceCheck = DateTime.MinValue;
    private static string _sender = ""; // eigener Friendcode, gemerkt für das Spielende
    private static bool _errorLogged;

    private static readonly TimeSpan SilenceCheckInterval = TimeSpan.FromSeconds(0.5);

    public static bool Enabled
    {
        get => CrewStatsPlugin.AutoMuteEnabled.Value;
        set => CrewStatsPlugin.AutoMuteEnabled.Value = value; // BepInEx speichert die Config sofort
    }

    /// <summary>Läuft jeden Frame (HudManager.Update) und meldet Phasenwechsel.</summary>
    public static void Tick()
    {
        try
        {
            var phase = Enabled ? CurrentPhase() : Phase.Off;
            if (phase != _sent)
            {
                Send(phase);
            }
            else if (phase == Phase.Meeting && TorBridge.Loaded && DateTime.UtcNow >= _nextSilenceCheck)
            {
                // Erpressung/Gefängnis kann sich im Meeting noch ändern (z.B. Jailor stirbt) → nur dann neu melden
                _nextSilenceCheck = DateTime.UtcNow + SilenceCheckInterval;
                if (Key(Silenced()) != _sentSilenced) Send(phase, newPhase: false);
            }
        }
        catch (Exception exc)
        {
            if (_errorLogged) return; // nicht jeden Frame ins Log schreiben
            _errorLogged = true;
            CrewStatsPlugin.Logger.LogError($"Auto-Mute-Fehler (Spiel läuft normal weiter): {exc}");
        }
    }

    /// <summary>Spielende / Spiel verlassen: alle entmuten (HudManager läuft dann evtl. nicht mehr).</summary>
    public static void Stop()
    {
        if (_sent != Phase.Off) Send(Phase.Off);
    }

    private static Phase CurrentPhase()
    {
        var client = AmongUsClient.Instance;
        if (client == null || ShipStatus.Instance == null) return Phase.Off;

        var inGame = client.NetworkMode == NetworkModes.FreePlay
            ? CrewStatsPlugin.RecordFreeplay.Value // Freeplay nur zum Testen
            : client.GameState == InnerNetClient.GameStates.Started;
        if (!inGame) return Phase.Off;

        var options = GameOptionsManager.Instance?.CurrentGameOptions;
        if (options == null || options.GameMode is not (GameModes.Normal or GameModes.NormalFools))
            return Phase.Off; // Hide & Seek hat keine Meetings
        if (TorBridge.Loaded && TorBridge.IsSpecialMode)
            return Phase.Off; // Hide & Seek / Freeplay von The Other Roles auch nicht

        return MeetingHud.Instance != null || ExileController.Instance != null ? Phase.Meeting : Phase.Tasks;
    }

    // Läuft auf dem Spiel-Thread: hier alle Spieldaten lesen, gesendet wird im Hintergrund
    private static void Send(Phase phase, bool newPhase = true)
    {
        _sent = phase;

        var local = PlayerControl.LocalPlayer;
        if (local != null && local.Data != null && !string.IsNullOrEmpty(local.Data.FriendCode))
            _sender = local.Data.FriendCode;

        // Tote nur beim Phasenwechsel melden: ein Kill mitten in der Runde ändert nichts am Voice,
        // sonst würden die Discord-Symbole (stumm/taub) den Tod sofort verraten.
        if (newPhase)
        {
            _sentDead = new List<string>();
            if (phase != Phase.Off && GameData.Instance != null)
            {
                foreach (var info in GameData.Instance.AllPlayers)
                {
                    if (info != null && info.IsDead && !string.IsNullOrEmpty(info.FriendCode))
                        _sentDead.Add(info.FriendCode);
                }
            }
        }

        // Im Meeting stumm: Erpresste/Eingesperrte (The Other Roles), nur Lebende
        var silenced = phase == Phase.Meeting && TorBridge.Loaded ? Silenced() : new List<string>();
        _sentSilenced = Key(silenced);
        _nextSilenceCheck = DateTime.UtcNow + SilenceCheckInterval;

        var content = JsonSerializer.Serialize(new
        {
            crewstats = "automute",
            v = 1,
            phase = phase switch { Phase.Tasks => "tasks", Phase.Meeting => "meeting", _ => "end" },
            sender = _sender,
            dead = _sentDead,
            silenced,
        });
        CrewStatsPlugin.Logger.LogInfo(silenced.Count > 0 ? $"Auto-Mute: {phase} ({silenced.Count} im Meeting stumm)" : $"Auto-Mute: {phase}");
        Sender.Send(content);
    }

    /// <summary>Friendcodes der lebenden Spieler, die im Meeting nicht reden dürfen (sortiert).</summary>
    private static List<string> Silenced()
    {
        var ids = TorBridge.Silenced();
        var codes = new List<string>();
        if (ids.Count == 0 || GameData.Instance == null) return codes;
        foreach (var info in GameData.Instance.AllPlayers)
        {
            if (info != null && ids.Contains(info.PlayerId) && !info.IsDead && !info.Disconnected
                && !string.IsNullOrEmpty(info.FriendCode))
                codes.Add(info.FriendCode);
        }
        codes.Sort(StringComparer.Ordinal);
        return codes;
    }

    private static string Key(List<string> codes) => string.Join(",", codes);
}
