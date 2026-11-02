using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using InnerNet;

namespace CrewStats;

/// <summary>Ein Spieler in der Lobby-Info.</summary>
internal sealed record LobbyRow(string Name, bool Me, PlayerStats? Stats);

/// <summary>
/// Komfort-Funktionen (alle einzeln abschaltbar, Config [Komfort] bzw. Einstellungsmenü F7): eigene Stats und
/// Lobby-Info in der Lobby, XP für die Rundenzusammenfassung, Verknüpfen per Code. Holt die Daten über
/// <see cref="BotClient"/> (Auto-Join-URL, sonst Webhook-URL) und hält sie für <see cref="ComfortUi"/> bereit.
/// Spiel-Daten werden nur im Spiel-Thread gelesen (<see cref="Tick"/>), Netzwerk läuft im Hintergrund.
/// </summary>
internal static class Comfort
{
    public enum Place { Menu, Lobby, Game }

    private static readonly TimeSpan OwnRefresh = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PlayerStale = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);
    private static readonly double[] SummaryChecks = { 8, 20, 45, 90 }; // Sekunden nach Spielende
    private static readonly Random Jitter = new();

    private static readonly object Lock = new();
    private static readonly Dictionary<string, (PlayerStats Stats, DateTime At)> Cache = new();
    private static StatsReply? _mine;
    private static DateTime _mineAt = DateTime.MinValue;
    private static StatsReply? _arrived; // im Hintergrund angekommen, im Spiel-Thread verarbeiten
    private static bool _busy;
    private static DateTime _nextStats = DateTime.MinValue;
    private static string _playerSet = "";
    private static int _summaryCheck;
    private static Place _lastPlace = Place.Menu;
    private static bool _menuChecked;
    private static bool _errorLogged;
    private static DateTime _nextLook = DateTime.MinValue;

    public static Place Where { get; private set; } = Place.Menu;
    public static StatsReply? Mine
    {
        get
        {
            lock (Lock) return _mine;
        }
    }
    public static IReadOnlyList<LobbyRow> Lobby { get; private set; } = Array.Empty<LobbyRow>();

    // Verknüpfen per Code
    public static string LinkText { get; private set; } = "";
    public static string LinkCode { get; private set; } = "";
    public static DateTime LinkExpires { get; private set; }
    private static bool _linkBusy;

    /// <summary>Jeden Frame aus <see cref="ComfortUi"/>.</summary>
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
            CrewStatsPlugin.Logger.LogError($"Komfort-Fehler (Spiel läuft normal weiter): {exc}");
        }
    }

    private static void TickInner()
    {
        Where = CurrentPlace();
        if (Where != _lastPlace)
        {
            if (Where == Place.Lobby) _nextStats = DateTime.UtcNow + TimeSpan.FromSeconds(2 + Jitter.NextDouble() * 3);
            if (Where == Place.Menu) _menuChecked = false;
            _lastPlace = Where;
        }
        RoundUploader.Tick();

        StatsReply? arrived;
        lock (Lock)
        {
            arrived = _arrived;
            _arrived = null;
        }
        if (arrived != null) RoundSummary.ApplyBot(arrived);

        var url = BotClient.PlayerUrl;
        if (Where == Place.Game || url == null)
        {
            if (Where == Place.Game) Lobby = Array.Empty<LobbyRow>();
            return;
        }

        var me = OwnFriendCode();
        var now = DateTime.UtcNow;
        if (now < _nextLook) return; // Lobby höchstens 4× pro Sekunde ansehen
        _nextLook = now + TimeSpan.FromMilliseconds(250);
        var players = Where == Place.Lobby ? LobbyPlayers() : new List<(string Name, string Code, bool Me)>();
        BuildRows(players);

        var wantOwn = CrewStatsPlugin.ShowOverlay.Value || CrewStatsPlugin.ShowRoundSummary.Value;
        var wantLobby = CrewStatsPlugin.ShowLobbyInfo.Value && Where == Place.Lobby;

        // Neue Spieler in der Lobby → bald nachfragen (mit Zufall, damit nicht alle gleichzeitig fragen)
        var set = string.Join(",", players.Select(p => p.Code).OrderBy(c => c));
        if (set != _playerSet)
        {
            _playerSet = set;
            if (wantLobby && players.Any(p => Stale(p.Code, now)))
            {
                var soon = now + TimeSpan.FromSeconds(3 + Jitter.NextDouble() * 9);
                if (soon < _nextStats) _nextStats = soon;
            }
        }

        // Rundenzusammenfassung: XP beim Bot nachfragen
        var summary = RoundSummary.Last;
        var summaryDue = summary != null && !summary.BotChecked && _summaryCheck < SummaryChecks.Length &&
                         (now - summary.EndedAt).TotalSeconds >= SummaryChecks[_summaryCheck];

        var due = summaryDue || now >= _nextStats;
        if (Where == Place.Menu)
        {
            // Im Hauptmenü nur einmal (für den Verbindungsstatus und die eigenen Stats)
            due = summaryDue || (!_menuChecked && wantOwn && now >= _nextStats && (_mine == null || now - _mineAt > PlayerStale));
        }
        if (!due || me == null || (!wantOwn && !wantLobby && !summaryDue)) return;

        lock (Lock)
        {
            if (_busy) return;
            _busy = true;
        }
        if (summaryDue) _summaryCheck++;
        if (Where == Place.Menu) _menuChecked = true;
        _nextStats = now + OwnRefresh;
        var ask = wantLobby ? players.Where(p => Stale(p.Code, now)).Select(p => p.Code).ToList() : new List<string>();
        Task.Run(() => FetchAsync(url, me, ask));
    }

    public static void OnRoundEnded()
    {
        _summaryCheck = 0;
    }

    private static bool Stale(string code, DateTime now)
    {
        lock (Lock) return !Cache.TryGetValue(code, out var entry) || now - entry.At > PlayerStale;
    }

    private static async Task FetchAsync(string url, string me, List<string> players)
    {
        try
        {
            var reply = await BotClient.AskAsync(url, PlayerInfoRules.StatsRequestContent(me, players), "CrewStats",
                PlayerInfoRules.ParseStats, ReplyTimeout);
            if (reply == null) return;
            var now = DateTime.UtcNow;
            lock (Lock)
            {
                _mine = reply;
                _mineAt = now;
                _arrived = reply;
                if (reply.Me != null) Cache[me] = (reply.Me, now);
                for (var i = 0; i < players.Count && i < reply.Players.Count; i++) Cache[players[i]] = (reply.Players[i], now);
            }
        }
        catch (Exception exc)
        {
            CrewStatsPlugin.Logger.LogWarning($"Stats-Anfrage fehlgeschlagen: {exc.Message}");
        }
        finally
        {
            lock (Lock) _busy = false;
        }
    }

    /// <summary>Code zum Verknüpfen holen (Knopf im Einstellungsmenü).</summary>
    public static void RequestLinkCode()
    {
        if (_linkBusy) return;
        var url = BotClient.PlayerUrl;
        if (url == null)
        {
            LinkText = "Keine Bot-URL eingetragen – im Discord /autojoin ausführen und die URL im Installer eintragen.";
            return;
        }
        var me = OwnFriendCode();
        if (me == null)
        {
            LinkText = "Kein Friendcode gefunden – bist du in Among Us angemeldet (kein Gast-Account)?";
            return;
        }
        _linkBusy = true;
        LinkText = "Frage den Bot nach einem Code …";
        LinkCode = "";
        Task.Run(async () =>
        {
            try
            {
                var reply = await BotClient.AskAsync(url, PlayerInfoRules.LinkRequestContent(me), "CrewStats",
                    PlayerInfoRules.ParseLink, ReplyTimeout);
                if (reply == null)
                {
                    LinkText = $"Kein Code: {ConnectionStatus.Current.Message}";
                }
                else if (!reply.Ok)
                {
                    LinkText = "Der Bot konnte keinen Code erzeugen (Friendcode unbekannt?).";
                }
                else
                {
                    LinkCode = reply.Code;
                    LinkExpires = DateTime.UtcNow + TimeSpan.FromMinutes(Math.Max(1, reply.Minutes));
                    LinkText = (reply.AlreadyLinked ? "Du bist schon verknüpft – mit dem Code kannst du es erneuern. " : "") +
                               $"Im Discord eingeben: /verknuepfen-code code:{reply.Code}  (gilt {Math.Max(1, reply.Minutes)} Minuten)";
                    lock (Lock) Cache.Remove(me); // nach dem Verknüpfen neu laden
                    _nextStats = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                    _menuChecked = false;
                }
            }
            catch (Exception exc)
            {
                LinkText = "Kein Code – Fehler beim Abruf.";
                CrewStatsPlugin.Logger.LogWarning($"Verknüpfungs-Code fehlgeschlagen: {exc.Message}");
            }
            finally
            {
                _linkBusy = false;
            }
        });
    }

    /// <summary>Eigene Stats sofort neu laden (Knopf im Overlay).</summary>
    public static void RefreshNow()
    {
        _nextStats = DateTime.UtcNow;
        _menuChecked = false;
        lock (Lock) Cache.Clear();
        _playerSet = "";
    }

    private static Place CurrentPlace()
    {
        var client = AmongUsClient.Instance;
        if (client == null) return Place.Menu;
        return client.GameState switch
        {
            InnerNetClient.GameStates.NotJoined => Place.Menu,
            InnerNetClient.GameStates.Started => Place.Game,
            _ => LobbyBehaviour.Instance != null ? Place.Lobby : Place.Game,
        };
    }

    /// <summary>Eigener Friendcode (klein geschrieben), null bei Gast-Accounts oder vor dem Login.</summary>
    public static string? OwnFriendCode()
    {
        string? code = null;
        try
        {
            var local = PlayerControl.LocalPlayer;
            if (local != null && local.Data != null) code = local.Data.FriendCode;
        }
        catch
        {
            // nicht in einer Lobby
        }
        if (string.IsNullOrWhiteSpace(code))
        {
            try
            {
                code = EOSManager.Instance != null ? EOSManager.Instance.FriendCode : null;
            }
            catch
            {
                code = null;
            }
        }
        return string.IsNullOrWhiteSpace(code) || !code.Contains('#') ? null : code.Trim().ToLowerInvariant();
    }

    private static List<(string Name, string Code, bool Me)> LobbyPlayers()
    {
        var result = new List<(string, string, bool)>();
        var data = GameData.Instance;
        if (data == null) return result;
        var localId = PlayerControl.LocalPlayer != null ? PlayerControl.LocalPlayer.PlayerId : (byte)255;
        foreach (var info in data.AllPlayers)
        {
            if (info == null || info.Disconnected) continue;
            var code = (info.FriendCode ?? "").Trim().ToLowerInvariant();
            result.Add((info.PlayerName ?? "?", code, info.PlayerId == localId));
        }
        return result;
    }

    private static void BuildRows(List<(string Name, string Code, bool Me)> players)
    {
        var rows = new List<LobbyRow>(players.Count);
        lock (Lock)
        {
            foreach (var p in players)
            {
                PlayerStats? stats = null;
                if (p.Code.Length > 0 && Cache.TryGetValue(p.Code, out var entry)) stats = entry.Stats;
                else if (p.Code.Length == 0) stats = new PlayerStats(false, 0, 0, 0); // Gast ohne Friendcode
                rows.Add(new LobbyRow(p.Name, p.Me, stats));
            }
        }
        Lobby = rows;
    }
}
