using System;
using System.Text.Json;
using InnerNet;

namespace CrewStats;

/// <summary>
/// Lobby-Ankündigung: meldet als Host den Zustand der eigenen Online-Lobby an den Bot
/// (offen + Spielerzahl, Runde läuft, geschlossen). Der Bot hält dazu eine Nachricht im
/// Codes-Channel aktuell und sperrt während der Runde das Wettbüro.
/// Spielerzahl-Änderungen werden gebündelt (höchstens alle 5 s), Zustandswechsel sofort gesendet.
/// JSON-Format ↔ crewbot/services/lobby.py (beide Seiten gemeinsam ändern!).
/// </summary>
internal static class LobbyReporter
{
    private sealed record Snapshot(string State, string Code, string Map, int Players, int Max, string Host);

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
        Send(_sent with { State = "closed", Players = 0 });
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
        var local = PlayerControl.LocalPlayer;
        if (local != null && local.Data != null && !string.IsNullOrEmpty(local.Data.FriendCode))
            _sender = local.Data.FriendCode;

        return new Snapshot(
            state,
            GameCode.IntToGameName(client.GameId) ?? "",
            options != null ? GameRecorder.MapName(options.MapId) : "?",
            GameData.Instance != null ? GameData.Instance.PlayerCount : 0,
            options?.MaxPlayers ?? 0,
            local != null && local.Data != null ? local.Data.PlayerName ?? "" : "");
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
        }));
        CrewStatsPlugin.Logger.LogInfo($"Lobby: {snapshot.State} {snapshot.Code} ({snapshot.Players}/{snapshot.Max})");
    }
}
