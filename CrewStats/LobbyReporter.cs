using System;
using System.Collections.Generic;
using System.Text.Json;
using InnerNet;

namespace CrewStats;

/// <summary>
/// Lobby-Ankündigung: meldet als Host den Zustand der eigenen Online-Lobby an den Bot
/// (offen + Spielerzahl, Runde läuft, geschlossen). Der Bot hält dazu eine Nachricht im
/// Codes-Channel aktuell und sperrt während der Runde das Wettbüro.
/// Spielerzahl-Änderungen werden gebündelt (höchstens alle 5 s), Zustandswechsel sofort gesendet.
/// Seit 1.9.0 mit Spielerliste (Name, Friendcode, Farbe) für die Live-Anzeige der Stats-Website – die Website zeigt
/// daraus nur Namen und Profil-Links, nie Friendcodes.
/// JSON-Format ↔ Bot: services/Lobby.kt (beide Seiten gemeinsam ändern!).
/// </summary>
internal static class LobbyReporter
{
    private sealed record Snapshot(string State, string Code, string Map, int Players, int Max, string Host,
        string Region, string Server, string RosterKey, List<RosterEntry> Roster);

    internal sealed record RosterEntry(string Name, string Fc, int Color);

    private const int MaxRoster = 20;

    private const double MinSecondsBetweenUpdates = 5;
    private static readonly LatestWebhookSender Sender = new("Lobby");

    private static Snapshot? _sent;
    private static DateTime _sentAt;
    private static string _sender = "";
    private static bool _errorLogged;

    /// <summary>Läuft jeden Frame (HudManager.Update – gibt es in Lobby und Runde).</summary>
    public static void Tick()
    {
        try
        {
            if (!CrewStatsPlugin.AnnounceLobby.Value) return;
            var now = Current();
            if (now == null || now == _sent) return;

            // record-Gleichheit vergleicht die Liste nur als Referenz → RosterKey
            if (_sent != null && now with { Roster = _sent.Roster } == _sent) return;
            var important = _sent == null || _sent.State != now.State || _sent.Code != now.Code;
            if (!important && (DateTime.UtcNow - _sentAt).TotalSeconds < MinSecondsBetweenUpdates) return;
            Send(now);
        }
        catch (Exception exc)
        {
            if (_errorLogged) return;
            _errorLogged = true;
            CrewStatsPlugin.Logger.LogError($"Lobby-Ankündigung-Fehler (Spiel läuft normal weiter): {exc}");
        }
    }

    /// <summary>Host verlässt die Lobby: Ankündigung als geschlossen markieren.</summary>
    public static void Close()
    {
        if (_sent == null) return;
        Send(_sent with { State = "closed", Players = 0, RosterKey = "", Roster = new List<RosterEntry>() });
        _sent = null;
    }

    private static Snapshot? Current()
    {
        var client = AmongUsClient.Instance;
        if (client == null || !client.AmHost || client.NetworkMode != NetworkModes.OnlineGame) return null;

        string state;
        if (client.GameState == InnerNetClient.GameStates.Joined) state = "open";
        else if (client.GameState == InnerNetClient.GameStates.Started) state = "started";
        else return null; // Endbildschirm o.ä. – nichts melden

        var options = GameOptionsManager.Instance?.CurrentGameOptions;
        var region = ServerManager.Instance?.CurrentRegion;
        var local = PlayerControl.LocalPlayer;
        if (local != null && local.Data != null && !string.IsNullOrEmpty(local.Data.FriendCode))
            _sender = local.Data.FriendCode;

        var roster = Roster();
        return new Snapshot(
            state,
            GameCode.IntToGameName(client.GameId) ?? "",
            options != null ? GameRecorder.MapName(options.MapId) : "?",
            GameData.Instance != null ? GameData.Instance.PlayerCount : 0,
            options?.MaxPlayers ?? 0,
            local != null && local.Data != null ? local.Data.PlayerName ?? "" : "",
            region?.Name ?? "",
            region != null ? MainAddress(region) : "",
            string.Join("|", roster.ConvertAll(entry => $"{entry.Name}/{entry.Fc}/{entry.Color}")),
            roster);
    }

    /// <summary>Wer in der Lobby ist (ohne getrennte Spieler), in der Reihenfolge des Spiels.</summary>
    private static List<RosterEntry> Roster()
    {
        var result = new List<RosterEntry>();
        var data = GameData.Instance;
        if (data == null) return result;
        foreach (var info in data.AllPlayers)
        {
            if (info == null || info.Disconnected) continue;
            var outfit = info.DefaultOutfit;
            result.Add(new RosterEntry(
                info.PlayerName ?? "?",
                (info.FriendCode ?? "").Trim().ToLowerInvariant(),
                outfit != null ? outfit.ColorId : -1));
            if (result.Count >= MaxRoster) break;
        }
        return result;
    }

    /// <summary>Adresse des ersten Servers der Region (sonst Ping-Server) – für Auto-Join (Lobby-Codes gelten nur dort).</summary>
    private static string MainAddress(IRegionInfo region)
    {
        var servers = region.Servers;
        if (servers != null)
            foreach (var server in servers)
                if (server != null && !string.IsNullOrEmpty(server.Ip)) return server.Ip;
        return region.PingServer ?? "";
    }

    private static void Send(Snapshot snapshot)
    {
        _sent = snapshot;
        _sentAt = DateTime.UtcNow;
        Sender.Send(JsonSerializer.Serialize(new
        {
            crewstats = "lobby",
            v = 1,
            state = snapshot.State,
            code = snapshot.Code,
            map = snapshot.Map,
            players = snapshot.Players,
            max = snapshot.Max,
            host = snapshot.Host,
            sender = _sender,
            region = snapshot.Region,
            server = snapshot.Server,
            list = snapshot.Roster.ConvertAll(entry => new { name = entry.Name, fc = entry.Fc, color = entry.Color }),
        }));
        CrewStatsPlugin.Logger.LogInfo($"Lobby: {snapshot.State} {snapshot.Code} ({snapshot.Players}/{snapshot.Max})");
    }
}
