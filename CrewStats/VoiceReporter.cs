using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AmongUs.GameOptions;
using InnerNet;

namespace CrewStats;

/// <summary>
/// Proximity-Voice im Live-Tab der Stats-Website: meldet als Host, wo alle stehen, wer tot ist, wer im Vent sitzt und
/// ob gerade ein Meeting läuft – an die Website-Adresse aus <c>[AutoJoin] Url</c> (nur eine Website-URL, kein Webhook:
/// 5 Meldungen pro Sekunde wären für Discord zu viel). Die Website rechnet daraus, wer wen wie laut hört; die
/// Positionen bekommt kein anderer Spieler. Im Spiel wird nichts angezeigt oder verändert.
/// Ohne Zuhörer nur alle 2 s, sonst alle 0,2 s, immer höchstens eine Anfrage gleichzeitig.
/// </summary>
internal static class VoiceReporter
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static DateTime _next;
    private static int _busy; // 1 = Anfrage unterwegs
    private static volatile int _listeners;
    private static int _loggedListeners = -1;
    private static bool _errorLogged;
    private static bool _sendErrorLogged;

    /// <summary>Läuft jeden Frame (HudManager.Update – in Lobby und Runde).</summary>
    public static void Tick()
    {
        try
        {
            if (!CrewStatsPlugin.VoiceEnabled.Value) return;
            if (DateTime.UtcNow < _next || Volatile.Read(ref _busy) == 1) return;
            var url = AutoJoinRules.NormalizeUrl(CrewStatsPlugin.AutoJoinUrl.Value);
            if (url == null || !AutoJoinRules.IsWebsiteUrl(url)) return;
            var content = Current();
            if (content == null) return;
            _next = DateTime.UtcNow + VoiceRules.Interval(_listeners);
            Interlocked.Exchange(ref _busy, 1);
            _ = Task.Run(() => SendAsync(url, content));
        }
        catch (Exception exc)
        {
            if (_errorLogged) return;
            _errorLogged = true;
            CrewStatsPlugin.Logger.LogError($"Voice-Fehler (Spiel läuft normal weiter): {exc}");
        }
    }

    /// <summary>Spieldaten lesen (Spiel-Thread). null = gerade nichts zu melden (kein Host, offline, Freeplay …).</summary>
    private static string? Current()
    {
        var client = AmongUsClient.Instance;
        if (client == null || !client.AmHost || client.NetworkMode != NetworkModes.OnlineGame) return null;
        var started = client.GameState == InnerNetClient.GameStates.Started;
        if (!started && client.GameState != InnerNetClient.GameStates.Joined) return null;
        var local = PlayerControl.LocalPlayer;
        if (local == null || local.Data == null || string.IsNullOrEmpty(local.Data.FriendCode)) return null;

        var options = GameOptionsManager.Instance?.CurrentGameOptions;
        var normal = options != null && (options.GameMode is GameModes.Normal or GameModes.NormalFools)
            && !(TorBridge.Loaded && TorBridge.IsSpecialMode);
        var meeting = MeetingHud.Instance != null || ExileController.Instance != null;
        var phase = VoiceRules.Phase(started && ShipStatus.Instance != null, normal, meeting);
        var silenced = phase == "meeting" && TorBridge.Loaded ? TorBridge.Silenced() : null;

        var players = new List<VoiceRules.Player>();
        foreach (var control in PlayerControl.AllPlayerControls.ToArray())
        {
            var info = control != null ? control.Data : null;
            if (info == null || info.Disconnected || string.IsNullOrEmpty(info.FriendCode)) continue;
            var pos = control!.GetTruePosition();
            var outfit = info.DefaultOutfit;
            players.Add(new VoiceRules.Player(
                info.FriendCode, info.PlayerName ?? "?", outfit != null ? outfit.ColorId : -1, pos.x, pos.y,
                info.IsDead, control.inVent, silenced != null && silenced.Contains(info.PlayerId)));
        }
        return VoiceRules.Build(local.Data.FriendCode, GameCode.IntToGameName(client.GameId) ?? "", phase, players);
    }

    private static async Task SendAsync(string url, string content)
    {
        try
        {
            using var response = await Http.PostAsync(url, new StringContent(content, Encoding.UTF8, "application/json"));
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _listeners = 0;
                if (!_sendErrorLogged)
                {
                    _sendErrorLogged = true;
                    CrewStatsPlugin.Logger.LogWarning($"Voice: Website antwortet mit {(int)response.StatusCode} (alte Website-Version?)");
                }
                return;
            }
            var listeners = VoiceRules.Listeners(text);
            _listeners = Math.Max(0, listeners);
            if (listeners >= 0 && listeners != _loggedListeners)
            {
                _loggedListeners = listeners;
                CrewStatsPlugin.Logger.LogInfo($"Voice: {listeners} im Browser-Voice");
            }
        }
        catch (Exception exc)
        {
            _listeners = 0;
            if (!_sendErrorLogged)
            {
                _sendErrorLogged = true;
                CrewStatsPlugin.Logger.LogWarning($"Voice: Website nicht erreichbar ({exc.Message})");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }
}
