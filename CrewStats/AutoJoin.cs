using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using InnerNet;
using UnityEngine;

namespace CrewStats;

/// <summary>
/// Auto-Join: im Hauptmenü fragt die Mod den Bot nach der neuesten Lobby (die der Bot im Codes-Channel ankündigt)
/// und tritt ihr bei. Für alle Spieler, nicht nur Hosts – mit eigener Auto-Join-URL (Discord: /autojoin), nicht der
/// geheimen Webhook-URL für Runden.
///
/// Ablauf wie bei den Rollenwünschen: Anfrage mit ?wait=true schicken, der Bot bearbeitet genau diese Nachricht mit
/// der Antwort, die Mod liest sie und löscht sie wieder (kein offener Port nötig).
/// Automatisch nur einmal pro Lobby (wer sie verlässt, bleibt draußen); F5 tritt sofort bei, F6 schaltet das
/// automatische Beitreten an/aus. Was die Mod gerade macht, steht unter der Versionsnummer.
/// Netzwerk läuft im Hintergrund, Spiel-Aufrufe nur im Spiel-Thread (Tick).
/// </summary>
internal static class AutoJoin
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan AutoInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(3); // Hauptmenü erst fertig aufbauen lassen
    private static readonly System.Random Jitter = new();

    private static readonly object Lock = new();
    private static readonly HashSet<string> JoinedCodes = new();
    private static bool _busy;
    private static string? _pendingJoin; // vom Hintergrund gefunden, im Spiel-Thread beitreten
    private static string _status = "";
    private static DateTime _nextPoll = DateTime.MinValue;
    private static bool _inMenu;
    private static bool _errorLogged;
    private static DateTime _joiningUntil = DateTime.MinValue; // nicht zweimal gleichzeitig beitreten

    // Statuszeile unter der Versionsnummer
    private static VersionShower? _shower;
    private static string _shownSuffix = "";

    public static void OnVersionShower(VersionShower shower)
    {
        _shower = shower;
        _shownSuffix = "";
    }

    /// <summary>Läuft jeden Frame im Hauptmenü (MainMenuManager.LateUpdate).</summary>
    public static void Tick()
    {
        try
        {
            TickInner();
        }
        catch (Exception exc)
        {
            if (_errorLogged) return;
            _errorLogged = true;
            CrewStatsPlugin.Logger.LogError($"Auto-Join-Fehler (Spiel läuft normal weiter): {exc}");
        }
    }

    private static void TickInner()
    {
        var url = AutoJoinRules.NormalizeUrl(CrewStatsPlugin.AutoJoinUrl.Value);
        if (url == null)
        {
            ShowStatus("");
            return;
        }

        var client = AmongUsClient.Instance;
        var idle = client != null && client.GameState == InnerNetClient.GameStates.NotJoined;
        if (!idle)
        {
            _inMenu = false;
            ShowStatus("");
            return;
        }
        if (!_inMenu)
        {
            _inMenu = true; // gerade im Hauptmenü angekommen
            _nextPoll = DateTime.UtcNow + FirstDelay;
            lock (Lock) _pendingJoin = null; // nichts von vorher
        }

        if (Input.GetKeyDown(CrewStatsPlugin.AutoJoinToggleKey.Value))
        {
            CrewStatsPlugin.AutoJoinAutomatic.Value = !CrewStatsPlugin.AutoJoinAutomatic.Value;
            SetStatus(CrewStatsPlugin.AutoJoinAutomatic.Value ? "Auto-Join an – suche Lobby …" : "Auto-Join aus");
            _nextPoll = DateTime.UtcNow;
        }
        var manual = Input.GetKeyDown(CrewStatsPlugin.AutoJoinKey.Value);
        if (DateTime.UtcNow < _joiningUntil)
        {
            ShowStatus(CurrentStatus());
            return;
        }

        string? join;
        lock (Lock)
        {
            join = _pendingJoin;
            _pendingJoin = null;
        }
        if (join != null)
        {
            Join(client!, join);
            ShowStatus(CurrentStatus());
            return;
        }

        var automatic = CrewStatsPlugin.AutoJoinAutomatic.Value;
        if (manual || (automatic && DateTime.UtcNow >= _nextPoll))
        {
            bool start;
            lock (Lock)
            {
                start = !_busy;
                if (start) _busy = true;
            }
            if (start)
            {
                _nextPoll = DateTime.UtcNow + AutoInterval + TimeSpan.FromSeconds(Jitter.NextDouble() * 3);
                if (manual) SetStatus("Auto-Join: frage den Bot …");
                Task.Run(() => FetchAsync(url, manual));
            }
        }
        else if (!automatic && !manual && CurrentStatus().Length == 0)
        {
            SetStatus($"Auto-Join aus – {CrewStatsPlugin.AutoJoinKey.Value}: neueste Lobby beitreten");
        }

        ShowStatus(CurrentStatus());
    }

    private static void Join(AmongUsClient client, string code)
    {
        lock (Lock) JoinedCodes.Add(code);
        _joiningUntil = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        SetStatus($"Auto-Join: trete Lobby {code} bei …");
        CrewStatsPlugin.Logger.LogInfo($"Auto-Join: trete Lobby {code} bei");
        client.NetworkMode = NetworkModes.OnlineGame;
        client.StartCoroutine(client.CoJoinOnlineGameFromCode(GameCode.GameNameToInt(code)));
    }

    private static async Task FetchAsync(string url, bool manual)
    {
        try
        {
            var lobby = await RequestAsync(url);
            if (lobby == null)
            {
                SetStatus("Auto-Join: keine Antwort vom Bot");
                return;
            }

            AutoJoinRules.Decision decision;
            lock (Lock) decision = AutoJoinRules.Decide(lobby, JoinedCodes, manual);
            switch (decision)
            {
                case AutoJoinRules.Decision.Join:
                    lock (Lock) _pendingJoin = lobby.Code;
                    break;
                case AutoJoinRules.Decision.Started:
                    SetStatus($"Auto-Join: Runde in {lobby.Code} läuft – warte …");
                    break;
                case AutoJoinRules.Decision.Full:
                    SetStatus($"Auto-Join: Lobby {lobby.Code} ist voll ({lobby.Players}/{lobby.Max})");
                    break;
                case AutoJoinRules.Decision.AlreadyJoined:
                    SetStatus($"Auto-Join: warte auf neue Lobby ({CrewStatsPlugin.AutoJoinKey.Value}: {lobby.Code} erneut beitreten)");
                    break;
                default:
                    SetStatus("Auto-Join: gerade keine offene Lobby – warte …");
                    break;
            }
        }
        catch (Exception exc)
        {
            SetStatus("Auto-Join: Bot nicht erreichbar");
            CrewStatsPlugin.Logger.LogWarning($"Auto-Join: Anfrage fehlgeschlagen: {exc.Message}");
        }
        finally
        {
            lock (Lock) _busy = false;
        }
    }

    /// <summary>Anfrage senden, auf die Antwort des Bots warten (er bearbeitet unsere Nachricht), Nachricht löschen.</summary>
    private static async Task<LobbyInfo?> RequestAsync(string url)
    {
        using var posted = await Http.PostAsync(url + "?wait=true", Body(AutoJoinRules.RequestContent()));
        if (!posted.IsSuccessStatusCode)
        {
            CrewStatsPlugin.Logger.LogWarning($"Auto-Join: Discord hat die Anfrage abgelehnt: {(int)posted.StatusCode} {posted.ReasonPhrase}" +
                (posted.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized ? " – Auto-Join-URL ungültig? (/autojoin im Discord)" : ""));
            return null;
        }
        string messageId;
        using (var json = JsonDocument.Parse(await posted.Content.ReadAsStringAsync()))
            messageId = json.RootElement.GetProperty("id").GetString() ?? "";
        var messageUrl = $"{url}/messages/{messageId}";

        try
        {
            var deadline = DateTime.UtcNow + ReplyTimeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollInterval);
                using var response = await Http.GetAsync(messageUrl);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    await Task.Delay(RetryAfter(response));
                    continue;
                }
                if (!response.IsSuccessStatusCode) continue;

                string? content;
                using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
                    content = json.RootElement.TryGetProperty("content", out var c) ? c.GetString() : null;
                var reply = AutoJoinRules.ParseReply(content);
                if (reply != null) return reply;
            }
            CrewStatsPlugin.Logger.LogWarning("Auto-Join: keine Antwort vom Bot (läuft er?).");
            return null;
        }
        finally
        {
            try
            {
                using var _ = await Http.DeleteAsync(messageUrl); // sonst räumt der Bot nach 30 s auf
            }
            catch
            {
                // nicht schlimm
            }
        }
    }

    private static StringContent Body(string content) =>
        new(JsonSerializer.Serialize(new
        {
            username = "CrewStats Auto-Join",
            content,
            flags = 4096, // keine Benachrichtigung im Channel
            allowed_mentions = new { parse = Array.Empty<string>() },
        }), Encoding.UTF8, "application/json");

    private static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 1;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0.2, 5));
    }

    private static void SetStatus(string status)
    {
        lock (Lock) _status = status;
    }

    private static string CurrentStatus()
    {
        lock (Lock) return _status;
    }

    /// <summary>Statuszeile unter der Versionsnummer (andere Zusätze, z.B. von The Other Roles, bleiben stehen).</summary>
    private static void ShowStatus(string status)
    {
        try
        {
            ShowStatusInner(status);
        }
        catch
        {
            _shower = null; // Menü schon weg – beim nächsten Hauptmenü kommt ein neues
        }
    }

    private static void ShowStatusInner(string status)
    {
        var text = _shower != null ? _shower.text : null;
        if (text == null) return;
        var suffix = status.Length > 0 ? $"\n<color=#1FBF7A>{status}</color>" : "";
        if (suffix == _shownSuffix) return;
        var current = text.text ?? "";
        if (_shownSuffix.Length > 0)
        {
            var index = current.LastIndexOf(_shownSuffix, StringComparison.Ordinal);
            if (index >= 0) current = current.Remove(index, _shownSuffix.Length);
        }
        text.text = current + suffix;
        _shownSuffix = suffix;
    }
}
