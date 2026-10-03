using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AmongUs.GameOptions;
using InnerNet;

namespace CrewStats;

/// <summary>
/// Discord-Auto-Mute: meldet dem Bot über den Webhook, in welcher Phase das Spiel ist und wer tot ist.
/// Aufgaben → Lebende stumm + taub, Tote reden miteinander; Meeting (inkl. Rauswurf-Animation) →
/// Lebende reden, Tote stumm; Spielende/Lobby → alle frei. Gilt im Voice-Channel des Spielers mit der Mod.
/// Funktioniert auch ohne Host zu sein – Meeting und Tote sieht jeder Spieler ohnehin.
/// JSON-Format ↔ crewbot/services/automute.py (beide Seiten gemeinsam ändern!).
/// </summary>
internal static class AutoMute
{
    private enum Phase { Off, Tasks, Meeting }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly SemaphoreSlim SendLock = new(1, 1);

    private static Phase _sent = Phase.Off; // was der Bot zuletzt bekommen hat
    private static int _sequence; // nur die neueste Phase wird wirklich gesendet
    private static string _sender = ""; // eigener Friendcode, gemerkt für das Spielende
    private static bool _errorLogged;

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
            if (phase != _sent) Send(phase);
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

        return MeetingHud.Instance != null || ExileController.Instance != null ? Phase.Meeting : Phase.Tasks;
    }

    // Läuft auf dem Spiel-Thread: hier alle Spieldaten lesen, gesendet wird im Hintergrund
    private static void Send(Phase phase)
    {
        _sent = phase;

        var local = PlayerControl.LocalPlayer;
        if (local != null && local.Data != null && !string.IsNullOrEmpty(local.Data.FriendCode))
            _sender = local.Data.FriendCode;

        // Tote nur beim Phasenwechsel melden: ein Kill mitten in der Runde ändert nichts am Voice,
        // sonst würden die Discord-Symbole (stumm/taub) den Tod sofort verraten.
        var dead = new List<string>();
        if (phase != Phase.Off && GameData.Instance != null)
        {
            foreach (var info in GameData.Instance.AllPlayers)
            {
                if (info != null && info.IsDead && !string.IsNullOrEmpty(info.FriendCode))
                    dead.Add(info.FriendCode);
            }
        }

        var content = JsonSerializer.Serialize(new
        {
            crewstats = "automute",
            v = 1,
            phase = phase switch { Phase.Tasks => "tasks", Phase.Meeting => "meeting", _ => "end" },
            sender = _sender,
            dead,
        });
        var sequence = Interlocked.Increment(ref _sequence);
        CrewStatsPlugin.Logger.LogInfo($"Auto-Mute: {phase}");
        Task.Run(() => PostAsync(content, sequence));
    }

    private static async Task PostAsync(string content, int sequence)
    {
        var url = RoundUploader.WebhookUrl;
        if (url == null)
        {
            CrewStatsPlugin.Logger.LogWarning("Auto-Mute: keine gültige Webhook-URL in der Config.");
            return;
        }

        await SendLock.WaitAsync();
        try
        {
            if (sequence != Volatile.Read(ref _sequence)) return; // inzwischen gibt es eine neuere Phase

            var body = JsonSerializer.Serialize(new
            {
                username = "CrewStats",
                content,
                flags = 4096, // keine Benachrichtigung im Channel
                allowed_mentions = new { parse = Array.Empty<string>() },
            });
            using var response = await Http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
            if (!response.IsSuccessStatusCode)
                CrewStatsPlugin.Logger.LogWarning($"Auto-Mute: Discord hat abgelehnt: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception exc)
        {
            CrewStatsPlugin.Logger.LogWarning($"Auto-Mute: Senden fehlgeschlagen: {exc.Message}");
        }
        finally
        {
            SendLock.Release();
        }
    }
}
